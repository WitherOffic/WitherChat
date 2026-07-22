using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WitherChat.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Action _openApplication;
    private readonly Action _restartApplication;
    private readonly Action _exitApplication;
    private readonly Func<string, string> _localize;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly Icon _icon;
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayIconService(
        Action openApplication,
        Action restartApplication,
        Action exitApplication,
        Func<string, string> localize)
    {
        _openApplication = openApplication;
        _restartApplication = restartApplication;
        _exitApplication = exitApplication;
        _localize = localize;

        _openItem = CreateMenuItem();
        _openItem.Click += OpenItem_Click;
        _restartItem = CreateMenuItem();
        _restartItem.Click += RestartItem_Click;
        _exitItem = CreateMenuItem();
        _exitItem.Click += ExitItem_Click;
        _contextMenu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(214, 22, 25, 34),
            ForeColor = Color.FromArgb(238, 241, 247),
            Renderer = new LiquidGlassTrayRenderer(),
            ShowCheckMargin = false,
            ShowImageMargin = false,
            DropShadowEnabled = true,
            Opacity = 0.98,
            Padding = new Padding(6),
            MinimumSize = new Size(230, 0),
            Font = new Font("Segoe UI Variable", 10F, FontStyle.Regular, GraphicsUnit.Point)
        };
        _openItem.ForeColor = _contextMenu.ForeColor;
        _restartItem.ForeColor = _contextMenu.ForeColor;
        _exitItem.ForeColor = _contextMenu.ForeColor;
        _contextMenu.Items.Add(_openItem);
        _contextMenu.Items.Add(_restartItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_exitItem);
        _contextMenu.Opening += ContextMenu_Opening;

        _icon = LoadApplicationIcon();
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = _icon,
            Text = AppInfo.Name,
            Visible = true
        };
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _notifyIcon.BalloonTipClicked += NotifyIcon_BalloonTipClicked;
        UpdateLocalizedText();
    }

    public void ShowStillRunningNotice()
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = AppInfo.Name;
        _notifyIcon.BalloonTipText = _localize("TrayStillRunning");
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.MouseClick -= NotifyIcon_MouseClick;
        _notifyIcon.BalloonTipClicked -= NotifyIcon_BalloonTipClicked;
        _contextMenu.Opening -= ContextMenu_Opening;
        _openItem.Click -= OpenItem_Click;
        _restartItem.Click -= RestartItem_Click;
        _exitItem.Click -= ExitItem_Click;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _icon.Dispose();
    }

    private static Icon LoadApplicationIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/WitherChat;component/Assets/WitherChat.ico", UriKind.Absolute));
        if (resource?.Stream is null)
        {
            throw new InvalidOperationException("WitherChat tray icon resource was not found.");
        }

        using (resource.Stream)
        using (var sourceIcon = new Icon(resource.Stream))
        {
            return (Icon)sourceIcon.Clone();
        }
    }

    private void ContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e) =>
        UpdateLocalizedText();

    private void UpdateLocalizedText()
    {
        _openItem.Text = _localize("TrayOpen");
        _restartItem.Text = _localize("TrayRestart");
        _exitItem.Text = _localize("TrayExit");
    }

    private static ToolStripMenuItem CreateMenuItem() => new()
    {
        AutoSize = true,
        Margin = new Padding(2),
        Padding = new Padding(12, 8, 12, 8)
    };

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _openApplication();
        }
    }

    private void NotifyIcon_BalloonTipClicked(object? sender, EventArgs e) => _openApplication();

    private void OpenItem_Click(object? sender, EventArgs e) => _openApplication();

    private void RestartItem_Click(object? sender, EventArgs e) => _restartApplication();

    private void ExitItem_Click(object? sender, EventArgs e) => _exitApplication();

    private sealed class LiquidGlassTrayRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color BackgroundTop = Color.FromArgb(248, 31, 35, 47);
        private static readonly Color BackgroundBottom = Color.FromArgb(248, 18, 21, 30);
        private static readonly Color Border = Color.FromArgb(104, 132, 143, 168);
        private static readonly Color ItemBackground = Color.FromArgb(28, 255, 255, 255);
        private static readonly Color ItemBorder = Color.FromArgb(38, 202, 211, 232);
        private static readonly Color HoverTop = Color.FromArgb(112, 126, 112, 255);
        private static readonly Color HoverBottom = Color.FromArgb(70, 89, 187, 220);

        public LiquidGlassTrayRenderer()
            : base(new ProfessionalColorTable { UseSystemColors = false })
        {
            RoundedEdges = true;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Math.Max(1, e.ToolStrip.Width - 1), Math.Max(1, e.ToolStrip.Height - 1));
            using var path = CreateRoundedRectangle(bounds, 14);
            using var brush = new LinearGradientBrush(bounds, BackgroundTop, BackgroundBottom, LinearGradientMode.Vertical);
            e.Graphics.FillPath(brush, path);

            var oldRegion = e.ToolStrip.Region;
            e.ToolStrip.Region = new Region(path);
            oldRegion?.Dispose();
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Math.Max(1, e.ToolStrip.Width - 1), Math.Max(1, e.ToolStrip.Height - 1));
            using var path = CreateRoundedRectangle(bounds, 14);
            using var pen = new Pen(Border, 1F);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(3, 2, Math.Max(1, e.Item.Width - 6), Math.Max(1, e.Item.Height - 4));
            using var path = CreateRoundedRectangle(bounds, 10);
            if (e.Item.Selected || e.Item.Pressed)
            {
                using var hoverBrush = new LinearGradientBrush(
                    bounds,
                    HoverTop,
                    HoverBottom,
                    LinearGradientMode.Horizontal);
                e.Graphics.FillPath(hoverBrush, path);
            }
            else
            {
                using var normalBrush = new SolidBrush(ItemBackground);
                e.Graphics.FillPath(normalBrush, path);
            }

            using var pen = new Pen(
                e.Item.Selected || e.Item.Pressed
                    ? Color.FromArgb(100, 194, 205, 255)
                    : ItemBorder,
                1F);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(Color.FromArgb(64, 180, 190, 211));
            e.Graphics.DrawLine(pen, 14, y, Math.Max(14, e.Item.Width - 14), y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? Color.FromArgb(244, 247, 255)
                : Color.FromArgb(130, 141, 161);
            e.TextFormat |= TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            base.OnRenderItemText(e);
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            var diameter = Math.Max(2, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
