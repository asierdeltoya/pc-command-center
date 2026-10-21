using System.Windows;
using PCCommandCenter.Core;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PCCommandCenter.App.Services;

/// <summary>Windows notification-area icon with a quick-status flyout and a context menu.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly IHardwareMonitor _hardware;
    private DateTime _lastTooltip = DateTime.MinValue;

    public event Action? OpenRequested;
    public event Action? QuickStatusRequested;
    public event Action? DiagnosticRequested;
    public event Action? ExitRequested;

    public TrayService(IHardwareMonitor hardware)
    {
        _hardware = hardware;
        _icon = new Forms.NotifyIcon
        {
            Text = AppInfo.Name,
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) QuickStatusRequested?.Invoke();
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
        _hardware.SnapshotUpdated += OnSnapshot;
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/PCCommandCenter;component/Assets/app.ico"));
            if (info != null) return new Drawing.Icon(info.Stream, new Drawing.Size(16, 16));
        }
        catch (Exception ex) { AppLog.Warn("Tray icon resource missing", ex); }
        return Drawing.SystemIcons.Application;
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColors()),
            ShowImageMargin = false,
            Font = new Drawing.Font("Segoe UI", 9.5f),
            Padding = new Forms.Padding(4),
        };
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            Add(menu, Loc.T("Open PC Command Center"), () => OpenRequested?.Invoke(), bold: true);
            Add(menu, Loc.T("Quick status"), () => QuickStatusRequested?.Invoke());
            Add(menu, Loc.T("Run diagnostic"), () => DiagnosticRequested?.Invoke());
            menu.Items.Add(new Forms.ToolStripSeparator());
            Add(menu, Loc.T("Exit"), () => ExitRequested?.Invoke());
        };
        return menu;
    }

    private static void Add(Forms.ContextMenuStrip menu, string text, Action action, bool bold = false)
    {
        var item = new Forms.ToolStripMenuItem(text) { ForeColor = Drawing.Color.FromArgb(237, 240, 245), Padding = new Forms.Padding(6, 5, 18, 5) };
        if (bold) item.Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void OnSnapshot(object? sender, SystemSnapshot s)
    {
        if (DateTime.Now - _lastTooltip < TimeSpan.FromSeconds(3)) return;
        _lastTooltip = DateTime.Now;
        var text = $"{AppInfo.Name}\nCPU {Fmt.Percent(s.Cpu.Usage)} · RAM {Fmt.Percent(s.Memory.Percent)}" +
                   (s.PrimaryGpu is { Usage: { } g } ? $" · GPU {Fmt.Percent(g)}" : "") +
                   (s.Network.LatencyMs is { } l ? $"\n{Loc.T("Latency")} {Fmt.Ms(l)}" : "");
        try { _icon.Text = text.Length > 127 ? text[..127] : text; } catch { }
    }

    public void ShowBalloon(string title, string message, NotificationLevel level)
    {
        try
        {
            _icon.ShowBalloonTip(5000, title, message, level switch
            {
                NotificationLevel.Error => Forms.ToolTipIcon.Error,
                NotificationLevel.Warning => Forms.ToolTipIcon.Warning,
                _ => Forms.ToolTipIcon.Info,
            });
        }
        catch { }
    }

    public void Dispose()
    {
        _hardware.SnapshotUpdated -= OnSnapshot;
        _icon.Visible = false;
        _icon.Dispose();
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(25, 29, 39);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(38, 44, 58);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(43, 50, 66);
        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Border;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
    }
}
