using GoMonitor.Services;
using Xunit;

namespace GoMonitor.Tests;

public class UsageScanTests
{
    [Fact]
    public void ParsesOpenAiCompleteUsage()
    {
        const string json = """
            {
              "id": "chatcmpl-1",
              "choices": [{"message": {"role": "assistant", "content": "hi"}}],
              "usage": {
                "prompt_tokens": 120,
                "completion_tokens": 45,
                "prompt_tokens_details": {"cached_tokens": 100}
              }
            }
            """;

        var usage = UsageParser.Parse(json);

        Assert.NotNull(usage);
        Assert.Equal(120, usage!.InputTokens);
        Assert.Equal(45, usage.OutputTokens);
        Assert.Equal(100, usage.CacheReadTokens);
    }

    [Fact]
    public void ParsesAnthropicMessageStartUsage()
    {
        const string json = """
            {"type":"message_start","message":{"usage":{"input_tokens":25,"cache_read_input_tokens":10,"cache_creation_input_tokens":5,"output_tokens":1}}}
            """;

        var usage = UsageParser.Parse(json);

        Assert.NotNull(usage);
        Assert.Equal(25, usage!.InputTokens);
        Assert.Equal(10, usage.CacheReadTokens);
        Assert.Equal(5, usage.CacheWriteTokens);
    }

    [Fact]
    public void ScannerMergesAnthropicStartAndDelta()
    {
        var scanner = new UsageStreamScanner();
        scanner.Feed("""data: {"type":"message_start","message":{"usage":{"input_tokens":30,"cache_read_input_tokens":20,"output_tokens":1}}}""");
        scanner.Feed("\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":null},\"usage\":{\"output_tokens\":87}}\n\n");

        var usage = scanner.BuildResult();

        Assert.NotNull(usage);
        Assert.Equal(30, usage!.InputTokens);
        Assert.Equal(87, usage.OutputTokens);
        Assert.Equal(20, usage.CacheReadTokens);
    }

    [Fact]
    public void ScannerHandlesUsageSplitAcrossChunks()
    {
        var scanner = new UsageStreamScanner();
        scanner.Feed("data: {\"choices\":[],\"us");
        scanner.Feed("age\":{\"prompt_to");
        scanner.Feed("kens\":7,\"completion_tokens\":3}}\n\ndata: [DONE]");

        var usage = scanner.BuildResult();

        Assert.NotNull(usage);
        Assert.Equal(7, usage!.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
    }

    [Fact]
    public void ScannerIgnoresContentWithoutUsage()
    {
        var scanner = new UsageStreamScanner();
        scanner.Feed("""data: {"choices":[{"delta":{"content":"hello \"usage\" fake"}}]}""");

        Assert.Null(scanner.BuildResult());
    }

    [Fact]
    public void ScannerHandlesMultipleUsageObjects()
    {
        var scanner = new UsageStreamScanner();
        scanner.Feed("""data: {"usage":{"input_tokens":5,"output_tokens":1}}""");
        scanner.Feed("""data: {"usage":{"input_tokens":5,"output_tokens":9}}""");

        var usage = scanner.BuildResult();

        Assert.Equal(5, usage!.InputTokens);
        Assert.Equal(9, usage.OutputTokens);
    }
}

public class ModelDiffTests
{
    [Fact]
    public void DetectsAddedAndRemovedModels()
    {
        var diff = ModelCatalogService.ComputeDiff(
            new[] { "glm-5.3", "kimi-k3", "new-model" },
            new[] { "glm-5.3", "kimi-k3", "old-model" });

        Assert.Equal(new[] { "new-model" }, diff.Added);
        Assert.Equal(new[] { "old-model" }, diff.Removed);
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void NoChangesYieldsEmptyDiff()
    {
        var diff = ModelCatalogService.ComputeDiff(
            new[] { "a", "b" },
            new[] { "b", "a" });

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.False(diff.HasChanges);
    }
}
