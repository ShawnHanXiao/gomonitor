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

    // 底色渐变（对应 icon-bolt-trio.svg 的背景）
    private const string BackgroundTop = "#3E8BFF";
    private const string BackgroundBottom = "#0A35C8";

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
        _proxyMenuItem = CreateMenuItem("Proxy: Off", () => ProxyToggleRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add(_proxyMenuItem);
        _menu.Items.Add(CreateMenuItem("New Session", () => NewSessionRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(CreateMenuItem("Settings", () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(CreateMenuItem("Exit", () => ExitRequested?.Invoke(this, EventArgs.Empty)));
        _notifyIcon.ContextMenuStrip = _menu;
    }

    private readonly ToolStripMenuItem _proxyMenuItem;

    public event EventHandler? OpenMonitorRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? ProxyToggleRequested;
    public event EventHandler? NewSessionRequested;

    public void UpdateProxyState(bool running, int port)
    {
        _proxyMenuItem.Text = running ? $"Proxy: Running ({port})" : "Proxy: Off";
    }

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

        // 对应 design/icon-bolt-trio.svg：蓝色渐变圆角底色 + 顶部高光 + 三道光泽闪电
        using (var background = CreateRoundedRectPath(0, 0, 32, 32, 7))
        using (var bgBrush = new LinearGradientBrush(new Rectangle(0, 0, 32, 32),
            ColorTranslator.FromHtml(BackgroundTop),
            ColorTranslator.FromHtml(BackgroundBottom), 59f))
        {
            graphics.FillPath(bgBrush, background);

            // 顶部高光：白色 35% → 0（至 55% 高度处消失）
            graphics.SetClip(new Rectangle(0, 0, 32, 18));
            using (var shineBrush = new LinearGradientBrush(new Rectangle(0, 0, 32, 18),
                Color.FromArgb(89, Color.White), Color.FromArgb(0, Color.White), 90f))
            {
                graphics.FillPath(shineBrush, background);
            }

            graphics.ResetClip();
        }

        DrawBolts(graphics, colorHexes);

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

    // 闪电多边形模板（viewBox 100 坐标系，上下尖、中段稍宽）
    private static readonly PointF[] BoltTemplate =
    {
        new(20f, 0f), new(2f, 42f), new(14f, 42f), new(6f, 80f), new(29f, 33f), new(16f, 33f),
    };

    // 三道闪电 x 位置（viewBox 100 坐标系）：左 / 中 / 右
    private static readonly float[] BoltXPositions = { 14f, 37f, 59f };

    // 各状态颜色的光泽渐变（上亮下深）
    private static readonly Dictionary<string, (string Top, string Bottom)> BoltGradients = new()
    {
        [ThresholdRule.Blue] = ("#7FE0FF", "#1E88F0"),
        [ThresholdRule.Yellow] = ("#FFF35C", "#FFC800"),
        [ThresholdRule.Red] = ("#FF8A70", "#E62E1E"),
        [ThresholdRule.Gray] = ("#B0B8C4", "#7A828E"),
    };

    private static void DrawBolts(Graphics graphics, IReadOnlyList<string> colorHexes)
    {
        const float scale = 0.32f; // viewBox 100 → 32px 图标
        // 绘制顺序：右→左，使左侧闪电压住右侧闪电
        for (var i = 2; i >= 0; i--)
        {
            var (topHex, bottomHex) = BoltGradients[colorHexes[i]];
            using var matrix = new Matrix();
            matrix.RotateAt(12f, new PointF(15f * scale, 40f * scale));
            matrix.Translate(BoltXPositions[i] * scale, 10f * scale, MatrixOrder.Append);

            var points = BoltTemplate
                .Select(p => new PointF(p.X * scale, p.Y * scale))
                .Select(p =>
                {
                    PointF[] pt = { p };
                    matrix.TransformPoints(pt);
                    return pt[0];
                })
                .ToArray();

            var minX = points.Min(p => p.X);
            var maxX = points.Max(p => p.X);
            var minY = points.Min(p => p.Y);
            var maxY = points.Max(p => p.Y);
            var bounds = new RectangleF(minX, minY, Math.Max(maxX - minX, 1f), Math.Max(maxY - minY, 1f));

            using var brush = new LinearGradientBrush(bounds,
                ColorTranslator.FromHtml(topHex), ColorTranslator.FromHtml(bottomHex), 90f);
            using var pen = new Pen(brush, 4f * scale) { LineJoin = LineJoin.Round };

            graphics.FillPolygon(brush, points);
            graphics.DrawPolygon(pen, points); // 模拟 SVG stroke-linejoin=round 的圆角
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

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
