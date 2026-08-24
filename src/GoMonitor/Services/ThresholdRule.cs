namespace GoMonitor.Services;

public static class ThresholdRule
{
    public const string Green = "#3FB950";
    public const string Yellow = "#E3B341";
    public const string Orange = "#F0883E";
    public const string Red = "#E5484D";
    public const string Gray = "#8B949E";

    public static string ColorFor(double? percent) => percent switch
    {
        null => Gray,
        < 60 => Green,
        < 80 => Yellow,
        < 95 => Orange,
        _ => Red
    };
}
