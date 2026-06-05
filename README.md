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

## Requirements

- Windows 10/11 on both machines.
- Both machines must be on the same LAN.
- Machine 1 builds the app from source and needs .NET SDK 10.0.300 or newer.
- Machine 2 only needs the copied `installer` folder; it does not need the .NET SDK.

## Quick Setup

Use PowerShell from the repository folder on machine 1.

### Machine 1: build, install, and create the pairing token

```powershell
cd D:\work\Sync\installer
.\setup.ps1 -Machine 1 -Build
```

This command:

- Builds a self-contained `ClipboardSync.exe`.
- Installs ClipboardSync for the current Windows user.
- Starts the tray app.
- Prints the exact command to run on machine 2.

Example output:

```powershell
.\setup.ps1 -Machine 2 -Token "<token-from-machine-1>"
```

Keep the token private. Anyone with the token and LAN access can join this clipboard sync group.

### Machine 2: copy the installer folder and install with the same token

Copy the whole `installer` folder from machine 1 to machine 2. Then run the command printed by machine 1 from inside that copied folder:

```powershell
cd C:\Path\To\ClipboardSync\installer
.\setup.ps1 -Machine 2 -Token "<token-from-machine-1>"
```

Machine 2 must use the same token as machine 1. If the tokens differ, both tray icons may show confusing state and clipboard sync will not work correctly.

## Manual Build Only

If you only want to rebuild the `installer` folder without installing:

```powershell
cd D:\work\Sync\installer
.\publish.ps1
```

The published app is self-contained, so target machines do not need a separate .NET runtime.

## Advanced Install Commands

`setup.ps1` is the recommended entrypoint. It wraps `install.ps1` with clearer machine roles:

- Machine 1 from source: `.\setup.ps1 -Machine 1 -Build`
- Machine 1 when `ClipboardSync.exe` already exists: `.\setup.ps1 -Machine 1`
- Machine 2: `.\setup.ps1 -Machine 2 -Token "<token-from-machine-1>"`
- Install without starting immediately: add `-NoStart`

You can still call `install.ps1` directly when scripting custom flows:

```powershell
.\install.ps1
.\install.ps1 -Token "<token-from-machine-1>"
```

## Auto-Start

The installer registers a current-user logon task. If Task Scheduler denies access, it falls back to a Startup folder shortcut. Normal installation does not require Administrator.

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

Do not commit a real token to git. `installer/appsettings.json` should be empty in source control and configured locally by setup.

## Tray Icon

- Blue icon: no peer is connected.
- Green icon: at least one peer is connected.
- The icon can be green even while a connection is reconnecting; clipboard transfer still depends on the TCP connection recovering.

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
- Install the latest copied `installer` folder on both machines. Running a new build on only one machine can leave the other machine on old sync logic.
- If needed, run `install.ps1` as Administrator once to add the firewall rule.
- Check logs under `%LOCALAPPDATA%\ClipboardSync\logs\`.

If tray icons are green but clipboard content does not appear on the other machine:

- Reinstall the latest build on both machines with `setup.ps1`.
- Check the logs on the receiving machine first; apply errors show there.
- For screenshots, the app waits briefly for the image clipboard to stabilize so only the final screenshot image is synced.
