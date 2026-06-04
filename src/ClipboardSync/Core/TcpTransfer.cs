using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipboardSync.Utils;

namespace ClipboardSync.Core;

public sealed class TcpTransfer : IDisposable
{
    private readonly FileLogger _logger;
    private readonly int _tcpPort;
    private readonly string _authToken;
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private readonly ConcurrentDictionary<string, TcpClient> _connections = new();
    private readonly ConcurrentDictionary<string, PeerInfo> _peerInfoMap = new();
    private readonly int _peerTimeoutSeconds;
    private CancellationTokenSource? _heartbeatCts;
    private CancellationTokenSource? _connectionCts;
    private readonly object _connLock = new();
    private readonly string _localHostname;
    private readonly string _localPeerId;
    private bool _disposed;

    private const int MaxClipboardBytes = 50 * 1024 * 1024;
    private const int MaxSecureFrameBytes = 75 * 1024 * 1024;
    private const int SendTimeoutMs = 5000;
    private const int ReceiveTimeoutMs = 10000;
    private const int MaxReconnectAttempts = 3;
    private const int MaxReconnectDelayMs = 30000;

    public event EventHandler<ClipboardReceivedEventArgs>? ClipboardReceived;

    public TcpTransfer(AppConfig config, FileLogger logger, PeerDiscovery discovery)
    {
        _logger = logger;
        _tcpPort = config.Transfer.TcpPort;
        _peerTimeoutSeconds = config.Discovery.PeerTimeoutSeconds;
        _authToken = config.Auth.RequireToken();
        _localHostname = discovery.LocalHostname;
        _localPeerId = discovery.LocalPeerId;
    }

    public Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Any, _tcpPort);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _listener.Start();
        _listenerCts = new CancellationTokenSource();
        _heartbeatCts = new CancellationTokenSource();
        _connectionCts = new CancellationTokenSource();

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
                    _ = ReceiveLoop(peer.PeerId, client, _connectionCts!.Token);
                    return;
                }
                client.Close();
            }
            catch (Exception ex)
            {
                _logger.Debug($"Connection attempt {attempt + 1}/{maxAttempts} to {peer.Hostname} failed: {ex.Message}");
                if (attempt < maxAttempts - 1)
                {
                    var delay = Math.Min(
                        (int)Math.Pow(2, attempt) * 1000,
                        MaxReconnectDelayMs);
                    await Task.Delay(delay);
                }
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
            var stream = client.GetStream();
            var headerLen = await ReadInt32Async(stream, ct);
            if (headerLen <= 0 || headerLen > MaxSecureFrameBytes)
            {
                _logger.Warn($"Invalid secure frame length: {headerLen}");
                return;
            }

            var jsonBytes = await ReadExactlyAsync(stream, headerLen, ct, 10000);
            var packet = DecodeSecureFrame(jsonBytes);
            if (packet == null) return;

            if (packet.Type == "heartbeat")
            {
                peerId = packet.SenderId;
                _peerInfoMap[peerId] = _peerInfoMap.GetValueOrDefault(peerId) ?? new PeerInfo
                    {
                        PeerId = peerId,
                        Hostname = packet.Hostname ?? "Unknown",
                        IpAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(),
                        TcpPort = packet.TcpPort
                    };
                if (_connections.TryAdd(peerId, client))
                {
                    _logger.Debug($"Incoming connection from {peerId} accepted.");
                    _ = ReceiveLoop(peerId, client, _connectionCts!.Token);
                }
                return;
            }

            if (packet.Type == "clipboard")
            {
                if (packet.Size > MaxClipboardBytes ||
                    (packet.ImageData?.Length ?? 0) > MaxClipboardBytes)
                {
                    _logger.Warn($"Clipboard payload too large: {packet.Size} bytes, max {MaxClipboardBytes}");
                    return;
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
                    packet.TextContent, packet.ImageData,
                    packet.FilePaths, packet.SenderId, packet.IsApplyingRemote));
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
                _logger.Warn($"Handle incoming connection error: {ex.Message}");
        }
    }

    private async Task ReceiveLoop(string peerId, TcpClient client, CancellationToken ct)
    {
        var lastHeartbeat = DateTime.UtcNow;
        try
        {
            using var stream = client.GetStream();
            while (client.Connected && !ct.IsCancellationRequested)
            {
                try
                {
                    var headerLen = await ReadInt32Async(stream, ct);
                    if (headerLen <= 0 || headerLen > MaxSecureFrameBytes)
                    {
                        _logger.Debug($"[{peerId}] Invalid secure frame length {headerLen}, closing.");
                        break;
                    }

                    var jsonBytes = await ReadExactlyAsync(stream, headerLen, ct, 10000);
                    var packet = DecodeSecureFrame(jsonBytes);
                    if (packet == null) break;
                    if (packet.SenderId != peerId)
                    {
                        _logger.Debug($"[{peerId}] Secure frame sender mismatch, closing.");
                        break;
                    }

                    if (packet.Type == "heartbeat")
                    {
                        lastHeartbeat = DateTime.UtcNow;
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
                        if (packet.Size > MaxClipboardBytes ||
                            (packet.ImageData?.Length ?? 0) > MaxClipboardBytes)
                        {
                            _logger.Warn($"[{peerId}] Clipboard payload too large: {packet.Size} bytes");
                            break;
                        }

                        ClipboardReceived?.Invoke(this, new ClipboardReceivedEventArgs(
                            packet.Hash, packet.Format,
                            packet.TextContent, packet.ImageData,
                            packet.FilePaths, packet.SenderId, packet.IsApplyingRemote));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (EndOfStreamException)
                {
                    _logger.Debug($"[{peerId}] Connection closed by remote.");
                    break;
                }
                catch (IOException ex)
                {
                    _logger.Debug($"[{peerId}] IO error: {ex.Message}");
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.Debug($"[{peerId}] Socket error: {ex.Message}");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug($"[{peerId}] ReceiveLoop cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Debug($"[{peerId}] Unexpected error in ReceiveLoop: {ex.Message}");
        }
        finally
        {
            ClosePeerConnection(peerId);
            _logger.Info($"Connection to peer {peerId} closed.");

            if (_peerInfoMap.TryGetValue(peerId, out var peer) && !_disposed)
            {
                _ = Task.Delay(3000).ContinueWith(_ =>
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
                    SenderId = _localPeerId,
                    Hostname = _localHostname,
                    TcpPort = _tcpPort
                };
                var headerBytes = EncodeSecureFrame(packet);
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

        var headerBytes = EncodeSecureFrame(packet);
        var lenBytes = BitConverter.GetBytes(headerBytes.Length);

        // Snapshot connections under lock to avoid modification during iteration
        List<(string PeerId, TcpClient Client)> snapshot;
        lock (_connLock)
        {
            snapshot = _connections.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        var tasks = snapshot
            .Where(kv => kv.Client.Connected)
            .Select(kv => SendToClientAsync(kv.Client, kv.PeerId, lenBytes, headerBytes, packet))
            .ToList();

        if (tasks.Count == 0) return;
        try { await Task.WhenAll(tasks); } catch { }
    }

    private async Task SendToClientAsync(TcpClient client, string peerId, byte[] headerLen, byte[] headerBytes, ClipboardPacket packet)
    {
        try
        {
            var stream = client.GetStream();
            await stream.WriteAsync(headerLen);
            await stream.WriteAsync(headerBytes);
        }
        catch (Exception)
        {
            // Connection will be cleaned up by heartbeat
        }
    }

    private byte[] EncodeSecureFrame(ClipboardPacket packet)
    {
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
        var encrypted = SharedSecretAuth.Encrypt(_authToken, plaintext);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var frame = new SecureFrame
        {
            Type = packet.Type,
            SenderId = packet.SenderId,
            Timestamp = timestamp,
            Proof = SharedSecretAuth.CreateProof(_authToken, packet.SenderId, timestamp),
            Nonce = encrypted.Nonce,
            Ciphertext = encrypted.Ciphertext,
            Tag = encrypted.Tag
        };

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame));
    }

    private ClipboardPacket? DecodeSecureFrame(byte[] frameBytes)
    {
        SecureFrame? frame;
        try
        {
            frame = JsonSerializer.Deserialize<SecureFrame>(Encoding.UTF8.GetString(frameBytes));
        }
        catch (JsonException ex)
        {
            _logger.Debug($"Secure frame rejected: invalid JSON ({ex.Message})");
            return null;
        }

        if (frame == null ||
            string.IsNullOrWhiteSpace(frame.SenderId) ||
            string.IsNullOrWhiteSpace(frame.Proof) ||
            frame.Nonce == null ||
            frame.Ciphertext == null ||
            frame.Tag == null ||
            frame.Nonce.Length != 12 ||
            frame.Tag.Length != 16)
        {
            _logger.Debug("Secure frame rejected: missing required fields.");
            return null;
        }

        if (!SharedSecretAuth.VerifyProof(_authToken, frame.SenderId, frame.Timestamp, frame.Proof, TimeSpan.FromMinutes(2)))
        {
            _logger.Debug($"Secure frame rejected: proof mismatch from sender {frame.SenderId}");
            return null;
        }

        try
        {
            var plaintext = SharedSecretAuth.Decrypt(
                _authToken,
                new EncryptedPayload(frame.Nonce, frame.Ciphertext, frame.Tag));
            var packet = JsonSerializer.Deserialize<ClipboardPacket>(Encoding.UTF8.GetString(plaintext));
            if (packet?.SenderId != frame.SenderId || packet.Type != frame.Type)
            {
                _logger.Debug("Secure frame rejected: decrypted packet metadata mismatch.");
                return null;
            }

            return packet;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            _logger.Debug($"Secure frame rejected: decrypt/deserialize failed ({ex.Message})");
            return null;
        }
    }

    private static async Task<int> ReadInt32Async(NetworkStream stream, CancellationToken ct)
    {
        var buf = await ReadExactlyAsync(stream, 4, ct, timeoutMs: 10000);
        return BitConverter.ToInt32(buf, 0);
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct, int timeoutMs = 10000)
    {
        if (count == 0) return [];

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (stream.DataAvailable)
            {
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
            await Task.Delay(20, ct);
        }

        throw new TimeoutException($"ReadExactlyAsync timed out after {timeoutMs}ms waiting for {count} bytes");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listenerCts?.Cancel();
        _heartbeatCts?.Cancel();
        _connectionCts?.Cancel();

        foreach (var c in _connections.Values)
        {
            try { c.Close(); } catch { }
        }
        _connections.Clear();
        _listener?.Stop();
        _listenerCts?.Dispose();
        _heartbeatCts?.Dispose();
        _connectionCts?.Dispose();
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
