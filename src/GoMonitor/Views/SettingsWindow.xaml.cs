using System.Windows;
using System.Windows.Controls;
using GoMonitor.Models;

namespace GoMonitor.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();

        _settings = new AppSettings
        {
            ApiKeyOverride = current.ApiKeyOverride,
            PollIntervalSeconds = current.EffectivePollIntervalSeconds,
            StartWithWindows = current.StartWithWindows,
            ProxyEnabled = current.ProxyEnabled,
            ProxyPort = current.EffectiveProxyPort,
            ProxyUserAgent = current.ProxyUserAgent,
            SessionIdleHours = current.EffectiveSessionIdleHours,
            UpstreamProto = current.UpstreamProto,
            UpstreamHost = current.UpstreamHost,
        };

        ApiKeyBox.Password = current.ApiKeyOverride ?? string.Empty;
        SelectInterval(current.EffectivePollIntervalSeconds);
        StartupCheck.IsChecked = current.StartWithWindows;
        ProxyEnabledCheck.IsChecked = current.ProxyEnabled;
        ProxyPortBox.Text = current.EffectiveProxyPort.ToString();
        SessionIdleBox.Text = current.EffectiveSessionIdleHours.ToString();
        UserAgentBox.Text = current.ProxyUserAgent;
        UpstreamProtoBox.Text = current.UpstreamProto;
        UpstreamHostBox.Text = current.UpstreamHost;

        if (!string.IsNullOrWhiteSpace(current.ApiKeyOverride))
        {
            KeyHint.Text = $"Override active - ends with {LastFour(current.ApiKeyOverride)}";
        }
    }

    public event EventHandler<AppSettings>? Saved;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _settings.ApiKeyOverride =
            string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? null : ApiKeyBox.Password;
        _settings.PollIntervalSeconds = GetSelectedInterval();
        _settings.StartWithWindows = StartupCheck.IsChecked == true;
        _settings.ProxyEnabled = ProxyEnabledCheck.IsChecked == true;
        _settings.ProxyPort = int.TryParse(ProxyPortBox.Text.Trim(), out var port) ? port : 9355;
        _settings.SessionIdleHours = int.TryParse(SessionIdleBox.Text.Trim(), out var idle) ? idle : 6;
        _settings.ProxyUserAgent = string.IsNullOrWhiteSpace(UserAgentBox.Text)
            ? "opencode/1.18.29 cli"
            : UserAgentBox.Text.Trim();
        _settings.UpstreamProto = string.IsNullOrWhiteSpace(UpstreamProtoBox.Text)
            ? "https"
            : UpstreamProtoBox.Text.Trim();
        _settings.UpstreamHost = string.IsNullOrWhiteSpace(UpstreamHostBox.Text)
            ? "opencode.ai"
            : UpstreamHostBox.Text.Trim();
        Saved?.Invoke(this, _settings);
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void SelectInterval(int seconds)
    {
        foreach (var item in IntervalBox.Items)
        {
            if (item is ComboBoxItem comboItem
                && comboItem.Tag is string tag
                && int.TryParse(tag, out var value)
                && value == seconds)
            {
                IntervalBox.SelectedItem = comboItem;
                return;
            }
        }

        IntervalBox.SelectedIndex = 2;
    }

    private int GetSelectedInterval()
    {
        if (IntervalBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && int.TryParse(tag, out var value))
        {
            return value;
        }

        return 60;
    }

    private static string LastFour(string value) =>
        value.Length <= 4 ? value : value[^4..];
}
