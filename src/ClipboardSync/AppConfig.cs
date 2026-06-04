namespace ClipboardSync;

public sealed class AppConfig
{
    public DiscoveryConfig Discovery { get; init; } = new();
    public TransferConfig Transfer { get; init; } = new();
    public SyncConfig Sync { get; init; } = new();
    public AuthConfig Auth { get; init; } = new();
}

public sealed class DiscoveryConfig
{
    public int UdpPort { get; init; } = 51234;
    public int BroadcastIntervalSeconds { get; init; } = 5;
    public int PeerTimeoutSeconds { get; init; } = 30;
}

public sealed class TransferConfig
{
    public int TcpPort { get; init; } = 51235;
}

public sealed class SyncConfig
{
    public bool Enabled { get; set; } = true;
    public bool SyncText { get; set; } = true;
    public bool SyncImages { get; set; } = true;
    public bool SyncFiles { get; set; }
}

public sealed class AuthConfig
{
    public string? Token { get; init; }
}
