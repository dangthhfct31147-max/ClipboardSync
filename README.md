# ClipboardSync

Secure P2P clipboard sync for Windows. It syncs clipboard content between Windows machines on the same LAN without requiring the same Microsoft account or a central server.

## What It Does

- Runs as a current-user tray app, so it can access the Windows clipboard correctly.
- Discovers peers on the LAN with UDP broadcast.
- Transfers clipboard updates over TCP.
- Uses a shared secret token so only paired machines join the same group.
- Does not broadcast the raw token.
- Encrypts clipboard payloads with AES-GCM before sending them over the LAN.
- Syncs text and images by default. File drop-list sync is available but disabled by default.

## Build

```powershell
cd D:\work\Sync\installer
.\publish.ps1
```

The build is self-contained, so target machines do not need a separate .NET runtime.

## Install And Pair Two Machines

On the first machine:

```powershell
cd D:\work\Sync\installer
.\install.ps1
```

If no token exists yet, the installer generates one and prints a pairing command.

On the second machine, copy the same `installer` folder and run the printed command:

```powershell
.\install.ps1 -Token "<token-from-first-machine>"
```

Keep the token private. Anyone with the token and LAN access can join the clipboard sync group.

## Auto-Start

`install.ps1` registers a current-user logon task. It does not require Administrator for normal installation.

If Windows Firewall blocks peer discovery or transfer, run the installer once from an elevated PowerShell. In elevated mode it adds an inbound Private-network firewall rule for `ClipboardSync.exe`.

## Uninstall

```powershell
cd D:\work\Sync\installer
.\install.ps1 -Uninstall
```

This removes the startup task/shortcut and stops the running process. Application files are left in place.

## Config

Edit `installer/appsettings.json` if you need custom ports or content options:

```json
{
  "Discovery": {
    "UdpPort": 51234,
    "BroadcastIntervalSeconds": 5,
    "PeerTimeoutSeconds": 30
  },
  "Transfer": {
    "TcpPort": 51235
  },
  "Sync": {
    "Enabled": true,
    "SyncText": true,
    "SyncImages": true,
    "SyncFiles": false
  },
  "Auth": {
    "Token": "<shared-secret-token>"
  }
}
```

The token must match on both machines.

## Tray Menu

- `Peers` shows connected machines.
- `Sync Enabled` turns all sync on/off.
- `Sync Text`, `Sync Images`, and `Sync Files` control each content type.
- `Exit` stops the app.

## Logs

```text
%LOCALAPPDATA%\ClipboardSync\logs\clipboardsync_YYYYMMDD.log
```

## Troubleshooting

If peers do not connect:

- Make sure both machines are on the same LAN.
- Make sure both machines use the same `Auth:Token`.
- If needed, run `install.ps1` as Administrator once to add the firewall rule.
- Check logs under `%LOCALAPPDATA%\ClipboardSync\logs\`.
