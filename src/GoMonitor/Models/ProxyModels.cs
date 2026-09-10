using System.Text.Json.Serialization;

namespace GoMonitor.Models;

/// <summary>One proxied request, appended to the daily jsonl file.</summary>
public sealed record ProxyRequestRecord(
    DateTimeOffset Timestamp,
    string SessionId,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    bool HasError,
    string? ErrorMessage);

/// <summary>Aggregated in-memory statistics for the current day.</summary>
public sealed record ProxyDailyStats(
    int TotalRequests,
    int ErrorRequests,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheWriteTokens,
    IReadOnlyList<ProxyModelStat> Models);

public sealed record ProxyModelStat(
    string Model,
    int Requests,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens);

/// <summary>A single model entry from the upstream catalog.</summary>
public sealed record ModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long Created);

public sealed record ModelCatalogSnapshot(
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> ModelIds);

/// <summary>Result of comparing the fresh catalog against the stored snapshot.</summary>
public sealed record ModelCatalogDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;
}
