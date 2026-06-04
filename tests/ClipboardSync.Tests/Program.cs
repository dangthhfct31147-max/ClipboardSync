using System.Text;
using System.Security.Cryptography;
using System.Net;
using System.Net.NetworkInformation;
using ClipboardSync.Core;

var tests = new (string Name, Action Body)[]
{
    ("Discovery group id is stable and does not reveal the token", DiscoveryGroupIdIsStable),
    ("Proof verifies only for the matching token and peer", ProofRequiresMatchingTokenAndPeer),
    ("Encryption round-trips and rejects tampering", EncryptionRoundTripsAndRejectsTampering),
    ("Discovery ignores VPN and virtual adapters", DiscoveryIgnoresVpnAndVirtualAdapters),
    ("Discovery identifies local self addresses", DiscoveryIdentifiesLocalSelfAddresses)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
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
