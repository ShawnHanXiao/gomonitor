using System.IO;
using GoMonitor.Models;
using GoMonitor.Services;
using Xunit;

namespace GoMonitor.Tests;

public class ProxyUsageRecorderTests : IDisposable
{
    private readonly string _directory;

    public ProxyUsageRecorderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "gomonitor-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RecordAggregatesRequestsAndTokens()
    {
        var recorder = new ProxyUsageRecorder(_directory);
        recorder.Record(new ProxyRequestRecord(DateTimeOffset.Now, "S1", "glm-5.3", 100, 20, 80, 5, false, null));
        recorder.Record(new ProxyRequestRecord(DateTimeOffset.Now, "S1", "glm-5.3", 50, 10, 0, 0, false, null));
        recorder.Record(new ProxyRequestRecord(DateTimeOffset.Now, "S2", "kimi-k3", 10, 2, 0, 0, true, "boom"));

        var stats = recorder.Snapshot();

        Assert.Equal(3, stats.TotalRequests);
        Assert.Equal(1, stats.ErrorRequests);
        Assert.Equal(160, stats.TotalInputTokens);
        Assert.Equal(32, stats.TotalOutputTokens);
        Assert.Equal(80, stats.TotalCacheReadTokens);
        var glm = Assert.Single(stats.Models, m => m.Model == "glm-5.3");
        Assert.Equal(2, glm.Requests);
        Assert.Contains(recorder.RecentRequests(), r => r.HasError);
    }

    [Fact]
    public void LoadTodayRestoresAggregateFromDisk()
    {
        var recorder = new ProxyUsageRecorder(_directory);
        recorder.Record(new ProxyRequestRecord(DateTimeOffset.Now, "S1", "glm-5.3", 100, 20, 80, 0, false, null));
        recorder.Record(new ProxyRequestRecord(DateTimeOffset.Now, "S1", "glm-5.3", 40, 8, 0, 0, false, null));

        var reloaded = new ProxyUsageRecorder(_directory);
        reloaded.LoadToday();
        var stats = reloaded.Snapshot();

        Assert.Equal(2, stats.TotalRequests);
        Assert.Equal(140, stats.TotalInputTokens);
        Assert.Equal(28, stats.TotalOutputTokens);
    }
}

public class AppSettingsProxyTests
{
    [Fact]
    public void EffectiveValuesClampOutOfRangeInput()
    {
        var settings = new AppSettings { ProxyPort = 80, SessionIdleHours = 0 };

        Assert.Equal(9355, settings.EffectiveProxyPort);
        Assert.Equal(6, settings.EffectiveSessionIdleHours);
    }

    [Fact]
    public void EffectiveValuesAcceptValidInput()
    {
        var settings = new AppSettings { ProxyPort = 12345, SessionIdleHours = 12 };

        Assert.Equal(12345, settings.EffectiveProxyPort);
        Assert.Equal(12, settings.EffectiveSessionIdleHours);
    }
}
