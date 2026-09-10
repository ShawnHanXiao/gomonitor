using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using GoMonitor.Models;

namespace GoMonitor.Services;

/// <summary>
/// Local reverse proxy between Trae and OpenCode Go. Injects the session headers
/// required by the upstream, rewrites "proxy-" model aliases, streams responses
/// through, and records usage tokens seen in responses.
/// </summary>
public sealed class OpenCodeProxyService : IDisposable
{
    private static readonly string[] RequestHeadersToSkip =
    {
        "Host", "Content-Length", "Connection", "Keep-Alive", "Transfer-Encoding",
        "Upgrade", "Proxy-Connection", "Proxy-Authorization", "Expect", "TE", "Trailer",
        "X-Opencode-Session", "X-Opencode-Request", "X-Opencode-Client", "X-Opencode-Project",
        "User-Agent", "Authorization",
    };

    private static readonly string[] ResponseHeadersToSkip =
    {
        "Transfer-Encoding", "Content-Length", "Connection", "Keep-Alive",
    };

    private readonly object _startLock = new();
    private readonly HttpClient _upstream;
    private readonly SessionRegistry _sessions;
    private readonly ProxyUsageRecorder _recorder;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public OpenCodeProxyService(SessionRegistry sessions, ProxyUsageRecorder recorder)
    {
        _sessions = sessions;
        _recorder = recorder;
        _upstream = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    public bool IsRunning => _listener is { IsListening: true };

    /// <summary>Base URL to configure in Trae, e.g. http://localhost:9355/zen/go/v1.</summary>
    public string? BaseUrl { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Detail of the most recent per-request failure (diagnostics/testing).</summary>
    public string? LastRequestError { get; private set; }

    public event EventHandler? StateChanged;

    public event EventHandler? StatsUpdated;

    /// <summary>Starts listening. Returns false and sets LastError when the port is unavailable.</summary>
    public bool Start(AppSettings settings, string? apiKey)
    {
        lock (_startLock)
        {
            if (IsRunning)
            {
                return true;
            }

            LastError = null;
            _cts = new CancellationTokenSource();
            var port = settings.EffectiveProxyPort;

            // http.sys only allows non-admin bindings for localhost-style hosts;
            // try the numeric loopback first, then fall back to "localhost".
            foreach (var host in new[] { "127.0.0.1", "localhost" })
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://{host}:{port}/zen/go/v1/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    BaseUrl = $"http://{host}:{port}/zen/go/v1";
                    _ = AcceptLoopAsync(settings, apiKey, _cts.Token);
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                }
            }

            _cts.Dispose();
            _cts = null;
            LastError = $"Port {port} is unavailable (bind denied or in use).";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    public void Stop()
    {
        lock (_startLock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _listener?.Close();
            _listener = null;
            BaseUrl = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ResetSessions() => _sessions.Reset();

    public void Dispose()
    {
        Stop();
        _upstream.Dispose();
    }

    private async Task AcceptLoopAsync(AppSettings settings, string? apiKey, CancellationToken token)
    {
        var listener = _listener!;
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context, settings, apiKey, token), token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, AppSettings settings, string? apiKey, CancellationToken token)
    {
        var request = context.Request;
        var response = context.Response;
        var startedAt = DateTimeOffset.Now;
        var model = string.Empty;
        var sessionId = string.Empty;

        try
        {
            var body = await ReadRequestBodyAsync(request, token).ConfigureAwait(false);
            var rewritten = ProxyBodyHelper.StripAliasPrefix(ProxyBodyHelper.ExtractModel(body));
            model = rewritten.ActualModel;
            if (rewritten.WasRewritten && body is not null)
            {
                body = ProxyBodyHelper.RewriteModel(body, model);
            }

            var hash = ProxyBodyHelper.ComputeSessionHash(body);
            var explicitSession = request.Headers["x-opencode-session"];
            (sessionId, var requestIndex) = _sessions.Resolve(explicitSession, hash);

            using var upstreamRequest = BuildUpstreamRequest(request, settings, body, sessionId, requestIndex, apiKey);
            using var upstreamResponse = await _upstream
                .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            CopyResponseHeaders(upstreamResponse, response);
            response.StatusCode = (int)upstreamResponse.StatusCode;
            response.SendChunked = true;

            var (tokens, hadError) = await PumpResponseBodyAsync(upstreamResponse, response, token)
                .ConfigureAwait(false);
            Record(startedAt, sessionId, model, tokens, hadError, null);
        }
        catch (Exception ex)
        {
            LastRequestError = ex.ToString();
            TryRespondBadGateway(response, ex.Message);
            Record(startedAt, sessionId, model, null, true, ex.Message);
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // Client already gone.
            }
        }
    }

    private static async Task<string?> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken token)
    {
        if (!request.HasEntityBody)
        {
            return null;
        }

        using var reader = new StreamReader(
            request.InputStream,
            request.ContentEncoding ?? Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192,
            leaveOpen: true);
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }

    private static HttpRequestMessage BuildUpstreamRequest(
        HttpListenerRequest request,
        AppSettings settings,
        string? body,
        string sessionId,
        int requestIndex,
        string? apiKey)
    {
        var uri = new Uri($"{settings.UpstreamProto.TrimEnd('/')}://{settings.UpstreamHost}{request.Url!.PathAndQuery}");
        var upstream = new HttpRequestMessage(new HttpMethod(request.HttpMethod), uri);

        foreach (var key in request.Headers.AllKeys)
        {
            if (key is null || RequestHeadersToSkip.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                upstream.Headers.TryAddWithoutValidation(key, request.Headers[key]);
            }
            catch (InvalidOperationException)
            {
                // Skip malformed header names/values.
            }
        }

        upstream.Headers.TryAddWithoutValidation("x-opencode-session", sessionId);
        upstream.Headers.TryAddWithoutValidation("x-opencode-request", $"msg_{requestIndex}");
        upstream.Headers.TryAddWithoutValidation("x-opencode-client", "cli");
        upstream.Headers.TryAddWithoutValidation("x-opencode-project", "global");
        upstream.Headers.TryAddWithoutValidation("User-Agent", settings.ProxyUserAgent);

        var auth = request.Headers["Authorization"];
        upstream.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            !string.IsNullOrWhiteSpace(auth) ? auth["Bearer ".Length..].Trim() : apiKey);

        if (body is not null)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.TryAddWithoutValidation(
                "Content-Type",
                request.ContentType ?? "application/json");
            upstream.Content = content;
        }

        return upstream;
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpListenerResponse target)
    {
        foreach (var header in source.Headers)
        {
            if (ResponseHeadersToSkip.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                target.Headers[header.Key] = string.Join(", ", header.Value);
            }
            catch (ArgumentException)
            {
                // Some headers are restricted on HttpListenerResponse; skip them.
            }
        }

        if (source.Content is not null)
        {
            target.ContentType = source.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        }
    }

    /// <summary>Streams the upstream body to the client while scanning for usage tokens.</summary>
    private static async Task<(UsageTokens? Tokens, bool HadError)> PumpResponseBodyAsync(
        HttpResponseMessage upstreamResponse,
        HttpListenerResponse response,
        CancellationToken token)
    {
        if (upstreamResponse.Content is null)
        {
            return (null, false);
        }

        var encoding = GetEncoding(upstreamResponse.Content.Headers.ContentType?.CharSet);
        var scanner = new UsageStreamScanner();
        var decoder = encoding.GetDecoder();

        await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[8192];
        var charBuffer = new char[8192];
        while (true)
        {
            var read = await upstreamStream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var chars = decoder.GetChars(buffer, 0, read, charBuffer, 0);
            if (chars > 0)
            {
                scanner.Feed(new string(charBuffer, 0, chars));
            }

            await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(token).ConfigureAwait(false);
        }

        return (scanner.BuildResult(), false);
    }

    private void TryRespondBadGateway(HttpListenerResponse response, string message)
    {
        try
        {
            response.StatusCode = 502;
            response.ContentType = "text/plain; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes($"[GoMonitor proxy] upstream error: {message}");
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // Response already closed by the client.
        }
    }

    private void Record(
        DateTimeOffset startedAt,
        string sessionId,
        string model,
        UsageTokens? tokens,
        bool hasError,
        string? errorMessage)
    {
        _recorder.Record(new ProxyRequestRecord(
            startedAt,
            sessionId,
            model,
            tokens?.InputTokens ?? 0,
            tokens?.OutputTokens ?? 0,
            tokens?.CacheReadTokens ?? 0,
            tokens?.CacheWriteTokens ?? 0,
            hasError,
            errorMessage));
        StatsUpdated?.Invoke(this, EventArgs.Empty);
    }

    private static Encoding GetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
