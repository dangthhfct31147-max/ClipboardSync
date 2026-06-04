using System.Collections.Concurrent;
using ClipboardSync.Utils;

namespace ClipboardSync.Core;

public sealed class PeerManager : IDisposable
{
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();
    private readonly FileLogger _logger;
    private readonly int _peerTimeoutSeconds;
    private readonly PeerDiscovery _discovery;
    private CancellationTokenSource? _cleanupCts;
    private bool _disposed;

    public event EventHandler<PeerInfo>? PeerConnected;
    public event EventHandler<string>? PeerDisconnected;

    public PeerManager(AppConfig config, FileLogger logger, PeerDiscovery discovery)
    {
        _logger = logger;
        _peerTimeoutSeconds = config.Discovery.PeerTimeoutSeconds;
        _discovery = discovery;
        _discovery.PeerDiscovered += OnPeerDiscovered;
    }

    public IReadOnlyCollection<PeerInfo> GetPeers() => _peers.Values.ToList();
    public int PeerCount => _peers.Count;

    public void Start()
    {
        _cleanupCts = new CancellationTokenSource();
        _ = CleanupLoop(_cleanupCts.Token);
        _logger.Info("PeerManager started.");
    }

    private void OnPeerDiscovered(object? sender, PeerDiscoveredEventArgs e)
    {
        var peer = e.Peer;
        if (_peers.TryGetValue(peer.PeerId, out var existing) &&
            existing.IpAddress == peer.IpAddress && existing.TcpPort == peer.TcpPort)
        {
            _peers[peer.PeerId] = peer with { LastSeen = DateTime.UtcNow };
            return;
        }

        if (_peers.TryAdd(peer.PeerId, peer))
        {
            _logger.Info($"New peer discovered: {peer.Hostname} ({peer.IpAddress}:{peer.TcpPort})");
            PeerConnected?.Invoke(this, peer);
        }
        else
        {
            _peers[peer.PeerId] = peer with { LastSeen = DateTime.UtcNow };
        }
    }

    public void UpdatePeerSeen(string peerId)
    {
        if (_peers.TryGetValue(peerId, out var peer))
        {
            _peers[peerId] = peer with { LastSeen = DateTime.UtcNow };
        }
    }

    public void ClearAndRediscover()
    {
        foreach (var (id, peer) in _peers)
        {
            PeerDisconnected?.Invoke(this, id);
        }
        _peers.Clear();
        _logger.Info("Peer list cleared, waiting for re-discovery.");
    }

    private async Task CleanupLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                var cutoff = DateTime.UtcNow.AddSeconds(-_peerTimeoutSeconds);
                foreach (var (id, peer) in _peers)
                {
                    if (peer.LastSeen < cutoff)
                    {
                        if (_peers.TryRemove(id, out var removed))
                        {
                            _logger.Info($"Peer timed out: {removed.Hostname} ({removed.IpAddress})");
                            PeerDisconnected?.Invoke(this, id);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Warn($"Cleanup loop error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupCts?.Cancel();
        _cleanupCts?.Dispose();
        _discovery.PeerDiscovered -= OnPeerDiscovered;
        _logger.Info("PeerManager disposed.");
    }
}
