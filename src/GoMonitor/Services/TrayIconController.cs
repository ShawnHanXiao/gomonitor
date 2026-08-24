using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GoMonitor.Models;

namespace GoMonitor.Services;

public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon = new();
    private readonly ContextMenuStrip _menu = new();

    private Icon? _currentIcon;
    private bool _disposed;

    public TrayIconController()
    {
        _notifyIcon.Text = "GoMonitor";
        _notifyIcon.Icon = CreateStateIcon(new[] { ThresholdRule.Gray, ThresholdRule.Gray, ThresholdRule.Gray }, IsPeakUtc(DateTime.UtcNow));
        _notifyIcon.Visible = true;
        _notifyIcon.MouseClick += OnMouseClick;

        _menu.Items.Add(CreateMenuItem("Open Monitor", () => OpenMonitorRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(CreateMenuItem("Refresh Now", () => RefreshRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(CreateMenuItem("Settings", () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(CreateMenuItem("Exit", () => ExitRequested?.Invoke(this, EventArgs.Empty)));
        _notifyIcon.ContextMenuStrip = _menu;
    }

    public event EventHandler? OpenMonitorRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public void Update(UsageSnapshot snapshot)
    {
        var colors = new[]
        {
            ColorFor(snapshot, snapshot.Rolling),
            ColorFor(snapshot, snapshot.Weekly),
            ColorFor(snapshot, snapshot.Monthly),
        };

        var isPeak = IsPeakUtc(DateTime.UtcNow);
        SetIcon(CreateStateIcon(colors, isPeak));
        _notifyIcon.Text = Clamp($"{FormatTooltip(snapshot)} | {(isPeak ? "Peak" : "Off-Peak")}", 63);
    }

    // DeepSeek V4 Flash / V4 Flash Vision Exp / V4 Pro 峰谷规则：
    // Peak 为 UTC 01:00-04:00 与 06:00-10:00，其余时段均为 Off-Peak
    private static bool IsPeakUtc(DateTime utcNow)
    {
        var time = utcNow.TimeOfDay;
        return (time >= TimeSpan.FromHours(1) && time < TimeSpan.FromHours(4))
            || (time >= TimeSpan.FromHours(6) && time < TimeSpan.FromHours(10));
    }

    private static string ColorFor(UsageSnapshot snapshot, UsageWindow window) =>
        !snapshot.HasError && window.IsAvailable
            ? ThresholdRule.ColorFor(window.Percent)
            : ThresholdRule.Gray;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _currentIcon?.Dispose();
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            OpenMonitorRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetIcon(Icon icon)
    {
        var old = _currentIcon;
        _currentIcon = icon;
        _notifyIcon.Icon = icon;
        old?.Dispose();
    }

    private static ToolStripMenuItem CreateMenuItem(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    private static string FormatTooltip(UsageSnapshot snapshot)
    {
        if (snapshot.HasError && !snapshot.Rolling.IsAvailable && !snapshot.Weekly.IsAvailable && !snapshot.Monthly.IsAvailable)
        {
            return $"GoMonitor - {snapshot.ErrorMessage ?? "Unavailable"}";
        }

        return $"5h {FormatPercent(snapshot.Rolling)} | Week {FormatPercent(snapshot.Weekly)} | Month {FormatPercent(snapshot.Monthly)}";
    }

    private static string FormatPercent(UsageWindow window) =>
        window.IsAvailable ? $"{window.Percent!.Value:0.#}%" : "n/a";

    private static string Clamp(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static Icon CreateStateIcon(IReadOnlyList<string> colorHexes, bool isPeak)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        // 峰时绘制黄色圆角底色，谷时无底色
        if (isPeak)
        {
            using var background = CreateRoundedRectPath(0, 0, 32, 32, 7);
            using var backgroundBrush = new SolidBrush(ColorTranslator.FromHtml(ThresholdRule.Yellow));
            graphics.FillPath(backgroundBrush, background);
        }

        // 三个直角方块纵向排列（不重叠），从上至下：5小时 / 每周 / 每月
        const int barWidth = 28;
        const int barHeight = 8;
        const int barLeft = 2;
        const int gap = 2;
        const int top = 2;

        for (var i = 0; i < 3; i++)
        {
            var y = top + i * (barHeight + gap);
            var color = ColorTranslator.FromHtml(colorHexes[i]);

            using (var fill = new SolidBrush(color))
            {
                graphics.FillRectangle(fill, barLeft, y, barWidth, barHeight);
            }

            using var edge = new Pen(Color.FromArgb(70, 0, 0, 0), 1);
            graphics.DrawRectangle(edge, barLeft, y, barWidth - 1, barHeight - 1);

            DrawLightning(graphics, barLeft, y, barWidth, barHeight);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath CreateRoundedRectPath(int x, int y, int width, int height, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawLightning(Graphics graphics, int left, int top, int width, int height)
    {
        // 闪电居中于方块内
        var cx = left + width / 2f;
        var cy = top + height / 2f;
        var s = height * 0.55f; // 闪电半尺寸

        var points = new[]
        {
            new PointF(cx + s * 0.15f, cy - s),
            new PointF(cx - s * 0.55f, cy + s * 0.15f),
            new PointF(cx - s * 0.05f, cy + s * 0.15f),
            new PointF(cx - s * 0.15f, cy + s),
            new PointF(cx + s * 0.55f, cy - s * 0.15f),
            new PointF(cx + s * 0.05f, cy - s * 0.15f),
        };

        using var brush = new SolidBrush(Color.White);
        graphics.FillPolygon(brush, points);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
