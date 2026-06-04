# ClipboardSync

P2P clipboard sync cho Windows - tự động sync clipboard giữa các máy Windows trong cùng mạng LAN, không cần server trung tâm.

## Features

- **P2P Discovery** - Các máy tự discover nhau qua UDP broadcast
- **TCP Transfer** - Sync text, image, files qua persistent TCP connections
- **Windows Service** - Chạy nền, auto-start on boot
- **System Tray** - Icon + menu để control (toggle sync, xem peers, exit)
- **Echo Prevention** - SHA256 hash-based để tránh sync loop
- **Cross-format** - Sync text, PNG images, file drop lists

## Build

```bash
dotnet build
```

Output: `src/ClipboardSync/bin/Debug/net8.0-windows/ClipboardSync.exe`

## Run

### Console Mode (for testing)
```bash
.\src\ClipboardSync\bin\Debug\net8.0-windows\ClipboardSync.exe
```

### Windows Service (production)

Cai dat:
```powershell
cd installer
.\install.ps1
```

Go bo:
```powershell
cd installer
.\install.ps1 -Uninstall
```

## How It Works

```
┌─────────────────────────────────────────────────────┐
│               ClipboardSync Service                   │
│                                                      │
│  UDP Broadcast (port 51234) ──▶ Peer Discovery       │
│  TCP Listener (port 51235) ◀── Peer Manager          │
│            │                    │                    │
│            ▼                    ▼                    │
│  ┌─────────────────────────────────────────────┐   │
│  │         Clipboard Sync Engine                 │   │
│  │  WM_CLIPBOARDUPDATE → TCP → Peers           │   │
│  │  Peers → TCP → Update clipboard (hash check) │   │
│  └─────────────────────────────────────────────┘   │
│                                                      │
│  System Tray Icon (NotifyIcon)                       │
└─────────────────────────────────────────────────────┘
```

1. Mỗi máy broadcast UDP packet mỗi 5 giây
2. Khi phát hiện máy khác, thiết lập TCP connection
3. Clipboard thay đổi → compute SHA256 hash → gửi qua TCP
4. Máy nhận → compare hash → update clipboard nếu khác

## Config

Chỉnh sửa `appsettings.json`:

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

- **Status** - Hiển thị số peers đang kết nối
- **Peers** - Xem danh sách IP của peers, click để copy
- **Sync Enabled** - Toggle bật/tắt sync
- **Sync Text / Images / Files** - Toggle từng loại content
- **Exit** - Dừng service

## Logs

```
%LOCALAPPDATA%\ClipboardSync\logs\clipboardsync_YYYYMMDD.log
```

## Requirements

- Windows 10/11 (cần AddClipboardFormatListener - Win8+)
- .NET 8 Runtime
- Windows Service chạy với quyền LocalService
