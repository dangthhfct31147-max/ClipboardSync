#Requires -RunAsAdministrator

param(
    [switch]$Uninstall,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

$exePath      = Join-Path $PSScriptRoot "ClipboardSync.exe"
$serviceName  = "ClipboardSync"
$displayName  = "ClipboardSync"
$description  = "P2P Clipboard Sync for Windows"
$logDir       = Join-Path $env:LOCALAPPDATA "ClipboardSync\logs"

# ----------------------------------------------------------------------
# Uninstall
# ----------------------------------------------------------------------
if ($Uninstall) {
    Write-Host "Uninstalling ClipboardSync service..." -ForegroundColor Yellow

    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -eq 'Running') {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            Write-Host "  Service stopped." -ForegroundColor Cyan
        }
        & sc.exe delete $serviceName 2>$null | Out-Null
        Write-Host "  Service deleted." -ForegroundColor Cyan
    } else {
        Write-Host "  Service not found. Nothing to uninstall." -ForegroundColor Gray
    }

    Write-Host ""
    Write-Host "ClipboardSync has been uninstalled." -ForegroundColor Green
    Write-Host "The application files in this folder are untouched." -ForegroundColor Gray
    exit 0
}

# ----------------------------------------------------------------------
# Runtime check
# ----------------------------------------------------------------------
Write-Host "Checking .NET 10 Desktop Runtime..." -ForegroundColor Cyan
$runtimes = dotnet --list-runtimes 2>&1 | Out-String
if ($runtimes -match "Microsoft\.WindowsDesktop\.App\s+10\.0\.\d+") {
    Write-Host "  OK - .NET 10 Desktop Runtime detected." -ForegroundColor Green
} else {
    Write-Host "ERROR: .NET 10 Desktop Runtime is not installed." -ForegroundColor Red
    Write-Host "Download from: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
    Write-Host "Install the x64 desktop runtime, then run this script again." -ForegroundColor Yellow
    exit 1
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
# Stop & remove existing service (if any)
# ----------------------------------------------------------------------
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Existing service found. Stopping and removing..." -ForegroundColor Yellow
    if ($svc.Status -eq 'Running') {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    }
    & sc.exe delete $serviceName 2>$null | Out-Null
    Start-Sleep -Seconds 1
}

# ----------------------------------------------------------------------
# Create the service
# ----------------------------------------------------------------------
Write-Host "Creating Windows Service..." -ForegroundColor Cyan

& sc.exe create $serviceName binPath= "`"$exePath`"" start= auto DisplayName= "`"$displayName`"" error= ignore
& sc.exe description $serviceName $description

# Windows 8+ supports delayed-auto start
$os = [System.Environment]::OSVersion.Version
if ($os.Major -ge 10) {
    & sc.exe config $serviceName start= delayed-auto | Out-Null
    Write-Host "  Startup type: Automatic (Delayed Start)" -ForegroundColor Gray
} else {
    Write-Host "  Startup type: Automatic" -ForegroundColor Gray
}

# ----------------------------------------------------------------------
# Configure recovery (auto-restart on failure)
# ----------------------------------------------------------------------
Write-Host "Configuring auto-recovery on failure..." -ForegroundColor Cyan
# Reset=86400 (1 day), actions: restart after 60s for first/second/future failures
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Write-Host "  Recovery actions configured (restart after 60s on any crash)." -ForegroundColor Gray

# ----------------------------------------------------------------------
# Start the service
# ----------------------------------------------------------------------
if ($NoStart) {
    Write-Host ""
    Write-Host "Service registered (not started). To start manually:" -ForegroundColor Yellow
    Write-Host "  sc.exe start $serviceName" -ForegroundColor White
    Write-Host "  Or restart your computer — it will start automatically." -ForegroundColor Yellow
    exit 0
}

Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service -Name $serviceName -ErrorAction SilentlyContinue

Start-Sleep -Seconds 3
$svc = Get-Service -Name $serviceName

if ($svc.Status -eq 'Running') {
    Write-Host ""
    Write-Host "SUCCESS — ClipboardSync is installed and running!" -ForegroundColor Green
    Write-Host "  Look for the clipboard icon in your system tray." -ForegroundColor Green
    Write-Host "  Logs are at: $logDir" -ForegroundColor Gray
} else {
    Write-Host ""
    Write-Host "WARNING: Service did not enter Running state." -ForegroundColor Yellow
    Write-Host "  Current status: $($svc.Status)" -ForegroundColor Yellow
    Write-Host "  Check logs at: $logDir" -ForegroundColor Yellow
    Write-Host "  Run: Get-Content `"$logDir\*.log`" -Tail 30" -ForegroundColor White
    exit 1
}
