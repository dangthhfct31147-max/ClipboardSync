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
    private readonly string _authToken;
    private readonly string _groupId;
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
        _authToken = config.Auth.RequireToken();
        _groupId = SharedSecretAuth.CreateGroupId(_authToken);
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
                if (IsIgnoredNetworkInterface(ni.Name, ni.Description, ni.NetworkInterfaceType))
                {
                    _logger.Debug($"Ignoring non-LAN adapter: {ni.Name} ({ni.Description})");
                    continue;
                }

                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address) &&
                        !addr.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
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
                if (IsLocalAddress(packet.IpAddress, _localIPs) ||
                    _localIPs.Any(ip => ip.Equals(result.RemoteEndPoint.Address)))
                {
                    _logger.Debug($"Discovery packet ignored from local address {packet.IpAddress}");
                    continue;
                }

                if (packet.GroupId != _groupId ||
                    !SharedSecretAuth.VerifyProof(_authToken, packet.PeerId, packet.Timestamp, packet.Proof, TimeSpan.FromMinutes(2)))
                {
                    _logger.Debug($"Discovery packet rejected: auth proof mismatch from {packet.Hostname}");
                    continue;
                }

                PeerDiscovered?.Invoke(this, new PeerDiscoveredEventArgs(new PeerInfo
                {
                    PeerId = packet.PeerId,
                    Hostname = packet.Hostname,
                    IpAddress = packet.IpAddress,
                    TcpPort = packet.TcpPort,
                    GroupId = packet.GroupId,
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
        foreach (var localIp in _localIPs)
        {
            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var packet = new DiscoveryPacket
                {
                    Type = "announce",
                    PeerId = _localPeerId,
                    Hostname = _hostname,
                    IpAddress = localIp.ToString(),
                    TcpPort = _tcpPort,
                    GroupId = _groupId,
                    Proof = SharedSecretAuth.CreateProof(_authToken, _localPeerId, timestamp),
                    Timestamp = timestamp
                };
                var json = JsonSerializer.Serialize(packet);
                var data = Encoding.UTF8.GetBytes(json);

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

    public static bool IsIgnoredNetworkInterface(string name, string description, NetworkInterfaceType type)
    {
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            return true;

        var text = $"{name} {description}".ToLowerInvariant();
        string[] blocked =
        [
            "kaspersky",
            "vpn",
            "wireguard",
            "wg",
            "vethernet",
            "hyper-v",
            "virtual",
            "default switch",
            "wsl",
            "docker",
            "radmin",
            "tailscale",
            "zerotier",
            "loopback"
        ];

        return blocked.Any(text.Contains);
    }

    public static bool IsLocalAddress(string? ipAddress, IEnumerable<IPAddress> localAddresses)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) return false;
        if (!IPAddress.TryParse(ipAddress, out var parsed)) return false;
        return localAddresses.Any(local => local.Equals(parsed));
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
