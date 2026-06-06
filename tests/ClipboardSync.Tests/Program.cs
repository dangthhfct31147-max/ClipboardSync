using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Drawing;
using System.Windows.Forms;
using ClipboardSync.Tray;
using ClipboardSync.Core;
using ClipboardSync;
using ClipboardSync.Utils;

var tests = new (string Name, Func<Task> Body)[]
{
    ("Discovery group id is stable and does not reveal the token", RunSync(DiscoveryGroupIdIsStable)),
    ("Proof verifies only for the matching token and peer", RunSync(ProofRequiresMatchingTokenAndPeer)),
    ("Encryption round-trips and rejects tampering", RunSync(EncryptionRoundTripsAndRejectsTampering)),
    ("Discovery ignores VPN and virtual adapters", RunSync(DiscoveryIgnoresVpnAndVirtualAdapters)),
    ("Discovery identifies local self addresses", RunSync(DiscoveryIdentifiesLocalSelfAddresses)),
    ("Peer manager can refresh a peer from TCP liveness", RunSync(PeerManagerCanRefreshPeerFromTcpLiveness)),
    ("Single instance guard blocks a second running instance", SingleInstanceGuardBlocksSecondRunningInstance),
    ("Tray icon color reflects peer connection state", RunSync(TrayIconColorReflectsPeerConnectionState)),
    ("Remote clipboard update can be applied from a background thread", RemoteClipboardUpdateCanBeAppliedFromBackgroundThread),
    ("Rapid screenshot image updates are coalesced into one event", RapidScreenshotImageUpdatesAreCoalescedIntoOneEvent),
    ("Outgoing TCP connection sends an initial heartbeat frame", OutgoingTcpConnectionSendsInitialHeartbeatFrame),
    ("TCP heartbeat reports peer liveness", TcpHeartbeatReportsPeerLiveness),
    ("Inbound TCP connection stays open past the default heartbeat interval", InboundTcpConnectionStaysOpenPastDefaultHeartbeatInterval),
    ("Simultaneous TCP peer connections still deliver clipboard payloads", SimultaneousTcpPeerConnectionsStillDeliverClipboardPayloads)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failed > 0)
{
    Environment.ExitCode = 1;
}

Func<Task> RunSync(Action action) => () =>
{
    action();
    return Task.CompletedTask;
};

void DiscoveryGroupIdIsStable()
{
    const string token = "secret-token-for-two-windows-machines";

    var first = SharedSecretAuth.CreateGroupId(token);
    var second = SharedSecretAuth.CreateGroupId(token);

    AssertEqual(first, second, "group id should be deterministic");
    AssertNotContains(token, first, "group id should not contain the raw token");
    AssertEqual(43, first.Length, "group id should be a 256-bit base64url value without padding");
}

void ProofRequiresMatchingTokenAndPeer()
{
    const string token = "secret-token-for-two-windows-machines";
    const string peerId = "peer-a";
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    var proof = SharedSecretAuth.CreateProof(token, peerId, timestamp);

    AssertTrue(SharedSecretAuth.VerifyProof(token, peerId, timestamp, proof, TimeSpan.FromMinutes(2)), "valid proof should verify");
    AssertFalse(SharedSecretAuth.VerifyProof("wrong-token", peerId, timestamp, proof, TimeSpan.FromMinutes(2)), "wrong token should fail");
    AssertFalse(SharedSecretAuth.VerifyProof(token, "peer-b", timestamp, proof, TimeSpan.FromMinutes(2)), "wrong peer should fail");
}

void EncryptionRoundTripsAndRejectsTampering()
{
    const string token = "secret-token-for-two-windows-machines";
    var plaintext = Encoding.UTF8.GetBytes("""{"Type":"clipboard","TextContent":"hello"}""");

    var encrypted = SharedSecretAuth.Encrypt(token, plaintext);
    var decrypted = SharedSecretAuth.Decrypt(token, encrypted);

    AssertEqual("hello", Encoding.UTF8.GetString(decrypted).Contains("hello") ? "hello" : "missing", "decrypted plaintext should match");

    encrypted.Ciphertext[0] ^= 0x01;
    AssertThrows<CryptographicException>(() => SharedSecretAuth.Decrypt(token, encrypted), "tampered ciphertext should fail authentication");
}

void DiscoveryIgnoresVpnAndVirtualAdapters()
{
    AssertTrue(
        PeerDiscovery.IsIgnoredNetworkInterface("Kaspersky VPN", "Kaspersky VPN", NetworkInterfaceType.Ethernet),
        "Kaspersky VPN adapter should be ignored");
    AssertTrue(
        PeerDiscovery.IsIgnoredNetworkInterface("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet),
        "Hyper-V virtual adapter should be ignored");
    AssertFalse(
        PeerDiscovery.IsIgnoredNetworkInterface("Wi-Fi 2", "Realtek RTL8852BE WiFi 6 802.11ax PCIe Adapter", NetworkInterfaceType.Wireless80211),
        "physical Wi-Fi adapter should be usable");
}

void DiscoveryIdentifiesLocalSelfAddresses()
{
    var local = new[] { IPAddress.Parse("192.168.2.53"), IPAddress.Parse("172.29.144.1") };

    AssertTrue(PeerDiscovery.IsLocalAddress("192.168.2.53", local), "known local address should be treated as self");
    AssertFalse(PeerDiscovery.IsLocalAddress("192.168.2.100", local), "remote LAN address should not be treated as self");
}

void TrayIconColorReflectsPeerConnectionState()
{
    AssertEqual(Color.FromArgb(30, 64, 175), TrayIconManager.GetIconBackColorForPeerCount(0), "tray icon should be blue before peers connect");
    AssertEqual(Color.FromArgb(22, 163, 74), TrayIconManager.GetIconBackColorForPeerCount(1), "tray icon should turn green after a peer connects");
    AssertEqual(Color.FromArgb(22, 163, 74), TrayIconManager.GetIconBackColorForPeerCount(2), "tray icon should stay green while any peer is connected");
}

void PeerManagerCanRefreshPeerFromTcpLiveness()
{
    var config = CreateTestConfig(tcpPort: 51235);
    var logPath = Path.Combine(Path.GetTempPath(), $"clipboardsync-tests-{Guid.NewGuid():N}.log");
    var logger = new FileLogger(logPath);
    using var discovery = new PeerDiscovery(config, logger);
    using var manager = new PeerManager(config, logger, discovery);

    var connectedCount = 0;
    manager.PeerConnected += (_, _) => connectedCount++;

    var stalePeer = new PeerInfo
    {
        PeerId = "remote-peer",
        Hostname = "remote",
        IpAddress = IPAddress.Loopback.ToString(),
        TcpPort = 51235,
        LastSeen = DateTime.UtcNow.AddMinutes(-5)
    };

    manager.RegisterOrUpdatePeer(stalePeer);
    manager.RegisterOrUpdatePeer(stalePeer with { LastSeen = DateTime.UtcNow.AddMinutes(-4) });

    AssertEqual(1, manager.PeerCount, "peer manager should contain the TCP-seen peer");
    AssertEqual(1, connectedCount, "refreshing an existing peer should not emit duplicate connected events");
    AssertTrue(manager.GetPeers().Single().LastSeen > DateTime.UtcNow.AddSeconds(-5), "TCP liveness should refresh LastSeen to now");
}

async Task SingleInstanceGuardBlocksSecondRunningInstance()
{
    var mutexName = $"Local\\ClipboardSync.Tests.{Guid.NewGuid():N}";
    using (var first = SingleInstanceGuard.TryAcquire(mutexName))
    {
        AssertTrue(first.HasHandle, "first instance should acquire the mutex");
        var secondHasHandle = await Task.Run(() =>
        {
            using var second = SingleInstanceGuard.TryAcquire(mutexName);
            return second.HasHandle;
        });
        AssertFalse(secondHasHandle, "second instance should not acquire the same mutex");
    }

    using var third = SingleInstanceGuard.TryAcquire(mutexName);
    AssertTrue(third.HasHandle, "mutex should be available again after the first instance exits");
}

async Task RemoteClipboardUpdateCanBeAppliedFromBackgroundThread()
{
    var config = CreateTestConfig(tcpPort: 51235);
    var logPath = Path.Combine(Path.GetTempPath(), $"clipboardsync-tests-{Guid.NewGuid():N}.log");
    var logger = new FileLogger(logPath);
    using var monitor = new ClipboardMonitor(config, logger);
    var text = $"ClipboardSync background update {Guid.NewGuid():N}";

    monitor.Start();
    await Task.Delay(500);

    var apartment = ApartmentState.Unknown;
    var updateTask = Task.Run(() =>
    {
        apartment = Thread.CurrentThread.GetApartmentState();
        monitor.UpdateClipboardSilently(text, image: null, files: null);
    });

    await AwaitTaskWithTimeout(updateTask, TimeSpan.FromSeconds(5));
    AssertFalse(apartment == ApartmentState.STA, "test should call UpdateClipboardSilently from a non-STA worker thread");
    AssertFalse(string.IsNullOrWhiteSpace(monitor.LastHash), "background clipboard update should update LastHash");
}

async Task RapidScreenshotImageUpdatesAreCoalescedIntoOneEvent()
{
    var config = CreateTestConfig(tcpPort: 51235);
    var logPath = Path.Combine(Path.GetTempPath(), $"clipboardsync-tests-{Guid.NewGuid():N}.log");
    var logger = new FileLogger(logPath);
    using var monitor = new ClipboardMonitor(config, logger);
    var imageEvents = 0;
    var firstEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    monitor.ClipboardChanged += (_, e) =>
    {
        if (e.Format != ClipboardFormat.Image) return;
        Interlocked.Increment(ref imageEvents);
        firstEvent.TrySetResult();
    };

    monitor.Start();
    await Task.Delay(500);

    await RunStaAsync(() =>
    {
        using var first = CreateTestBitmap(Color.Red);
        Clipboard.SetImage(first);
        Thread.Sleep(250);
        using var second = CreateTestBitmap(Color.Green);
        Clipboard.SetImage(second);
    });

    await AwaitTaskWithTimeout(firstEvent.Task, TimeSpan.FromSeconds(5));
    await Task.Delay(TimeSpan.FromSeconds(1));
    AssertEqual(1, imageEvents, "rapid screenshot image clipboard updates should emit one image event");
}

async Task OutgoingTcpConnectionSendsInitialHeartbeatFrame()
{
    const string token = "secret-token-for-two-windows-machines";
    var remoteListener = new TcpListener(IPAddress.Loopback, 0);
    var localListener = new TcpListener(IPAddress.Loopback, 0);
    remoteListener.Start();
    localListener.Start();
    var remotePort = ((IPEndPoint)remoteListener.LocalEndpoint).Port;
    var localPort = ((IPEndPoint)localListener.LocalEndpoint).Port;
    localListener.Stop();

    var config = CreateTestConfig(localPort);

    var logPath = Path.Combine(Path.GetTempPath(), $"clipboardsync-tests-{Guid.NewGuid():N}.log");
    var logger = new FileLogger(logPath);
    using var discovery = new PeerDiscovery(config, logger);
    using var transfer = new TcpTransfer(config, logger, discovery);

    await transfer.StartAsync();
    try
    {
        transfer.RegisterPeer(new PeerInfo
        {
            PeerId = "remote-peer",
            Hostname = "remote",
            IpAddress = IPAddress.Loopback.ToString(),
            TcpPort = remotePort
        });

        using var serverClient = await AwaitWithTimeout(remoteListener.AcceptTcpClientAsync(), TimeSpan.FromSeconds(2));
        var stream = serverClient.GetStream();
        var lenBytes = await ReadExactlyForTestAsync(stream, 4, TimeSpan.FromSeconds(2));
        var frameLength = BitConverter.ToInt32(lenBytes, 0);
        AssertTrue(frameLength > 0, "initial heartbeat frame should have a positive length");

        var frameBytes = await ReadExactlyForTestAsync(stream, frameLength, TimeSpan.FromSeconds(2));
        var frame = JsonSerializer.Deserialize<SecureFrame>(Encoding.UTF8.GetString(frameBytes));
        AssertEqual("heartbeat", frame?.Type, "initial frame should be a heartbeat");
        AssertTrue(SharedSecretAuth.VerifyProof(token, frame!.SenderId, frame.Timestamp, frame.Proof, TimeSpan.FromMinutes(2)), "initial heartbeat proof should verify");
    }
    finally
    {
        remoteListener.Stop();
    }
}

async Task TcpHeartbeatReportsPeerLiveness()
{
    const string token = "secret-token-for-two-windows-machines";
    var localListener = new TcpListener(IPAddress.Loopback, 0);
    localListener.Start();
    var localPort = ((IPEndPoint)localListener.LocalEndpoint).Port;
    localListener.Stop();

    var config = CreateTestConfig(localPort);
    var logPath = Path.Combine(Path.GetTempPath(), $"clipboardsync-tests-{Guid.NewGuid():N}.log");
    var logger = new FileLogger(logPath);
    using var discovery = new PeerDiscovery(config, logger);
    using var transfer = new TcpTransfer(config, logger, discovery);
    using var client = new TcpClient();
    var peerSeen = new TaskCompletionSource<PeerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
    transfer.PeerSeen += (_, peer) => peerSeen.TrySetResult(peer);

    await transfer.StartAsync();
    await client.ConnectAsync(IPAddress.Loopback, localPort);
    await WriteHeartbeatFrameForTestAsync(client.GetStream(), token, "remote-peer", 51235);
    var peer = await AwaitWithTimeout(peerSeen.Task, TimeSpan.FromSeconds(2));

    AssertEqual("remote-peer", peer.PeerId, "heartbeat should report the sender peer id");
    AssertEqual("remote", peer.Hostname, "heartbeat should report the sender hostname");
    AssertEqual(51235, peer.TcpPort, "heartbeat should report the sender TCP port");
}

async Task InboundTcpConnectionStaysOpenPastDefaultHeartbeatInterval()
{
    const string token = "secret-token-for-two-windows-machines";
    var localListener = new TcpListener(IPAddress.Loopback, 0);
    localListener.Start();
    var localPort = ((IPEndPoint)localListener.LocalEndpoint).Port;
    localListener.Stop();

    var config = CreateTestConfig(localPort);

    var logPath = Path.Combine(Path.GetTempPath(), $"clipboardsync-tests-{Guid.NewGuid():N}.log");
    var logger = new FileLogger(logPath);
    using var discovery = new PeerDiscovery(config, logger);
    using var transfer = new TcpTransfer(config, logger, discovery);
    using var client = new TcpClient();

    await transfer.StartAsync();
    await client.ConnectAsync(IPAddress.Loopback, localPort);
    await WriteHeartbeatFrameForTestAsync(client.GetStream(), token, "remote-peer", 51235);
    await Task.Delay(TimeSpan.FromSeconds(11));

    var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : "";
    AssertFalse(log.Contains("Connection to peer remote-peer closed.", StringComparison.Ordinal), "inbound connection should remain open while waiting for the next heartbeat");
}

async Task SimultaneousTcpPeerConnectionsStillDeliverClipboardPayloads()
{
    var firstProbe = new TcpListener(IPAddress.Loopback, 0);
    var secondProbe = new TcpListener(IPAddress.Loopback, 0);
    firstProbe.Start();
    secondProbe.Start();
    var firstPort = ((IPEndPoint)firstProbe.LocalEndpoint).Port;
    var secondPort = ((IPEndPoint)secondProbe.LocalEndpoint).Port;
    firstProbe.Stop();
    secondProbe.Stop();

    var firstConfig = CreateTestConfig(firstPort);
    var secondConfig = CreateTestConfig(secondPort);
    var firstLogPath = Path.Combine(Path.GetTempPath(), $"clipboardsync-tests-{Guid.NewGuid():N}.log");
    var secondLogPath = Path.Combine(Path.GetTempPath(), $"clipboardsync-tests-{Guid.NewGuid():N}.log");
    var firstLogger = new FileLogger(firstLogPath);
    var secondLogger = new FileLogger(secondLogPath);
    using var first = new TcpTransfer(firstConfig, firstLogger, "first", "peer-a");
    using var second = new TcpTransfer(secondConfig, secondLogger, "second", "peer-b");
    var received = new TaskCompletionSource<ClipboardReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
    second.ClipboardReceived += (_, e) =>
    {
        if (e.SenderId == "peer-a")
            received.TrySetResult(e);
    };

    await first.StartAsync();
    await second.StartAsync();

    first.RegisterPeer(new PeerInfo
    {
        PeerId = "peer-b",
        Hostname = "second",
        IpAddress = IPAddress.Loopback.ToString(),
        TcpPort = secondPort
    });
    second.RegisterPeer(new PeerInfo
    {
        PeerId = "peer-a",
        Hostname = "first",
        IpAddress = IPAddress.Loopback.ToString(),
        TcpPort = firstPort
    });

    await Task.Delay(TimeSpan.FromMilliseconds(500));

    await first.SendClipboardAsync(new ClipboardPacket
    {
        Type = "clipboard",
        Hash = "test-hash",
        Format = ClipboardFormat.Text,
        Size = Encoding.UTF8.GetByteCount("hello from first"),
        TextContent = "hello from first",
        SenderId = "peer-a",
        Hostname = "first",
        TcpPort = firstPort
    });

    var payload = await AwaitWithTimeout(received.Task, TimeSpan.FromSeconds(3));
    AssertEqual("hello from first", payload.TextContent, "simultaneous peer connections should leave a readable channel for clipboard payloads");
}

AppConfig CreateTestConfig(int tcpPort) => new()
{
    Discovery = new DiscoveryConfig
    {
        UdpPort = 0,
        BroadcastIntervalSeconds = 5,
        PeerTimeoutSeconds = 30
    },
    Transfer = new TransferConfig { TcpPort = tcpPort },
    Auth = new AuthConfig { Token = "secret-token-for-two-windows-machines" }
};

async Task WriteHeartbeatFrameForTestAsync(NetworkStream stream, string token, string senderId, int tcpPort)
{
    var packet = new ClipboardPacket
    {
        Type = "heartbeat",
        Hash = "",
        Format = ClipboardFormat.Text,
        Size = 0,
        SenderId = senderId,
        Hostname = "remote",
        TcpPort = tcpPort
    };
    var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
    var encrypted = SharedSecretAuth.Encrypt(token, plaintext);
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var frame = new SecureFrame
    {
        Type = packet.Type,
        SenderId = packet.SenderId,
        Timestamp = timestamp,
        Proof = SharedSecretAuth.CreateProof(token, packet.SenderId, timestamp),
        Nonce = encrypted.Nonce,
        Ciphertext = encrypted.Ciphertext,
        Tag = encrypted.Tag
    };
    var frameBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame));
    await stream.WriteAsync(BitConverter.GetBytes(frameBytes.Length));
    await stream.WriteAsync(frameBytes);
    await stream.FlushAsync();
}

async Task<T> AwaitWithTimeout<T>(Task<T> task, TimeSpan timeout)
{
    var completed = await Task.WhenAny(task, Task.Delay(timeout));
    if (completed != task)
        throw new TimeoutException($"Timed out after {timeout.TotalMilliseconds}ms.");

    return await task;
}

async Task AwaitTaskWithTimeout(Task task, TimeSpan timeout)
{
    var completed = await Task.WhenAny(task, Task.Delay(timeout));
    if (completed != task)
        throw new TimeoutException($"Timed out after {timeout.TotalMilliseconds}ms.");

    await task;
}

Task RunStaAsync(Action action)
{
    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            action();
            tcs.SetResult();
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return tcs.Task;
}

Bitmap CreateTestBitmap(Color color)
{
    var bmp = new Bitmap(16, 16);
    using var g = Graphics.FromImage(bmp);
    g.Clear(color);
    return bmp;
}

async Task<byte[]> ReadExactlyForTestAsync(NetworkStream stream, int count, TimeSpan timeout)
{
    var buffer = new byte[count];
    using var cts = new CancellationTokenSource(timeout);
    var offset = 0;
    while (offset < count)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cts.Token);
        if (read == 0) throw new EndOfStreamException();
        offset += read;
    }
    return buffer;
}

void AssertTrue(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

void AssertFalse(bool condition, string message)
{
    if (condition) throw new InvalidOperationException(message);
}

void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}. Expected '{expected}', got '{actual}'.");
}

void AssertNotContains(string unexpected, string actual, string message)
{
    if (actual.Contains(unexpected, StringComparison.Ordinal))
        throw new InvalidOperationException(message);
}

void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}
