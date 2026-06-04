# ClipboardSync

P2P clipboard sync cho Windows - tu dong sync clipboard giua cac may Windows trong cung mang LAN, khong can server trung tam.

## Requirements

- Windows 10/11
- No runtime required (self-contained build)

## Build & Install

### 1. Build

```powershell
cd installer
.\publish.ps1
```

This restores packages, publishes to `installer/`, and verifies all required files are present.

### 2. Install

```powershell
cd installer
.\install.ps1
```

This creates a startup shortcut so ClipboardSync runs automatically at logon.
(Requires Administrator for Task Scheduler registration)

### Uninstall

```powershell
cd installer
.\install.ps1 -Uninstall
```

## Features

- **P2P Discovery** - Cac may tu discover nhau qua UDP broadcast
- **TCP Transfer** - Sync text, image, files qua persistent TCP connections
- **User-Space App** - Chay trong user session, co quyen truy cap clipboard day du
- **System Tray** - Icon + menu de control (toggle sync, xem peers, exit)
- **Echo Prevention** - SHA256 hash-based de tranh sync loop
- **Cross-format** - Sync text, PNG images, file drop lists

## How It Works

```
ClipboardSync App (user session)
  UDP Broadcast (port 51234) --> Peer Discovery
  TCP Listener (port 51235) <-- Peer Manager
              |                    |
              v                    v
  Clipboard Sync Engine
  WM_CLIPBOARDUPDATE -> TCP -> Peers
  Peers -> TCP -> Update clipboard (hash check)
  System Tray Icon (NotifyIcon)
```

1. Moi may broadcast UDP packet moi 5 giay
2. Khi phat hien may khac, thiet lap TCP connection
3. Clipboard thay doi -> compute SHA256 hash -> gui qua TCP
4. May nhan -> compare hash -> update clipboard neu khac

## Config

Chinh sua `installer/appsettings.json` sau khi publish:

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
  }
}
```

## System Tray Menu

- **Status** - Hien thi so peers dang ket noi
- **Peers** - Xem danh sach IP cua peers, click de copy
- **Sync Enabled** - Toggle bat/tat sync
- **Sync Text / Images / Files** - Toggle tung loai content
- **Exit** - Dung app

## Logs

```
%LOCALAPPDATA%\ClipboardSync\logs\clipboardsync_YYYYMMDD.log
```

Logs are automatically purged after 7 days.

## Troubleshooting

**App doesn't appear in tray**
-> Kiem tra logs tai %LOCALAPPDATA%\ClipboardSync\logs\
-> Dam bao app da duoc install va khoi dong

**Peers not connecting**
-> Dam bao cac may cung mang LAN
-> Kiem tra firewall cho phep UDP 51234 va TCP 51235
-> Kiem tra Auth Token giong nhau trong appsettings.json

**Clipboard not syncing**
-> Kiem tra toggle trong tray menu (Sync Enabled, Sync Text/Images)
-> Restart app bang cach Exit va khoi dong lai
