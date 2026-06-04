#Requires -RunAsAdministrator

param(
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

$exePath      = Join-Path $PSScriptRoot "ClipboardSync.exe"
$serviceName  = "ClipboardSync"
$appName      = "ClipboardSync"
$displayName  = "ClipboardSync"
$description  = "P2P Clipboard Sync for Windows"
$logDir       = Join-Path $env:LOCALAPPDATA "ClipboardSync\logs"

# ----------------------------------------------------------------------
# Uninstall — remove shortcut, Task Scheduler entry, and old service
# ----------------------------------------------------------------------
if ($Uninstall) {
    Write-Host "Uninstalling ClipboardSync..." -ForegroundColor Yellow

    # Remove startup shortcut from user's startup folder
    $startupDir = [Environment]::GetFolderPath("Startup")
    $shortcutPath = Join-Path $startupDir "ClipboardSync.lnk"
    if (Test-Path $shortcutPath) {
        Remove-Item $shortcutPath -Force
        Write-Host "  Startup shortcut removed." -ForegroundColor Cyan
    }

    # Remove Task Scheduler entry (if installed)
    $taskName = "ClipboardSync"
    $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($existingTask) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "  Scheduled task removed." -ForegroundColor Cyan
    }

    # Clean up old Windows Service (if any — for users migrating from old version)
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -eq 'Running') {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        }
        & sc.exe delete $serviceName 2>$null | Out-Null
        Write-Host "  Old Windows Service removed." -ForegroundColor Cyan
    }

    Write-Host ""
    Write-Host "ClipboardSync has been uninstalled." -ForegroundColor Green
    Write-Host "Application files in this folder are untouched." -ForegroundColor Gray
    exit 0
}

# ----------------------------------------------------------------------
# Pre-flight checks
# ----------------------------------------------------------------------
Write-Host "Running pre-flight checks..." -ForegroundColor Cyan

if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: ClipboardSync.exe not found at:" -ForegroundColor Red
    Write-Host "  $exePath" -ForegroundColor Red
    Write-Host ""
    Write-Host "Run publish.ps1 first to build the project." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path (Join-Path $PSScriptRoot "appsettings.json"))) {
    Write-Host "ERROR: appsettings.json not found in installer folder." -ForegroundColor Red
    Write-Host "Run publish.ps1 first to copy all required files." -ForegroundColor Yellow
    exit 1
}

# ----------------------------------------------------------------------
# Remove old Windows Service if it exists (from old version)
# ----------------------------------------------------------------------
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Removing old Windows Service..." -ForegroundColor Yellow
    if ($svc.Status -eq 'Running') {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    }
    & sc.exe delete $serviceName 2>$null | Out-Null
}

# ----------------------------------------------------------------------
# Create startup shortcut (Start Menu / user startup)
# ----------------------------------------------------------------------
Write-Host "Creating startup shortcut..." -ForegroundColor Cyan

$WshShell = New-Object -ComObject WScript.Shell
$startupPath = Join-Path ([Environment]::GetFolderPath("Startup")) "ClipboardSync.lnk"
$shortcut = $WshShell.CreateShortcut($startupPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = "P2P Clipboard Sync for Windows"
$shortcut.WindowStyle = 1  # Normal window
$shortcut.Save()

Write-Host "  Startup shortcut created: $startupPath" -ForegroundColor Gray

# ----------------------------------------------------------------------
# Register with Task Scheduler (hidden startup for better UX)
# ----------------------------------------------------------------------
Write-Host "Registering startup task..." -ForegroundColor Cyan

$taskAction = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $PSScriptRoot
$taskTrigger = New-ScheduledTaskTrigger -AtLogOn
$taskPrincipal = New-ScheduledTaskPrincipal -GroupId "S-1-5-21-$(whoami /user | Select-Object -Skip 3 | ForEach-Object { $_.Trim() })" -RunLevel Limited

# Fallback: use current user if above fails
if (-not $taskPrincipal.GroupId) {
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -RunLevel Limited
}

$taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

# Remove existing task if any
$existingTask = Get-ScheduledTask -TaskName $serviceName -ErrorAction SilentlyContinue
if ($existingTask) {
    Unregister-ScheduledTask -TaskName $serviceName -Confirm:$false
}

Register-ScheduledTask -TaskName $serviceName -Action $taskAction -Trigger $taskTrigger `
    -Settings $taskSettings -Principal $taskPrincipal -Description $description | Out-Null

Write-Host "  Startup task registered (runs at logon)." -ForegroundColor Gray

# ----------------------------------------------------------------------
# Ensure log directory exists
# ----------------------------------------------------------------------
try {
    [void][System.IO.Directory]::CreateDirectory($logDir)
} catch {
    Write-Host "  Note: Could not create log directory. Logs will go to: $logDir" -ForegroundColor Gray
}

# ----------------------------------------------------------------------
# Launch the app now
# ----------------------------------------------------------------------
Write-Host "Launching ClipboardSync..." -ForegroundColor Cyan
Start-Process $exePath -WorkingDirectory $PSScriptRoot

Start-Sleep -Seconds 2

Write-Host ""
Write-Host "SUCCESS — ClipboardSync is installed and running!" -ForegroundColor Green
Write-Host "  Look for the clipboard icon in your system tray." -ForegroundColor Green
Write-Host "  Logs are at: $logDir" -ForegroundColor Gray
Write-Host "  To uninstall: .\install.ps1 -Uninstall" -ForegroundColor Gray
