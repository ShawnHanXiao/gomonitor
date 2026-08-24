using System.IO;
using GoMonitor.Models;
using GoMonitor.Services;

namespace GoMonitor.Tests;

public class SettingsTests
{
    [Fact]
    public void NewSettings_HasExpectedDefaults()
    {
        var settings = new AppSettings();

        Assert.Null(settings.ApiKeyOverride);
        Assert.Equal(60, settings.PollIntervalSeconds);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(60, settings.EffectivePollIntervalSeconds);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-5, 60)]
    [InlineData(301, 60)]
    [InlineData(10, 10)]
    [InlineData(120, 120)]
    [InlineData(300, 300)]
    public void EffectivePollInterval_ClampsInvalidValues(int value, int expected)
    {
        var settings = new AppSettings { PollIntervalSeconds = value };

        Assert.Equal(expected, settings.EffectivePollIntervalSeconds);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gomonitor-settings-{Guid.NewGuid():N}.json");
        try
        {
            var service = new SettingsService(path);
            service.Save(new AppSettings
            {
                ApiKeyOverride = "test-key",
                PollIntervalSeconds = 30,
                StartWithWindows = false
            });

            var loaded = service.Load();

            Assert.Equal("test-key", loaded.ApiKeyOverride);
            Assert.Equal(30, loaded.PollIntervalSeconds);
            Assert.False(loaded.StartWithWindows);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
