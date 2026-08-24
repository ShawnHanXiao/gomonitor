using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GoMonitor.Models;
using GoMonitor.Services;

using WpfBrush = System.Windows.Media.Brush;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace GoMonitor.Views;

public partial class MonitorWindow : Window
{
    private static readonly WpfBrush TextBrush = CreateBrush("#E6EDF3");
    private static readonly WpfBrush MutedBrush = CreateBrush("#8B949E");

    public MonitorWindow()
    {
        InitializeComponent();
        // SizeToContent="Height" 时，实际高度在加载后才确定，需在 Loaded 后重新定位
        Loaded += (_, _) => PositionNearTray();
    }

    public event EventHandler? RefreshRequested;
    public event EventHandler? OpenConsoleRequested;
    public event EventHandler? SettingsRequested;

    public void ShowSnapshot(UsageSnapshot snapshot)
    {
        var hasError = snapshot.HasError;
        ApplyRow(RollingBar, RollingPercent, snapshot.Rolling, hasError);
        ApplyRow(WeeklyBar, WeeklyPercent, snapshot.Weekly, hasError);
        ApplyRow(MonthlyBar, MonthlyPercent, snapshot.Monthly, hasError);

        var statusColor = hasError || !snapshot.Rolling.IsAvailable
            ? ThresholdRule.Gray
            : ThresholdRule.ColorFor(snapshot.Rolling.Percent);
        StatusDot.Fill = CreateBrush(statusColor);

        UpdatedText.Text = $"Updated {snapshot.FetchedAt.ToLocalTime():HH:mm:ss}";
        ResetsText.Text = FormatResets(snapshot);

        if (hasError)
        {
            ErrorText.Text = snapshot.ErrorMessage ?? "Unavailable";
            ErrorPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
        }
    }

    public void PositionNearTray()
    {
        var area = SystemParameters.WorkArea;
        // SizeToContent 模式下显示前 Height 为 NaN/0，优先用实际渲染高度
        var height = ActualHeight > 0 ? ActualHeight : Height;
        if (double.IsNaN(height))
        {
            height = 240;
        }

        Left = area.Right - Width - 16;
        Top = area.Bottom - height - 16;
    }

    private static void ApplyRow(WpfProgressBar bar, TextBlock percentText, UsageWindow window, bool hasError)
    {
        if (!hasError && window.IsAvailable)
        {
            var color = ThresholdRule.ColorFor(window.Percent!.Value);
            bar.Foreground = CreateBrush(color);
            bar.Value = window.Percent.Value;
            percentText.Text = $"{window.Percent.Value:0.#}%";
            percentText.Foreground = TextBrush;
        }
        else
        {
            bar.Value = 0;
            percentText.Text = "n/a";
            percentText.Foreground = MutedBrush;
        }
    }

    private static string FormatResets(UsageSnapshot snapshot)
    {
        static string One(UsageWindow window)
        {
            if (!window.IsAvailable || window.ResetsAt is null)
            {
                return "--";
            }

            var span = window.ResetsAt.Value.ToLocalTime() - DateTimeOffset.Now;
            if (span <= TimeSpan.Zero)
            {
                return "now";
            }

            return span.TotalDays >= 1
                ? $"{(int)span.TotalDays}d {span.Hours:00}h"
                : $"{span.Hours:00}h {span.Minutes:00}m";
        }

        return $"Resets - 5h {One(snapshot.Rolling)} | Week {One(snapshot.Weekly)} | Month {One(snapshot.Monthly)}";
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(color);
    }

    private void OnDeactivated(object sender, EventArgs e) => Hide();

    private void OnRefreshClick(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenConsoleClick(object sender, RoutedEventArgs e) =>
        OpenConsoleRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);
}
