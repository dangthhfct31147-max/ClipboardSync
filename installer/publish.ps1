# Build and publish ClipboardSync to installer folder
# Run this BEFORE running install.ps1

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "..\src\ClipboardSync\ClipboardSync.csproj"
$installerPath = $PSScriptRoot
$publishPath = Join-Path $installerPath "staged"
$configPath = Join-Path $installerPath "appsettings.json"
$preservedConfigPath = Join-Path $env:TEMP "ClipboardSync.appsettings.$PID.json"

Write-Host "=== ClipboardSync Build & Publish ===" -ForegroundColor Cyan

function Stop-RunningClipboardSync {
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "ClipboardSync*" }
    if (-not $running) { return $true }

    Write-Host "Stopping running ClipboardSync process before publish..." -ForegroundColor Yellow
    try {
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
        return $true
    } catch {
        Write-Host "Could not stop running process. Build will remain staged:" -ForegroundColor Yellow
        Write-Host "  $publishPath" -ForegroundColor Yellow
        Write-Host "Close ClipboardSync or run this script as Administrator to replace installer files." -ForegroundColor Yellow
        return $false
    }
}

function Remove-ObsoleteInstallerArtifacts {
    $patterns = @(
        "ClipboardSync_*.exe",
        "ClipboardSync_test.exe",
        "ClipboardSync.dll",
        "ClipboardSync.deps.json",
        "ClipboardSync.runtimeconfig.json",
        "Microsoft.Extensions*.dll",
        "System.ServiceProcess.ServiceController.dll"
    )

    foreach ($pattern in $patterns) {
        Get-ChildItem -Path $installerPath -File -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }

    $runtimeDir = Join-Path $installerPath "runtimes"
    if (Test-Path $runtimeDir) {
        Remove-Item -LiteralPath $runtimeDir -Recurse -Force
    }

    foreach ($stateDirName in @("data", "logs")) {
        $stateDir = Join-Path $installerPath $stateDirName
        if (Test-Path $stateDir) {
            Remove-Item -LiteralPath $stateDir -Recurse -Force
        }
    }
}

$running = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "ClipboardSync*" }
$canReplaceInstaller = Stop-RunningClipboardSync

if (Test-Path $configPath) {
    Copy-Item -LiteralPath $configPath -Destination $preservedConfigPath -Force
}

if (Test-Path $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}
[void][IO.Directory]::CreateDirectory($publishPath)

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

if (Test-Path $preservedConfigPath) {
    Copy-Item -LiteralPath $preservedConfigPath -Destination (Join-Path $publishPath "appsettings.json") -Force
    Remove-Item -LiteralPath $preservedConfigPath -Force
}

# Verify output
$exePath = Join-Path $publishPath "ClipboardSync.exe"
$publishedConfigPath = Join-Path $publishPath "appsettings.json"

if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: ClipboardSync.exe not found after publish!" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $publishedConfigPath)) {
    Write-Host "ERROR: appsettings.json not found!" -ForegroundColor Red
    exit 1
}

$fileCount = (Get-ChildItem $publishPath -File | Measure-Object).Count
Write-Host "Published $fileCount files to staging: $publishPath" -ForegroundColor Green

if ($canReplaceInstaller) {
    Remove-ObsoleteInstallerArtifacts
    Copy-Item -Path (Join-Path $publishPath "*") -Destination $installerPath -Recurse -Force
    Remove-Item -LiteralPath $publishPath -Recurse -Force
    Write-Host "Installer folder updated and obsolete artifacts removed." -ForegroundColor Green
    Write-Host "Ready to run: .\install.ps1" -ForegroundColor Green
} else {
    Write-Host "Staged build is ready. Close ClipboardSync, then copy staged files into installer or rerun publish.ps1 elevated." -ForegroundColor Yellow
}
