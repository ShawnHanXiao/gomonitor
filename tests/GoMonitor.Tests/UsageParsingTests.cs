using GoMonitor.Models;
using GoMonitor.Services;

namespace GoMonitor.Tests;

public class UsageParsingTests
{
    [Fact]
    public void Parse_ValidResponse_PopulatesAllWindows()
    {
        var json = """
        {
          "usage": {
            "rolling": { "status": "ok", "percent": 13.5, "resetsAt": "2026-08-18T22:00:00Z" },
            "weekly": { "status": "ok", "percent": 42, "resetsAt": "2026-08-23T00:00:00Z" },
            "monthly": { "status": "ok", "percent": 8, "resetsAt": "2026-08-31T00:00:00Z" }
          }
        }
        """;

        var snapshot = OpenCodeUsageService.Parse(json, DateTimeOffset.Now);

        Assert.False(snapshot.HasError);
        Assert.True(snapshot.Rolling.IsAvailable);
        Assert.Equal(13.5, snapshot.Rolling.Percent);
        Assert.Equal("ok", snapshot.Rolling.Status);
        Assert.Equal(42, snapshot.Weekly.Percent);
        Assert.Equal(8, snapshot.Monthly.Percent);
        Assert.NotNull(snapshot.Rolling.ResetsAt);
    }

    [Fact]
    public void Parse_MissingWindows_AreUnavailable()
    {
        var json = """{ "usage": { "rolling": { "status": "ok", "percent": 10, "resetsAt": null } } }""";

        var snapshot = OpenCodeUsageService.Parse(json, DateTimeOffset.Now);

        Assert.False(snapshot.HasError);
        Assert.True(snapshot.Rolling.IsAvailable);
        Assert.False(snapshot.Weekly.IsAvailable);
        Assert.False(snapshot.Monthly.IsAvailable);
        Assert.Null(snapshot.Weekly.Percent);
    }

    [Fact]
    public void Parse_NonOkStatus_IsUnavailable()
    {
        var json = """
        {
          "usage": {
            "rolling": { "status": "error", "percent": 95, "resetsAt": null },
            "weekly": { "status": "ok", "percent": 30, "resetsAt": null },
            "monthly": { "status": "ok", "percent": 5, "resetsAt": null }
          }
        }
        """;

        var snapshot = OpenCodeUsageService.Parse(json, DateTimeOffset.Now);

        Assert.False(snapshot.HasError);
        Assert.False(snapshot.Rolling.IsAvailable);
        Assert.True(snapshot.Weekly.IsAvailable);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsErrorSnapshot()
    {
        var snapshot = OpenCodeUsageService.Parse("{ not json", DateTimeOffset.Now);

        Assert.True(snapshot.HasError);
        Assert.False(snapshot.Rolling.IsAvailable);
        Assert.NotNull(snapshot.ErrorMessage);
    }

    [Fact]
    public void WithError_PreservesLastPercentagesAndMarksError()
    {
        var json = """
        {
          "usage": {
            "rolling": { "status": "ok", "percent": 42, "resetsAt": null },
            "weekly": { "status": "ok", "percent": 19, "resetsAt": null },
            "monthly": { "status": "ok", "percent": 9, "resetsAt": null }
          }
        }
        """;
        var ok = OpenCodeUsageService.Parse(json, DateTimeOffset.Now);

        var error = ok.WithError(DateTimeOffset.Now, "HTTP 500");

        Assert.True(error.HasError);
        Assert.Equal("HTTP 500", error.ErrorMessage);
        Assert.Equal(42, error.Rolling.Percent);
        Assert.Equal(19, error.Weekly.Percent);
        Assert.Equal(9, error.Monthly.Percent);
        Assert.True(error.Rolling.IsAvailable);
    }
}
