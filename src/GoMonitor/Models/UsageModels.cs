namespace GoMonitor.Models;

public sealed record UsageWindow(string Status, double? Percent, DateTimeOffset? ResetsAt)
{
    public bool IsAvailable =>
        string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase) && Percent is not null;
}

public sealed record UsageSnapshot(
    UsageWindow Rolling,
    UsageWindow Weekly,
    UsageWindow Monthly,
    DateTimeOffset FetchedAt,
    bool HasError,
    string? ErrorMessage)
{
    public static UsageSnapshot Error(DateTimeOffset fetchedAt, string message) =>
        new(
            new UsageWindow("error", null, null),
            new UsageWindow("error", null, null),
            new UsageWindow("error", null, null),
            fetchedAt,
            true,
            message);

    public UsageSnapshot WithError(DateTimeOffset fetchedAt, string message) =>
        this with { FetchedAt = fetchedAt, HasError = true, ErrorMessage = message };
}
