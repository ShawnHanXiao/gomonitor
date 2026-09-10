namespace GoMonitor.Models;

public sealed class AppSettings
{
    public string? ApiKeyOverride { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
    public bool StartWithWindows { get; set; } = true;

    public bool ProxyEnabled { get; set; }
    public int ProxyPort { get; set; } = 9355;
    public string ProxyUserAgent { get; set; } = "opencode/1.18.29 cli";
    public int SessionIdleHours { get; set; } = 6;
    public string UpstreamProto { get; set; } = "https";
    public string UpstreamHost { get; set; } = "opencode.ai";

    public int EffectivePollIntervalSeconds =>
        PollIntervalSeconds is >= 10 and <= 300 ? PollIntervalSeconds : 60;

    public int EffectiveProxyPort =>
        ProxyPort is >= 1024 and <= 65535 ? ProxyPort : 9355;

    public int EffectiveSessionIdleHours =>
        SessionIdleHours is >= 1 and <= 168 ? SessionIdleHours : 6;
}
