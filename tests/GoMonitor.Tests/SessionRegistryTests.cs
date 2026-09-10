using GoMonitor.Services;
using Xunit;

namespace GoMonitor.Tests;

public class SessionRegistryTests
{
    private const string Body = """
        {
          "model": "proxy-glm-5.3-flash",
          "messages": [
            { "role": "system", "content": "You are a coding agent." },
            { "role": "user", "content": "Fix the bug in foo.py" }
          ]
        }
        """;

    [Fact]
    public void SameConversationHashYieldsStableSession()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(6));
        var hash = ProxyBodyHelper.ComputeSessionHash(Body);

        var (first, firstIndex) = registry.Resolve(null, hash);
        var (second, secondIndex) = registry.Resolve(null, hash);

        Assert.Equal(first, second);
        Assert.Equal(1, firstIndex);
        Assert.Equal(2, secondIndex);
    }

    [Fact]
    public void DifferentConversationGetsDifferentSession()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(6));
        var hashA = ProxyBodyHelper.ComputeSessionHash(Body);
        var hashB = ProxyBodyHelper.ComputeSessionHash(Body.Replace("foo.py", "bar.py"));

        var (sessionA, _) = registry.Resolve(null, hashA);
        var (sessionB, _) = registry.Resolve(null, hashB);

        Assert.NotEqual(sessionA, sessionB);
    }

    [Fact]
    public void IdleWindowExpiryStartsFreshSession()
    {
        var registry = new SessionRegistry(TimeSpan.FromTicks(1));
        var hash = ProxyBodyHelper.ComputeSessionHash(Body);

        var (first, _) = registry.Resolve(null, hash);
        var (second, _) = registry.Resolve(null, hash);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ManualResetStartsNewSession()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(6));
        var hash = ProxyBodyHelper.ComputeSessionHash(Body);

        var (first, _) = registry.Resolve(null, hash);
        registry.Reset();
        var (second, index) = registry.Resolve(null, hash);

        Assert.NotEqual(first, second);
        Assert.Equal(1, index);
    }

    [Fact]
    public void ExplicitSessionIdWinsAndCountsRequests()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(6));

        var (first, firstIndex) = registry.Resolve("AB12CD34", null);
        var (second, secondIndex) = registry.Resolve("AB12CD34", null);

        Assert.Equal("AB12CD34", first);
        Assert.Equal("AB12CD34", second);
        Assert.Equal(1, firstIndex);
        Assert.Equal(2, secondIndex);
    }

    [Fact]
    public void SessionIdFormatMatchesClientStyle()
    {
        for (var i = 0; i < 50; i++)
        {
            var id = SessionRegistry.NewSessionId();
            Assert.Equal(8, id.Length);
            Assert.All(id, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        }
    }
}

public class ProxyBodyHelperTests
{
    [Fact]
    public void ExtractModelReadsModelProperty()
    {
        const string body = """{"model":"proxy-glm-5.3-flash","messages":[]}""";
        Assert.Equal("proxy-glm-5.3-flash", ProxyBodyHelper.ExtractModel(body));
    }

    [Fact]
    public void ExtractModelReturnsNullForNonJson()
    {
        Assert.Null(ProxyBodyHelper.ExtractModel("not json"));
        Assert.Null(ProxyBodyHelper.ExtractModel(null));
    }

    [Fact]
    public void RewriteModelReplacesModelProperty()
    {
        const string body = """{"model":"proxy-glm-5.3-flash","stream":true,"messages":[{"role":"user","content":"hi"}]}""";
        var rewritten = ProxyBodyHelper.RewriteModel(body, "glm-5.3-flash");

        Assert.Equal("glm-5.3-flash", ProxyBodyHelper.ExtractModel(rewritten));
        Assert.Contains("\"stream\":true", rewritten);
    }

    [Fact]
    public void RewriteModelKeepsOriginalWhenNotJson()
    {
        const string body = "not json";
        Assert.Same(body, ProxyBodyHelper.RewriteModel(body, "x"));
    }

    [Theory]
    [InlineData("proxy-glm-5.3-flash", "glm-5.3-flash", true)]
    [InlineData("PROXY-glm-5.3-flash", "glm-5.3-flash", true)]
    [InlineData("glm-5.3-flash", "glm-5.3-flash", false)]
    [InlineData(null, "", false)]
    public void StripAliasPrefixHandlesPrefix(string? input, string expected, bool wasRewritten)
    {
        var (model, rewritten) = ProxyBodyHelper.StripAliasPrefix(input);
        Assert.Equal(expected, model);
        Assert.Equal(wasRewritten, rewritten);
    }

    [Fact]
    public void SessionHashIsStableAndContentSensitive()
    {
        var a = ProxyBodyHelper.ComputeSessionHash("""{"messages":[{"role":"system","content":"s"},{"role":"user","content":"u"}]}""");
        var b = ProxyBodyHelper.ComputeSessionHash("""{"messages":[{"role":"system","content":"s"},{"role":"user","content":"u"}]}""");
        var c = ProxyBodyHelper.ComputeSessionHash("""{"messages":[{"role":"system","content":"s"},{"role":"user","content":"v"}]}""");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void SessionHashNullForBodyWithoutMessages()
    {
        Assert.Null(ProxyBodyHelper.ComputeSessionHash("""{"model":"x"}"""));
        Assert.Null(ProxyBodyHelper.ComputeSessionHash(null));
    }

    [Fact]
    public void SessionHashWorksWithAnthropicSystemAndContentBlocks()
    {
        var a = ProxyBodyHelper.ComputeSessionHash("""
            {
              "system": [{"type":"text","text":"be brief"}],
              "messages": [
                {"role":"user","content":[{"type":"text","text":"hello"}]}
              ]
            }
            """);

        Assert.NotNull(a);
    }
}
