param(
    [string]$InstallDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

function Get-ShortHash([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value.Trim())
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
        return -join ($hash[0..5] | ForEach-Object { $_.ToString("x2") })
    } finally {
        $sha.Dispose()
    }
}

function Get-FileShortHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return "" }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.Substring(0, 16).ToLowerInvariant()
}

$configPath = Join-Path $InstallDir "appsettings.json"
$exePath = Join-Path $InstallDir "ClipboardSync.exe"
$logDir = Join-Path $env:LOCALAPPDATA "ClipboardSync\logs"
$latestLog = Get-ChildItem -LiteralPath $logDir -Filter "clipboardsync_*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

$token = ""
$udpPort = 51234
$tcpPort = 51235
if (Test-Path -LiteralPath $configPath) {
    try {
        $json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $token = [string]$json.Auth.Token
        if ($json.Discovery.UdpPort) { $udpPort = [int]$json.Discovery.UdpPort }
        if ($json.Transfer.TcpPort) { $tcpPort = [int]$json.Transfer.TcpPort }
    } catch {
        Write-Host "Could not parse config: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

$processes = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -like "ClipboardSync*" }

$tcpListeners = Get-NetTCPConnection -LocalPort $tcpPort -ErrorAction SilentlyContinue |
    Where-Object { $_.State -eq "Listen" } |
    Select-Object LocalAddress, LocalPort, OwningProcess

$udpListeners = Get-NetUDPEndpoint -LocalPort $udpPort -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess

Write-Host "=== ClipboardSync diagnostics ===" -ForegroundColor Cyan
Write-Host "Machine: $env:COMPUTERNAME"
Write-Host "InstallDir: $InstallDir"
Write-Host "ExeExists: $(Test-Path -LiteralPath $exePath)"
Write-Host "ExeSha256Prefix: $(Get-FileShortHash $exePath)"
Write-Host "ConfigExists: $(Test-Path -LiteralPath $configPath)"
Write-Host "TokenSet: $(-not [string]::IsNullOrWhiteSpace($token))"
Write-Host "TokenLength: $($token.Trim().Length)"
Write-Host "TokenSha256Prefix: $(Get-ShortHash $token)"
Write-Host "UdpPort: $udpPort"
Write-Host "TcpPort: $tcpPort"
Write-Host ""

Write-Host "Running processes:" -ForegroundColor Cyan
if ($processes) {
    $processes |
        Select-Object Id, ProcessName, Path,
            @{Name = "PrivateMB"; Expression = { [math]::Round($_.PrivateMemorySize64 / 1MB, 1) } },
            @{Name = "WorkingSetMB"; Expression = { [math]::Round($_.WorkingSet64 / 1MB, 1) } },
            StartTime |
        Format-Table -AutoSize
} else {
    Write-Host "  none"
}

Write-Host "TCP listeners:" -ForegroundColor Cyan
if ($tcpListeners) { $tcpListeners | Format-Table -AutoSize } else { Write-Host "  none" }

Write-Host "UDP listeners:" -ForegroundColor Cyan
if ($udpListeners) { $udpListeners | Format-Table -AutoSize } else { Write-Host "  none" }

if ($latestLog) {
    Write-Host "Recent sync/auth log lines:" -ForegroundColor Cyan
    Select-String -LiteralPath $latestLog.FullName -Pattern "New peer discovered|Connected to peer|Clipboard received|Clipboard sent|auth proof mismatch|Failed to connect|Peer timed out|ReadExactlyAsync timed out|ERROR|WARN" |
        Select-Object -Last 40
} else {
    Write-Host "No logs found under $logDir" -ForegroundColor Yellow
}
