using GoMonitor.Services;

namespace GoMonitor.Tests;

public class ThresholdColorTests
{
    [Theory]
    [InlineData(0, ThresholdRule.Blue)]
    [InlineData(59.9, ThresholdRule.Blue)]
    [InlineData(60, ThresholdRule.Yellow)]
    [InlineData(84.9, ThresholdRule.Yellow)]
    [InlineData(85, ThresholdRule.Red)]
    [InlineData(100, ThresholdRule.Red)]
    public void ColorFor_Boundaries_MapCorrectly(double percent, string expected)
    {
        Assert.Equal(expected, ThresholdRule.ColorFor(percent));
    }

    [Fact]
    public void ColorFor_Null_IsGray()
    {
        Assert.Equal(ThresholdRule.Gray, ThresholdRule.ColorFor(null));
    }
}
