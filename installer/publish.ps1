# Build and publish ClipboardSync to installer folder
# Run this BEFORE running install.ps1

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "..\src\ClipboardSync\ClipboardSync.csproj"
$installerPath = $PSScriptRoot
$publishPath = $installerPath

Write-Host "=== ClipboardSync Build & Publish ===" -ForegroundColor Cyan

$running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "ClipboardSync*" }
if ($running) {
    Write-Host "Stopping running ClipboardSync process before publish..." -ForegroundColor Yellow
    try {
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    } catch {
        $publishPath = Join-Path $installerPath "staged"
        [void][IO.Directory]::CreateDirectory($publishPath)
        Write-Host "Could not stop running process. Publishing to staging folder instead:" -ForegroundColor Yellow
        Write-Host "  $publishPath" -ForegroundColor Yellow
        Write-Host "Close ClipboardSync or run this script as Administrator to publish directly to installer." -ForegroundColor Yellow
    }
}

# Check .NET 10 SDK
$sdks = dotnet --list-sdks 2>&1 | Out-String
if ($sdks -notmatch "10\.0\.300") {
    Write-Host "WARNING: .NET 10.0.300 SDK not found!" -ForegroundColor Yellow
    Write-Host "Installed SDKs:"
    Write-Host $sdks -ForegroundColor Gray
    Write-Host "Download from: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
}

# Restore & publish
Write-Host "Restoring packages..." -ForegroundColor Cyan
dotnet restore $projectPath 2>&1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publishing self-contained..." -ForegroundColor Cyan
dotnet publish $projectPath -c Release -o $publishPath -r win-x64 --self-contained 2>&1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Verify output
$exePath = Join-Path $publishPath "ClipboardSync.exe"
$configPath = Join-Path $publishPath "appsettings.json"

if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: ClipboardSync.exe not found after publish!" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $configPath)) {
    Write-Host "ERROR: appsettings.json not found!" -ForegroundColor Red
    exit 1
}

$fileCount = (Get-ChildItem $publishPath -File | Measure-Object).Count
Write-Host "Published $fileCount files to: $publishPath" -ForegroundColor Green
if ($publishPath -eq $installerPath) {
    Write-Host "Ready to run: .\install.ps1" -ForegroundColor Green
} else {
    Write-Host "Staged build is ready. Close ClipboardSync, then copy staged files into installer or rerun publish.ps1 elevated." -ForegroundColor Yellow
}
