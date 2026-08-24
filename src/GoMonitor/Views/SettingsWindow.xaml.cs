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
            StartWithWindows = current.StartWithWindows
        };

        ApiKeyBox.Password = current.ApiKeyOverride ?? string.Empty;
        SelectInterval(current.EffectivePollIntervalSeconds);
        StartupCheck.IsChecked = current.StartWithWindows;

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
