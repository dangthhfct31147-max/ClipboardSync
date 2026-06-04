# Build and publish ClipboardSync to installer folder
# Run this BEFORE running install.ps1

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "..\src\ClipboardSync\ClipboardSync.csproj"
$installerPath = $PSScriptRoot

Write-Host "=== ClipboardSync Build & Publish ===" -ForegroundColor Cyan

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

Write-Host "Publishing..." -ForegroundColor Cyan
dotnet publish $projectPath -c Release -o $installerPath --no-self-contained 2>&1

# Verify output
$exePath = Join-Path $installerPath "ClipboardSync.exe"
$configPath = Join-Path $installerPath "appsettings.json"
$depsPath = Join-Path $installerPath "ClipboardSync.deps.json"

if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: ClipboardSync.exe not found after publish!" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $configPath)) {
    Write-Host "ERROR: appsettings.json not found!" -ForegroundColor Red
    exit 1
}

$fileCount = (Get-ChildItem $installerPath -File | Measure-Object).Count
Write-Host "Published $fileCount files to installer folder." -ForegroundColor Green
Write-Host "Ready to run: .\install.ps1" -ForegroundColor Green
