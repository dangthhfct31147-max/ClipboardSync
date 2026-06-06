using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClipboardSync.Core;
using ClipboardSync.Utils;

namespace ClipboardSync.Tray;

public sealed class TrayIconManager : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private Icon? _currentIcon;
    private readonly FileLogger _logger;
    private ContextMenuStrip? _contextMenu;
    private ToolStripMenuItem? _statusMenuItem;
    private ToolStripMenuItem? _peerListMenuItem;
    private ToolStripMenuItem? _syncTextMenuItem;
    private ToolStripMenuItem? _syncImagesMenuItem;
    private ToolStripMenuItem? _syncFilesMenuItem;
    private ToolStripMenuItem? _enableSyncMenuItem;
    private bool? _iconShowsConnected;
    private string _peerListSignature = string.Empty;
    private bool _disposed;

    private static readonly Color DisconnectedColor = Color.FromArgb(30, 64, 175);
    private static readonly Color ConnectedColor = Color.FromArgb(22, 163, 74);

    public TrayIconManager(FileLogger logger)
    {
        _logger = logger;
    }

    public void Initialize(SyncConfig config, string status)
    {
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(_statusMenuItem = new ToolStripMenuItem(status) { Enabled = false });
        _contextMenu.Items.Add(new ToolStripSeparator());
        _peerListMenuItem = new ToolStripMenuItem("Peers: none");
        _contextMenu.Items.Add(_peerListMenuItem);
        _contextMenu.Items.Add(new ToolStripSeparator());

        _enableSyncMenuItem = new ToolStripMenuItem("Sync Enabled", null, OnToggleSync)
        { Checked = config.Enabled };
        _contextMenu.Items.Add(_enableSyncMenuItem);

        _syncTextMenuItem = new ToolStripMenuItem("Sync Text", null, OnToggleText)
        { Checked = config.SyncText };
        _contextMenu.Items.Add(_syncTextMenuItem);

        _syncImagesMenuItem = new ToolStripMenuItem("Sync Images", null, OnToggleImages)
        { Checked = config.SyncImages };
        _contextMenu.Items.Add(_syncImagesMenuItem);

        _syncFilesMenuItem = new ToolStripMenuItem("Sync Files", null, OnToggleFiles)
        { Checked = config.SyncFiles };
        _contextMenu.Items.Add(_syncFilesMenuItem);

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

        _currentIcon = CreateStatusIcon(peerCount: 0);
        _iconShowsConnected = false;
        _notifyIcon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = "ClipboardSync",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        _notifyIcon.DoubleClick += OnDoubleClick;
        _logger.Info("Tray icon initialized.");
    }

    public static Color GetIconBackColorForPeerCount(int peerCount) =>
        peerCount > 0 ? ConnectedColor : DisconnectedColor;

    private static Icon CreateStatusIcon(int peerCount)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.Clear(GetIconBackColorForPeerCount(peerCount));
        using var pen = new Pen(Color.White, 2);
        g.DrawRectangle(pen, 4, 8, 24, 16);
        g.FillRectangle(Brushes.White, 8, 12, 8, 8);
        g.FillRectangle(Brushes.White, 18, 12, 8, 8);
        g.FillRectangle(Brushes.White, 8, 14, 16, 2);
        g.FillRectangle(Brushes.White, 8, 18, 16, 2);

        var iconHandle = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void UpdateStatus(string status)
    {
        if (_statusMenuItem == null) return;
        _statusMenuItem.Text = status;
    }

    public void UpdatePeerList(IReadOnlyCollection<PeerInfo> peers)
    {
        var peerList = peers
            .OrderBy(peer => peer.PeerId, StringComparer.Ordinal)
            .ToList();
        UpdateIconForPeerCount(peerList.Count);

        var signature = string.Join('\n', peerList.Select(peer =>
            $"{peer.PeerId}|{peer.Hostname}|{peer.IpAddress}|{peer.TcpPort}"));
        if (signature == _peerListSignature) return;
        _peerListSignature = signature;

        if (_peerListMenuItem == null) return;
        ClearDropDownItems(_peerListMenuItem);
        if (peerList.Count == 0)
        {
            _peerListMenuItem.Text = "Peers: none";
        }
        else
        {
            _peerListMenuItem.Text = $"Peers: {peerList.Count}";
            foreach (var peer in peerList)
            {
                var item = new ToolStripMenuItem($"{peer.Hostname} ({peer.IpAddress})", null, (_, _) =>
                {
                    try { Clipboard.SetText($"{peer.Hostname} ({peer.IpAddress})"); } catch { }
                });
                _peerListMenuItem.DropDownItems.Add(item);
            }
        }
    }

    private void UpdateIconForPeerCount(int peerCount)
    {
        if (_notifyIcon == null) return;
        var connected = peerCount > 0;
        if (_iconShowsConnected == connected) return;

        var newIcon = CreateStatusIcon(peerCount);
        var previousIcon = _currentIcon;
        _currentIcon = newIcon;
        _iconShowsConnected = connected;
        _notifyIcon.Icon = newIcon;
        previousIcon?.Dispose();
    }

    private static void ClearDropDownItems(ToolStripMenuItem menuItem)
    {
        foreach (ToolStripItem item in menuItem.DropDownItems)
        {
            item.Dispose();
        }

        menuItem.DropDownItems.Clear();
    }

    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? SyncToggled;
    public event EventHandler<bool>? SyncTextToggled;
    public event EventHandler<bool>? SyncImagesToggled;
    public event EventHandler<bool>? SyncFilesToggled;

    private void OnToggleSync(object? sender, EventArgs e)
    {
        if (_enableSyncMenuItem != null)
        {
            _enableSyncMenuItem.Checked = !_enableSyncMenuItem.Checked;
            SyncToggled?.Invoke(this, _enableSyncMenuItem.Checked);
        }
    }

    private void OnToggleText(object? sender, EventArgs e)
    {
        if (_syncTextMenuItem != null)
        {
            _syncTextMenuItem.Checked = !_syncTextMenuItem.Checked;
            SyncTextToggled?.Invoke(this, _syncTextMenuItem.Checked);
        }
    }

    private void OnToggleImages(object? sender, EventArgs e)
    {
        if (_syncImagesMenuItem != null)
        {
            _syncImagesMenuItem.Checked = !_syncImagesMenuItem.Checked;
            SyncImagesToggled?.Invoke(this, _syncImagesMenuItem.Checked);
        }
    }

    private void OnToggleFiles(object? sender, EventArgs e)
    {
        if (_syncFilesMenuItem != null)
        {
            _syncFilesMenuItem.Checked = !_syncFilesMenuItem.Checked;
            SyncFilesToggled?.Invoke(this, _syncFilesMenuItem.Checked);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnDoubleClick(object? sender, EventArgs e)
    {
        _notifyIcon?.ShowBalloonTip(2000, "ClipboardSync", "Clipboard sync is running", ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _currentIcon?.Dispose();
        _currentIcon = null;
        _iconShowsConnected = null;
        _contextMenu?.Dispose();
        _logger.Info("Tray icon disposed.");
    }
}
