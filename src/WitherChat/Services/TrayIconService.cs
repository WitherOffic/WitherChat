using System.Drawing;
using System.Windows.Forms;

namespace WitherChat.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Action _openApplication;
    private readonly Action _exitApplication;
    private readonly Func<string, string> _localize;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly Icon _icon;
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayIconService(
        Action openApplication,
        Action exitApplication,
        Func<string, string> localize)
    {
        _openApplication = openApplication;
        _exitApplication = exitApplication;
        _localize = localize;

        _openItem = new ToolStripMenuItem();
        _openItem.Click += OpenItem_Click;
        _exitItem = new ToolStripMenuItem();
        _exitItem.Click += ExitItem_Click;
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(_openItem);
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
        _exitItem.Text = _localize("TrayExit");
    }

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _openApplication();
        }
    }

    private void NotifyIcon_BalloonTipClicked(object? sender, EventArgs e) => _openApplication();

    private void OpenItem_Click(object? sender, EventArgs e) => _openApplication();

    private void ExitItem_Click(object? sender, EventArgs e) => _exitApplication();
}
