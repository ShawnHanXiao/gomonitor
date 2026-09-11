namespace GoMonitor.Services;

public static class ThresholdRule
{
    public const string Blue = "#3FC6F5";
    public const string Yellow = "#FFDD2E";
    public const string Red = "#F5483F";
    public const string Gray = "#8B949E";

    // 蓝 < 60% · 黄 60–85% · 红 ≥ 85% · 灰 = 不可用/错误
    public static string ColorFor(double? percent) => percent switch
    {
        null => Gray,
        < 60 => Blue,
        < 85 => Yellow,
        _ => Red
    };
}
