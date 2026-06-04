using System.Text.Json.Serialization;

namespace ClipboardSync.Core;

public enum ClipboardFormat
{
    Text,
    Image,
    Files
}

public sealed class ClipboardPacket
{
    public required string Type { get; init; }
    public required string Hash { get; init; }
    public required ClipboardFormat Format { get; init; }
    public required long Size { get; init; }
    public string? TextContent { get; init; }
    public byte[]? ImageData { get; init; }
    public List<string>? FilePaths { get; init; }
    public required string SenderId { get; init; }
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class DiscoveryPacket
{
    public required string Type { get; init; }
    public required string PeerId { get; init; }
    public required string Hostname { get; init; }
    public required string IpAddress { get; init; }
    public required int TcpPort { get; init; }
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

    public sealed record PeerInfo
    {
        public required string PeerId { get; init; }
        public required string Hostname { get; init; }
        public required string IpAddress { get; init; }
        public required int TcpPort { get; init; }
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }
