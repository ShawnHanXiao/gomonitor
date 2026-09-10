using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using GoMonitor.Models;
using GoMonitor.Services;
using Xunit;

namespace GoMonitor.Tests;

/// <summary>
/// End-to-end smoke test: proxy -> stub upstream, verifying header injection,
/// model alias rewriting, streaming passthrough and usage recording.
/// </summary>
public class ProxyEndToEndTests : IDisposable
{
    private readonly HttpListener _stub = new();
    private readonly List<OpenCodeProxyService> _proxies = new();
    private readonly string _usageDir = Path.Combine(
        Path.GetTempPath(), "gomonitor-tests", Guid.NewGuid().ToString("N"));

    public ProxyEndToEndTests()
    {
        var port = 19091;
        while (true)
        {
            try
            {
                _stub.Prefixes.Add($"http://localhost:{port}/");
                _stub.Start();
                _stubPort = port;
                break;
            }
            catch (HttpListenerException)
            {
                _stub.Prefixes.Clear();
                port++;
                if (port > 19100)
                {
                    throw;
                }
            }
        }

        _ = Task.Run(AcceptStubAsync);
    }

    private int _stubPort;

    private async Task AcceptStubAsync()
    {
        while (_stub.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _stub.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }

            var request = context.Request;
            var body = await new StreamReader(request.InputStream).ReadToEndAsync();

            var headers = string.Join(
                "\n",
                request.Headers.AllKeys.Select(k => $"{k}={request.Headers[k]}"));

            var response = context.Response;
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            var payload =
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7," +
                $"\"prompt_tokens_details\":{{\"cached_tokens\":9}}}}\n\n" +
                "data: [DONE]\n\n" +
                $"<!-- {headers} | BODY {body} -->";
            var bytes = Encoding.UTF8.GetBytes(payload);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.OutputStream.Close();
        }
    }

    [Fact]
    public async Task ProxyInjectsHeadersRewritesModelAndRecordsUsage()
    {
        var port = 19081;
        var settings = new AppSettings
        {
            ProxyEnabled = true,
            ProxyPort = port,
            UpstreamProto = "http",
            UpstreamHost = $"localhost:{_stubPort}",
        };
        var (svc, recorder, sessions) = StartProxyOnFreePort(settings, ref port);

        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{svc.BaseUrl}/chat/completions")
        {
            Content = new StringContent(
                "{\"model\":\"proxy-glm-5.3-flash\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("Authorization", "Bearer test-key");

        var response = await client.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"status={response.StatusCode} err={svc.LastRequestError} body={content}");
        Assert.True(svc.LastRequestError is null, svc.LastRequestError);
        Assert.Contains("\"model\":\"glm-5.3-flash\"", content); // alias stripped in forwarded body
        Assert.Contains("x-opencode-session=", content);
        Assert.Contains("x-opencode-request=msg_1", content);
        Assert.Contains("x-opencode-client=cli", content);
        Assert.Contains("x-opencode-project=global", content);
        Assert.Contains("opencode/1.18.29 cli", content);
    }

    [Fact]
    public async Task ProxyKeepsStableSessionForSameConversation()
    {
        var port = 19082;
        var settings = new AppSettings
        {
            ProxyEnabled = true,
            ProxyPort = port,
            UpstreamProto = "http",
            UpstreamHost = $"localhost:{_stubPort}",
        };
        var (svc, recorder, sessions) = StartProxyOnFreePort(settings, ref port);

        using var client = new HttpClient();
        const string body = "{\"model\":\"proxy-glm-5.3-flash\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";

        for (var i = 0; i < 3; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{svc.BaseUrl}/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Authorization", "Bearer test-key");
            await client.SendAsync(request);
        }

        Assert.Equal(1, sessions.ActiveSessionCount);
        Assert.Equal(3, recorder.Snapshot().TotalRequests);
        Assert.Equal(33, recorder.Snapshot().TotalInputTokens); // 11 x 3
        Assert.Equal(21, recorder.Snapshot().TotalOutputTokens); // 7 x 3
        Assert.Equal(27, recorder.Snapshot().TotalCacheReadTokens); // 9 x 3
        Assert.Equal("glm-5.3-flash", recorder.Snapshot().Models.Single().Model);
    }

    public void Dispose()
    {
        _stub.Close();
        foreach (var proxy in _proxies)
        {
            proxy.Dispose();
        }

        try
        {
            Directory.Delete(_usageDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (OpenCodeProxyService, ProxyUsageRecorder, SessionRegistry) StartProxyOnFreePort(AppSettings settings, ref int port)
    {
        var recorder = new ProxyUsageRecorder(_usageDir);
        var sessions = new SessionRegistry(TimeSpan.FromHours(6));
        var proxy = new OpenCodeProxyService(sessions, recorder);
        _proxies.Add(proxy);

        while (true)
        {
            settings.ProxyPort = port;
            if (proxy.Start(settings, "test-key"))
            {
                break;
            }

            port++;
            if (port > 19100)
            {
                throw new InvalidOperationException("No free port for proxy test.");
            }
        }

        return (proxy, recorder, sessions);
    }
}
