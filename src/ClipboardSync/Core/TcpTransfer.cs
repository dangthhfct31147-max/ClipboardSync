using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClipboardSync.Utils;

namespace ClipboardSync.Core;

public sealed class TcpTransfer : IDisposable
{
    private readonly FileLogger _logger;
    private readonly int _tcpPort;
    private readonly string? _authToken;
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private readonly ConcurrentDictionary<string, TcpClient> _connections = new();
    private readonly ConcurrentDictionary<string, PeerInfo> _peerInfoMap = new();
    private readonly int _peerTimeoutSeconds;
    private CancellationTokenSource? _heartbeatCts;
    private readonly object _connLock = new();
    private readonly string _localHostname;
    private bool _disposed;

    private const int MaxClipboardBytes = 50 * 1024 * 1024;
    private const int SendTimeoutMs = 5000;
    private const int ReceiveTimeoutMs = 10000;

    public event EventHandler<ClipboardReceivedEventArgs>? ClipboardReceived;

    public TcpTransfer(AppConfig config, FileLogger logger, PeerDiscovery discovery)
    {
        _logger = logger;
        _tcpPort = config.Transfer.TcpPort;
        _peerTimeoutSeconds = config.Discovery.PeerTimeoutSeconds;
        _authToken = config.Auth?.Token;
        _localHostname = discovery.LocalHostname;
    }

    public Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Any, _tcpPort);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _listener.Start();
        _listenerCts = new CancellationTokenSource();
        _heartbeatCts = new CancellationTokenSource();

        _logger.Info($"TCP listener started on port {_tcpPort}");

        _ = AcceptLoop(_listenerCts.Token);
        _ = HeartbeatLoop(_heartbeatCts.Token);

        return Task.CompletedTask;
    }

    public void RegisterPeer(PeerInfo peer)
    {
        _peerInfoMap[peer.PeerId] = peer;
        _ = ConnectToPeerWithReconnectAsync(peer);
    }

    public void UnregisterPeer(string peerId)
    {
        _peerInfoMap.TryRemove(peerId, out _);
        ClosePeerConnection(peerId);
    }

    private void ClosePeerConnection(string peerId)
    {
        if (_connections.TryRemove(peerId, out var client))
        {
            try { client.Close(); } catch { }
        }
    }

    private async Task ConnectToPeerWithReconnectAsync(PeerInfo peer, int maxAttempts = 3)
    {
        for (int attempt = 0; attempt < maxAttempts && !_disposed; attempt++)
        {
            if (_connections.ContainsKey(peer.PeerId)) return;

            try
            {
                var client = new TcpClient();
                client.SendTimeout = SendTimeoutMs;
                client.ReceiveTimeout = ReceiveTimeoutMs;

                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(peer.IpAddress, peer.TcpPort, connectCts.Token);

                if (_connections.TryAdd(peer.PeerId, client))
                {
                    _logger.Info($"Connected to peer {peer.Hostname} ({peer.IpAddress}:{peer.TcpPort})");
                    _ = ReceiveLoop(peer.PeerId, client);
                    return;
                }
                client.Close();
            }
            catch (Exception ex)
            {
                _logger.Debug($"Connection attempt {attempt + 1}/{maxAttempts} to {peer.Hostname} failed: {ex.Message}");
                if (attempt < maxAttempts - 1)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }

        _logger.Warn($"Failed to connect to peer {peer.Hostname} after {maxAttempts} attempts");
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                client.SendTimeout = SendTimeoutMs;
                client.ReceiveTimeout = ReceiveTimeoutMs;
                client.NoDelay = true;
                _ = HandleIncomingConnection(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!_disposed)
                    _logger.Warn($"Accept loop error: {ex.Message}");
            }
        }
    }

    private async Task HandleIncomingConnection(TcpClient client, CancellationToken ct)
    {
        string? peerId = null;
        try
        {
            using var stream = client.GetStream();
            var headerLen = await ReadInt32Async(stream, ct);
            if (headerLen <= 0 || headerLen > 65536)
            {
                _logger.Warn($"Invalid header length: {headerLen}");
                return;
            }

            var jsonBytes = await ReadExactlyAsync(stream, headerLen, ct);
            var json = Encoding.UTF8.GetString(jsonBytes);
            var packet = JsonSerializer.Deserialize<ClipboardPacket>(json);

            if (packet == null)
            {
                _logger.Warn("Failed to deserialize packet header");
                return;
            }

            if (!string.IsNullOrEmpty(_authToken) && packet.Token != _authToken)
            {
                _logger.Debug($"TCP connection rejected: auth token mismatch from sender {packet.SenderId}");
                return;
            }

            if (packet.Type == "heartbeat")
            {
                peerId = packet.SenderId;
                _peerInfoMap[peerId] = _peerInfoMap.GetValueOrDefault(peerId) ?? new PeerInfo
                {
                    PeerId = peerId,
                    Hostname = "Unknown",
                    IpAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(),
                    TcpPort = 0
                };
                if (_connections.TryAdd(peerId, client))
                {
                    _logger.Debug($"Incoming heartbeat connection from {peerId} accepted.");
                    _ = ReceiveLoop(peerId, client);
                }
                return;
            }

            if (packet.Type == "clipboard")
            {
                if (packet.Size > MaxClipboardBytes)
                {
                    _logger.Warn($"Clipboard payload too large: {packet.Size} bytes, max {MaxClipboardBytes}");
                    return;
                }

                byte[] payload = [];
                if (packet.Size > 0)
                {
                    payload = await ReadExactlyAsync(stream, (int)packet.Size, ct);
                }

                var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                if (!_connections.ContainsKey(packet.SenderId))
                    _connections[packet.SenderId] = client;
                if (!_peerInfoMap.ContainsKey(packet.SenderId))
                {
                    _peerInfoMap[packet.SenderId] = new PeerInfo
                    {
                        PeerId = packet.SenderId,
                        Hostname = packet.Hostname ?? "Unknown",
                        IpAddress = remoteIp,
                        TcpPort = packet.TcpPort > 0 ? packet.TcpPort : 51235,
                        LastSeen = DateTime.UtcNow
                    };
                }

                ClipboardReceived?.Invoke(this, new ClipboardReceivedEventArgs(
                    packet.Hash, packet.Format,
                    packet.TextContent, packet.ImageData ?? payload,
                    packet.FilePaths, packet.SenderId, packet.IsApplyingRemote));
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
                _logger.Warn($"Handle incoming connection error: {ex.Message}");
        }
    }

    private async Task ReceiveLoop(string peerId, TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            while (client.Connected && !_disposed)
            {
                try
                {
                    var headerLen = await ReadInt32Async(stream, CancellationToken.None);
                    if (headerLen <= 0 || headerLen > 65536)
                    {
                        break;
                    }

                    var jsonBytes = await ReadExactlyAsync(stream, headerLen, CancellationToken.None);
                    var json = Encoding.UTF8.GetString(jsonBytes);
                    var packet = JsonSerializer.Deserialize<ClipboardPacket>(json);
                    if (packet == null) break;

                    if (packet.Type == "heartbeat")
                    {
                        if (!_connections.ContainsKey(peerId))
                        {
                            _connections[peerId] = client;
                        }
                        if (_peerInfoMap.TryGetValue(peerId, out var existing) && existing.Hostname == "Unknown")
                        {
                            _peerInfoMap[peerId] = existing with
                            {
                                Hostname = packet.Hostname ?? existing.Hostname,
                                IpAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(),
                                TcpPort = packet.TcpPort > 0 ? packet.TcpPort : existing.TcpPort
                            };
                        }
                        else if (!_peerInfoMap.ContainsKey(peerId))
                        {
                            _peerInfoMap[peerId] = new PeerInfo
                            {
                                PeerId = peerId,
                                Hostname = packet.Hostname ?? "Unknown",
                                IpAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(),
                                TcpPort = packet.TcpPort > 0 ? packet.TcpPort : 51235,
                                LastSeen = DateTime.UtcNow
                            };
                        }
                        continue;
                    }

                    if (packet.Type == "clipboard")
                    {
                        byte[] payload = [];
                        if (packet.Size > 0)
                        {
                            if (packet.Size > MaxClipboardBytes) break;
                            payload = await ReadExactlyAsync(stream, (int)packet.Size, CancellationToken.None);
                        }
                        ClipboardReceived?.Invoke(this, new ClipboardReceivedEventArgs(
                            packet.Hash, packet.Format,
                            packet.TextContent, packet.ImageData ?? payload,
                            packet.FilePaths, packet.SenderId, packet.IsApplyingRemote));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (EndOfStreamException) { break; }
                catch (IOException) { break; }
                catch (SocketException) { break; }
            }
        }
        catch { }
        finally
        {
            ClosePeerConnection(peerId);
            _logger.Info($"Connection to peer {peerId} closed.");

            if (_peerInfoMap.TryGetValue(peerId, out var peer) && !_disposed)
            {
                _ = Task.Delay(5000).ContinueWith(_ =>
                {
                    if (!_disposed && !_connections.ContainsKey(peerId))
                        _ = ConnectToPeerWithReconnectAsync(peer);
                });
            }
        }
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_peerTimeoutSeconds / 2.0), ct);

                var packet = new ClipboardPacket
                {
                    Type = "heartbeat",
                    Hash = "",
                    Format = ClipboardFormat.Text,
                    Size = 0,
                    SenderId = "",
                    Hostname = _localHostname,
                    TcpPort = _tcpPort,
                    Token = _authToken
                };
                var json = JsonSerializer.Serialize(packet);
                var headerBytes = Encoding.UTF8.GetBytes(json);
                var lenBytes = BitConverter.GetBytes(headerBytes.Length);

                List<string> deadConnections = [];

                foreach (var (peerId, client) in _connections)
                {
                    try
                    {
                        if (!client.Connected) continue;
                        var stream = client.GetStream();
                        await stream.WriteAsync(lenBytes);
                        await stream.WriteAsync(headerBytes);
                    }
                    catch
                    {
                        deadConnections.Add(peerId);
                    }
                }

                foreach (var deadId in deadConnections)
                    ClosePeerConnection(deadId);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!_disposed)
                    _logger.Warn($"Heartbeat error: {ex.Message}");
            }
        }
    }

    public async Task SendClipboardAsync(ClipboardPacket packet)
    {
        if (packet.Size > MaxClipboardBytes)
        {
            _logger.Warn($"Clipboard too large to send: {packet.Size} bytes");
            return;
        }

        var json = JsonSerializer.Serialize(packet);
        var headerBytes = Encoding.UTF8.GetBytes(json);
        var lenBytes = BitConverter.GetBytes(headerBytes.Length);
        var tasks = new List<Task>();

        foreach (var (peerId, client) in _connections)
        {
            if (!client.Connected) continue;
            tasks.Add(Task.Run(() => SendToClient(client, peerId, lenBytes, headerBytes, packet)));
        }

        try { await Task.WhenAll(tasks); } catch { }
    }

    private static async Task SendToClient(TcpClient client, string peerId, byte[] headerLen, byte[] headerBytes, ClipboardPacket packet)
    {
        try
        {
            var stream = client.GetStream();
            await stream.WriteAsync(headerLen);
            await stream.WriteAsync(headerBytes);

            if (packet.ImageData?.Length > 0)
            {
                await stream.WriteAsync(packet.ImageData);
            }
        }
        catch (Exception)
        {
            // Connection will be cleaned up by heartbeat
        }
    }

    private static async Task<int> ReadInt32Async(NetworkStream stream, CancellationToken ct)
    {
        var buf = await ReadExactlyAsync(stream, 4, ct);
        return BitConverter.ToInt32(buf, 0);
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        if (count == 0) return [];
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            var n = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
        return buf;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listenerCts?.Cancel();
        _heartbeatCts?.Cancel();

        foreach (var c in _connections.Values)
        {
            try { c.Close(); } catch { }
        }
        _connections.Clear();
        _listener?.Stop();
        _listenerCts?.Dispose();
        _heartbeatCts?.Dispose();
        _logger.Info("TcpTransfer disposed.");
    }
}

public sealed class ClipboardReceivedEventArgs : EventArgs
{
    public string Hash { get; }
    public ClipboardFormat Format { get; }
    public string? TextContent { get; }
    public byte[]? ImageData { get; }
    public List<string>? FilePaths { get; }
    public string SenderId { get; }
    public bool IsApplyingRemote { get; }

    public ClipboardReceivedEventArgs(string hash, ClipboardFormat format, string? text, byte[]? image, List<string>? files, string senderId, bool isApplyingRemote = false)
    {
        Hash = hash;
        Format = format;
        TextContent = text;
        ImageData = image;
        FilePaths = files;
        SenderId = senderId;
        IsApplyingRemote = isApplyingRemote;
    }
}
