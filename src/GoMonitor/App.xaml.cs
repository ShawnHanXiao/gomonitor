using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using GoMonitor.Models;
using GoMonitor.Services;
using GoMonitor.Views;
using Microsoft.Win32;

namespace GoMonitor;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\GoMonitor.SingleInstance";
    private const string ActivateEventName = "Local\\GoMonitor.Activate";
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryRunValue = "GoMonitor";

    private static readonly Mutex SingleInstanceMutex = new(true, MutexName, out _);
    private static readonly EventWaitHandle ActivateSignal =
        new(false, EventResetMode.AutoReset, ActivateEventName);

    private SettingsService _settingsService = null!;
    private AppSettings _settings = null!;
    private AuthKeyProvider _authProvider = null!;
    private OpenCodeUsageService _usageService = null!;
    private TrayIconController _trayIcon = null!;
    private MonitorWindow _monitorWindow = null!;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer _timer = null!;
    private bool _startMinimized;
    private bool _refreshing;
    private UsageSnapshot? _lastSnapshot;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!SingleInstanceMutex.WaitOne(0, false))
        {
            ActivateSignal.Set();
            Shutdown();
            return;
        }

        _startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _settingsService = new SettingsService(Path.Combine(appData, "GoMonitor", "settings.json"));
        _settings = _settingsService.Load();
        _authProvider = new AuthKeyProvider(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share",
            "opencode",
            "auth.json"));
        _usageService = new OpenCodeUsageService();

        var activationThread = new Thread(OnActivationSignal)
        {
            IsBackground = true,
            Name = "GoMonitorActivation"
        };
        activationThread.Start();

        _trayIcon = new TrayIconController();
        _trayIcon.OpenMonitorRequested += (_, _) => ToggleMonitor();
        _trayIcon.RefreshRequested += async (_, _) => await RefreshAsync();
        _trayIcon.SettingsRequested += (_, _) => ShowSettings();
        _trayIcon.ExitRequested += (_, _) => Shutdown();

        _monitorWindow = new MonitorWindow();
        _monitorWindow.RefreshRequested += async (_, _) => await RefreshAsync();
        _monitorWindow.OpenConsoleRequested += (_, _) => OpenConsole();
        _monitorWindow.SettingsRequested += (_, _) => ShowSettings();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_settings.EffectivePollIntervalSeconds)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        ApplyStartupSetting();

        _ = RefreshAsync();
        if (!_startMinimized)
        {
            ShowMonitor();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    private void OnActivationSignal()
    {
        while (ActivateSignal.WaitOne())
        {
            try
            {
                Dispatcher.Invoke(ShowMonitor);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var key = _authProvider.Resolve(_settings.ApiKeyOverride);
            var snapshot = await _usageService.FetchAsync(key);
            if (snapshot.HasError && _lastSnapshot is not null)
            {
                snapshot = _lastSnapshot.WithError(
                    snapshot.FetchedAt,
                    snapshot.ErrorMessage ?? "Unavailable");
            }

            _lastSnapshot = snapshot;
            _trayIcon.Update(snapshot);
            _monitorWindow.ShowSnapshot(snapshot);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ToggleMonitor()
    {
        if (_monitorWindow.IsVisible)
        {
            _monitorWindow.Hide();
        }
        else
        {
            ShowMonitor();
        }
    }

    private void ShowMonitor()
    {
        if (_monitorWindow.IsVisible)
        {
            _monitorWindow.Activate();
            return;
        }

        _monitorWindow.PositionNearTray();
        _monitorWindow.Show();
        _monitorWindow.Activate();
    }

    private void ShowSettings()
    {
        _monitorWindow.Hide();

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.Saved += OnSettingsSaved;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsSaved(object? sender, AppSettings settings)
    {
        _settings = settings;
        _settingsService.Save(settings);
        ApplyStartupSetting();
        _timer.Interval = TimeSpan.FromSeconds(settings.EffectivePollIntervalSeconds);
        _ = RefreshAsync();
    }

    private void OpenConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://opencode.ai/auth") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _monitorWindow.ShowSnapshot(UsageSnapshot.Error(
                DateTimeOffset.Now,
                "Unable to open console: " + ex.Message));
        }
    }

    private void ApplyStartupSetting()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryRunKey, writable: true);
            if (_settings.StartWithWindows)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    key.SetValue(RegistryRunValue, $"\"{exe}\" --minimized", RegistryValueKind.String);
                }
            }
            else
            {
                key.DeleteValue(RegistryRunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Registry access can be restricted in some environments; retry on next settings save.
        }
    }
}
