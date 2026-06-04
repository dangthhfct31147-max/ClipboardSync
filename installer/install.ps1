#Requires -RunAsAdministrator

param(
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $PSScriptRoot "ClipboardSync.exe"
$serviceName = "ClipboardSync"
$displayName = "ClipboardSync"
$description = "P2P Clipboard Sync for Windows"
$logDir = Join-Path $env:LOCALAPPDATA "ClipboardSync\logs"

if ($Uninstall) {
    Write-Host "Uninstalling ClipboardSync service..." -ForegroundColor Yellow

    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -eq 'Running') {
            Stop-Service -Name $serviceName -Force
            Write-Host "Service stopped."
        }
        & sc.exe delete $serviceName 2>$null
        Write-Host "Service deleted."
    }

    Write-Host "ClipboardSync has been uninstalled." -ForegroundColor Green
    exit 0
}

# Check .NET 10 runtime
$runtimes = dotnet --list-runtimes 2>&1 | Out-String
if ($runtimes -match "Microsoft\.WindowsDesktop\.App\s+10\.0\.\d+") {
    Write-Host ".NET 10 Desktop Runtime detected." -ForegroundColor Green
} else {
    Write-Host "ERROR: .NET 10 Desktop Runtime is not installed." -ForegroundColor Red
    Write-Host "Download from: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
    Write-Host "Install the x64 desktop runtime, then run this script again." -ForegroundColor Yellow
    exit 1
}

Write-Host "Installing ClipboardSync..." -ForegroundColor Cyan

if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: ClipboardSync.exe not found at: $exePath" -ForegroundColor Red
    Write-Host "Run publish.ps1 first to build the project." -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path (Join-Path $PSScriptRoot "appsettings.json"))) {
    Write-Host "ERROR: appsettings.json not found in installer folder." -ForegroundColor Red
    Write-Host "Run publish.ps1 first to copy all required files." -ForegroundColor Yellow
    exit 1
}

$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Service already exists. Stopping and removing..." -ForegroundColor Yellow
    if ($svc.Status -eq 'Running') {
        Stop-Service -Name $serviceName -Force
    }
    & sc.exe delete $serviceName 2>$null
    Start-Sleep -Seconds 1
}

& sc.exe create $serviceName binPath= "`"$exePath`"" start= auto DisplayName= "`"$displayName`"" error= ignore
& sc.exe description $serviceName $description
& sc.exe config $serviceName obj= "NT AUTHORITY\NetworkService"
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000

Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service -Name $serviceName -ErrorAction Stop

Start-Sleep -Seconds 2
$svc = Get-Service -Name $serviceName
if ($svc.Status -eq 'Running') {
    Write-Host "ClipboardSync service installed and running!" -ForegroundColor Green
    Write-Host "Look for the ClipboardSync icon in your system tray." -ForegroundColor Green
    Write-Host "Logs are at: $logDir" -ForegroundColor Gray
} else {
    Write-Host "Service failed to start. Check logs at: $logDir" -ForegroundColor Red
    Write-Host "Status: $($svc.Status)" -ForegroundColor Red
    exit 1
}
