using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClipboardSync.Utils;

namespace ClipboardSync.Core;

public sealed class PeerDiscovery : IDisposable
{
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private readonly FileLogger _logger;
    private readonly int _udpPort;
    private readonly int _broadcastIntervalSeconds;
    private readonly string _localPeerId;
    private readonly string _hostname;
    private readonly int _tcpPort;
    private readonly string? _authToken;
    private readonly ConcurrentBag<IPAddress> _localIPs = [];
    private bool _disposed;

    public event EventHandler<PeerDiscoveredEventArgs>? PeerDiscovered;
    public event EventHandler? NetworkChanged;

    public PeerDiscovery(AppConfig config, FileLogger logger)
    {
        _logger = logger;
        _udpPort = config.Discovery.UdpPort;
        _broadcastIntervalSeconds = config.Discovery.BroadcastIntervalSeconds;
        _tcpPort = config.Transfer.TcpPort;
        _authToken = config.Auth?.Token;
        _localPeerId = GetOrCreatePeerId();
        _hostname = Environment.MachineName;
        DiscoverLocalIPs();

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _logger.Info("Network address changed, re-discovering local IPs...");
        DiscoverLocalIPs();
        NetworkChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DiscoverLocalIPs()
    {
        _localIPs.Clear();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        _localIPs.Add(addr.Address);
                        _logger.Debug($"Found local IP: {addr.Address} on {ni.Name}");
                    }
                }
            }

            if (_localIPs.IsEmpty)
            {
                _localIPs.Add(IPAddress.Loopback);
                _logger.Warn("No non-loopback IP found, using 127.0.0.1");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not resolve local IPs: {ex.Message}");
            _localIPs.Add(IPAddress.Loopback);
        }
    }

    private static string GetOrCreatePeerId()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipboardSync", "data");
        var idFile = Path.Combine(appData, "peer.id");
        try
        {
            Directory.CreateDirectory(appData);
            if (File.Exists(idFile))
            {
                var id = File.ReadAllText(idFile).Trim();
                if (!string.IsNullOrEmpty(id)) return id;
            }
            var newId = Guid.NewGuid().ToString();
            File.WriteAllText(idFile, newId);
            return newId;
        }
        catch
        {
            return Guid.NewGuid().ToString();
        }
    }

    public string LocalPeerId => _localPeerId;
    public string LocalHostname => _hostname;

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();

        _listener = new UdpClient();
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);

        try
        {
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, _udpPort));
        }
        catch (SocketException ex)
        {
            _logger.Error($"Failed to bind UDP port {_udpPort}: {ex.Message}");
            return Task.CompletedTask;
        }

        _listener.EnableBroadcast = true;

        _logger.Info($"UDP discovery listener started on port {_udpPort}");

        _ = ListenLoop(_cts.Token);
        _ = BroadcastLoop(_cts.Token);

        return Task.CompletedTask;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _listener!.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var packet = JsonSerializer.Deserialize<DiscoveryPacket>(json);

                if (packet?.Type != "announce") continue;
                if (packet.PeerId == _localPeerId) continue;
                if (string.IsNullOrEmpty(packet.IpAddress)) continue;

                if (!string.IsNullOrEmpty(_authToken) && packet.AuthToken != _authToken)
                {
                    _logger.Debug($"Discovery packet rejected: auth token mismatch from {packet.Hostname}");
                    continue;
                }

                PeerDiscovered?.Invoke(this, new PeerDiscoveredEventArgs(new PeerInfo
                {
                    PeerId = packet.PeerId,
                    Hostname = packet.Hostname,
                    IpAddress = packet.IpAddress,
                    TcpPort = packet.TcpPort,
                    AuthToken = packet.AuthToken,
                    LastSeen = DateTime.UtcNow
                }));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Warn($"UDP listen error: {ex.Message}");
            }
        }
    }

    private async Task BroadcastLoop(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                BroadcastAnnounce();
                await Task.Delay(TimeSpan.FromSeconds(_broadcastIntervalSeconds), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Warn($"UDP broadcast error: {ex.Message}");
            }
        }
    }

    private void BroadcastAnnounce()
    {
        var packet = new DiscoveryPacket
        {
            Type = "announce",
            PeerId = _localPeerId,
            Hostname = _hostname,
            IpAddress = _localIPs.FirstOrDefault()?.ToString() ?? "0.0.0.0",
            TcpPort = _tcpPort,
            AuthToken = _authToken
        };

        var json = JsonSerializer.Serialize(packet);
        var data = Encoding.UTF8.GetBytes(json);

        foreach (var localIp in _localIPs)
        {
            try
            {
                using var client = new UdpClient();
                client.EnableBroadcast = true;
                if (localIp.Equals(IPAddress.Loopback))
                {
                    client.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, _udpPort));
                }
                else
                {
                    client.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, _udpPort));
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Broadcast on {localIp} failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _cts?.Cancel();
        _listener?.Dispose();
        _cts?.Dispose();
        _logger.Info("PeerDiscovery disposed.");
    }
}

public sealed class PeerDiscoveredEventArgs : EventArgs
{
    public PeerInfo Peer { get; }
    public PeerDiscoveredEventArgs(PeerInfo peer) => Peer = peer;
}
