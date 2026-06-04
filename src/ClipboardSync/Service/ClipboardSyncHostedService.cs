using ClipboardSync.Core;
using ClipboardSync.Tray;
using ClipboardSync.Utils;
using Microsoft.Extensions.Hosting;
using System.Text;

namespace ClipboardSync.Service;

public sealed class ClipboardSyncHostedService : IHostedService, IDisposable
{
    private readonly ClipboardMonitor _clipboardMonitor;
    private readonly PeerDiscovery _discovery;
    private readonly PeerManager _peerManager;
    private readonly TcpTransfer _tcpTransfer;
    private readonly TrayIconManager _trayIcon;
    private readonly FileLogger _logger;
    private readonly AppConfig _config;
    private readonly IHostApplicationLifetime _appLifetime;
    private bool _disposed;

    public ClipboardSyncHostedService(
        ClipboardMonitor clipboardMonitor,
        PeerDiscovery discovery,
        PeerManager peerManager,
        TcpTransfer tcpTransfer,
        TrayIconManager trayIcon,
        FileLogger logger,
        AppConfig config,
        IHostApplicationLifetime appLifetime)
    {
        _clipboardMonitor = clipboardMonitor;
        _discovery = discovery;
        _peerManager = peerManager;
        _tcpTransfer = tcpTransfer;
        _trayIcon = trayIcon;
        _logger = logger;
        _config = config;
        _appLifetime = appLifetime;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.Info("ClipboardSyncHostedService starting...");

        _clipboardMonitor.ClipboardChanged += OnClipboardChanged;
        _peerManager.PeerConnected += OnPeerConnected;
        _peerManager.PeerDisconnected += OnPeerDisconnected;
        _tcpTransfer.ClipboardReceived += OnClipboardReceived;
        _discovery.NetworkChanged += OnNetworkChanged;
        _trayIcon.ExitRequested += OnExitRequested;

        await _discovery.StartAsync();
        await _tcpTransfer.StartAsync();
        _peerManager.Start();
        _clipboardMonitor.Start();
        _trayIcon.Initialize(_config.Sync, GetStatusText());
        _logger.Info("All services started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Info("ClipboardSyncHostedService stopping...");
        _clipboardMonitor.ClipboardChanged -= OnClipboardChanged;
        _peerManager.PeerConnected -= OnPeerConnected;
        _peerManager.PeerDisconnected -= OnPeerDisconnected;
        _tcpTransfer.ClipboardReceived -= OnClipboardReceived;
        _discovery.NetworkChanged -= OnNetworkChanged;
        _trayIcon.ExitRequested -= OnExitRequested;

        _clipboardMonitor.Dispose();
        _discovery.Dispose();
        _peerManager.Dispose();
        _tcpTransfer.Dispose();
        _trayIcon.Dispose();

        _logger.Info("ClipboardSyncHostedService stopped.");
        await Task.CompletedTask;
    }

    private async void OnClipboardChanged(object? sender, ClipboardChangedEventArgs e)
    {
        if (!_config.Sync.Enabled) return;
        if (e.Format == ClipboardFormat.Text && !_config.Sync.SyncText) return;
        if (e.Format == ClipboardFormat.Image && !_config.Sync.SyncImages) return;
        if (e.Format == ClipboardFormat.Files && !_config.Sync.SyncFiles) return;

        try
        {
            var packet = new ClipboardPacket
            {
                Type = "clipboard",
                Hash = e.Hash,
                Format = e.Format,
                Size = e.Format == ClipboardFormat.Image
                    ? (e.ImageData?.Length ?? 0)
                    : Encoding.UTF8.GetByteCount(e.TextContent ?? string.Empty),
                TextContent = e.TextContent,
                ImageData = e.ImageData,
                FilePaths = e.FilePaths,
                SenderId = _discovery.LocalPeerId,
                Hostname = _discovery.LocalHostname,
                TcpPort = _config.Transfer.TcpPort
            };

            await _tcpTransfer.SendClipboardAsync(packet);
            _trayIcon.UpdateStatus(GetStatusText());
            _logger.Debug($"Clipboard sent to peers: {e.Format}");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to send clipboard", ex);
        }
    }

    private void OnPeerConnected(object? sender, PeerInfo peer)
    {
        _tcpTransfer.RegisterPeer(peer);
        _trayIcon.UpdateStatus(GetStatusText());
        _trayIcon.UpdatePeerList(_peerManager.GetPeers());
        _logger.Info($"Peer connected: {peer.Hostname}");
    }

    private void OnPeerDisconnected(object? sender, string peerId)
    {
        _tcpTransfer.UnregisterPeer(peerId);
        _trayIcon.UpdateStatus(GetStatusText());
        _trayIcon.UpdatePeerList(_peerManager.GetPeers());
        _logger.Info($"Peer disconnected: {peerId}");
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        _logger.Info("Network changed, triggering peer re-discovery...");
        _peerManager.ClearAndRediscover();
        _trayIcon.UpdateStatus(GetStatusText());
    }

    private void OnClipboardReceived(object? sender, ClipboardReceivedEventArgs e)
    {
        if (!_config.Sync.Enabled) return;
        if (e.Format == ClipboardFormat.Text && !_config.Sync.SyncText) return;
        if (e.Format == ClipboardFormat.Image && !_config.Sync.SyncImages) return;
        if (e.Format == ClipboardFormat.Files && !_config.Sync.SyncFiles) return;

        if (e.IsApplyingRemote)
        {
            _logger.Debug($"Received clipboard marked as remote-apply, skipping.");
            return;
        }

        if (e.SenderId == _discovery.LocalPeerId)
        {
            _logger.Debug($"Received clipboard from self, skipping.");
            return;
        }

        try
        {
            if (e.Hash == _clipboardMonitor.LastHash)
            {
                _logger.Debug("Received clipboard hash matches local, skipping.");
                return;
            }

            _clipboardMonitor.UpdateClipboardSilently(e.TextContent, e.ImageData, e.FilePaths);
            _logger.Info($"Clipboard received from {e.SenderId}: {e.Format}");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to apply received clipboard", ex);
        }
    }

    private string GetStatusText()
    {
        var peers = _peerManager.PeerCount;
        return peers == 0 ? "No peers connected"
            : $"Connected to {peers} peer{(peers == 1 ? "" : "s")}";
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        _logger.Info("Exit requested from tray icon.");
        _appLifetime.StopApplication();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
