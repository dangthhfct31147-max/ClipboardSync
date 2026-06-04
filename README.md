# ClipboardSync

P2P clipboard sync cho Windows - tu dong sync clipboard giua cac may Windows trong cung mang LAN, khong can server trung tam.

## Requirements

- Windows 10/11
- **.NET 10 Desktop Runtime** (download: https://dotnet.microsoft.com/download/dotnet/10.0)
- .NET 10 SDK (only needed for building)

## Build & Install

### 1. Build

```powershell
cd installer
.\publish.ps1
```

This restores packages, publishes to `installer/`, and verifies all required files are present.

### 2. Install as Windows Service

```powershell
cd installer
.\install.ps1
```

(Requires Administrator)

### Uninstall

```powershell
cd installer
.\install.ps1 -Uninstall
```

## Run in Console Mode (for testing)

```powershell
.\src\ClipboardSync\bin\Release\net10.0-windows\ClipboardSync.exe
```

## Features

- **P2P Discovery** - Cac may tu discover nhau qua UDP broadcast
- **TCP Transfer** - Sync text, image, files qua persistent TCP connections
- **Windows Service** - Chay nen, auto-start on boot
- **System Tray** - Icon + menu de control (toggle sync, xem peers, exit)
- **Echo Prevention** - SHA256 hash-based de tranh sync loop
- **Cross-format** - Sync text, PNG images, file drop lists

## How It Works

```
ClipboardSync Service
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
- **Exit** - Dung service

## Logs

```
%LOCALAPPDATA%\ClipboardSync\logs\clipboardsync_YYYYMMDD.log
```

## Troubleshooting

**"Unable to start program" / "system cannot execute"**
-> Chay `.\publish.ps1` truoc, sau do `.\install.ps1`

**Service fails to start**
-> Kiem tra %LOCALAPPDATA%\ClipboardSync\logs\ cho loi chi tiet
-> Dam bao .NET 10 Desktop Runtime da duoc cai
