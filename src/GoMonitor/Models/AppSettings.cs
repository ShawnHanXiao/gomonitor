namespace GoMonitor.Models;

public sealed class AppSettings
{
    public string? ApiKeyOverride { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
    public bool StartWithWindows { get; set; } = true;

    public int EffectivePollIntervalSeconds =>
        PollIntervalSeconds is >= 10 and <= 300 ? PollIntervalSeconds : 60;
}
