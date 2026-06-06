using System.Security.Cryptography;
using System.Text;

namespace ClipboardSync.Core;

public static class SharedSecretAuth
{
    private const string DiscoveryPurpose = "ClipboardSync discovery group v1";
    private const string ProofPurpose = "ClipboardSync peer proof v1";

    public static string CreateGroupId(string token)
    {
        var key = DeriveKey(token);
        return Base64UrlEncode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(DiscoveryPurpose)));
    }

    public static string CreateProof(string token, string peerId, long timestamp)
    {
        var key = DeriveKey(token);
        var message = $"{ProofPurpose}|{peerId}|{timestamp}";
        return Base64UrlEncode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));
    }

    public static bool VerifyProof(string token, string peerId, long timestamp, string? proof, TimeSpan maxClockSkew)
    {
        if (string.IsNullOrWhiteSpace(proof)) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var skew = Math.Abs(now - timestamp);
        if (skew > maxClockSkew.TotalMilliseconds) return false;

        var expected = CreateProof(token, peerId, timestamp);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(proof);
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    public static EncryptedPayload Encrypt(string token, byte[] plaintext)
    {
        var key = DeriveKey(token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return new EncryptedPayload(nonce, ciphertext, tag);
    }

    public static byte[] Decrypt(string token, EncryptedPayload payload)
    {
        var key = DeriveKey(token);
        var plaintext = new byte[payload.Ciphertext.Length];

        using var aes = new AesGcm(key, payload.Tag.Length);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext);

        return plaintext;
    }

    private static byte[] DeriveKey(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Auth token is required for secure clipboard sync.");
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record EncryptedPayload(byte[] Nonce, byte[] Ciphertext, byte[] Tag);
