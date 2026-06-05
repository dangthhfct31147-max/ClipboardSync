param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("1", "2", "Machine1", "Machine2")]
    [string]$Machine,

    [string]$Token,
    [switch]$Build,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

$publishScript = Join-Path $PSScriptRoot "publish.ps1"
$installScript = Join-Path $PSScriptRoot "install.ps1"
$configPath = Join-Path $PSScriptRoot "appsettings.json"

function Get-ClipboardSyncToken {
    if (-not (Test-Path $configPath)) {
        throw "appsettings.json not found at $configPath"
    }

    $json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $value = [string]$json.Auth.Token
    if ([string]::IsNullOrWhiteSpace($value) -or $value.Trim().ToLowerInvariant() -eq "changeme") {
        throw "Auth token was not created. Re-run setup for machine 1."
    }

    return $value.Trim()
}

function Invoke-Install([string[]]$Arguments) {
    & $installScript @Arguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path $installScript)) {
    throw "install.ps1 not found next to setup.ps1."
}

$isMachine1 = $Machine -eq "1" -or $Machine -eq "Machine1"
$installArgs = @()
if ($NoStart) {
    $installArgs += "-NoStart"
}

if ($isMachine1) {
    Write-Host "=== ClipboardSync setup: machine 1 ===" -ForegroundColor Cyan

    if ($Build) {
        if (-not (Test-Path $publishScript)) {
            throw "publish.ps1 not found next to setup.ps1."
        }

        & $publishScript
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }

    Invoke-Install $installArgs
    $pairingToken = Get-ClipboardSyncToken

    Write-Host ""
    Write-Host "Machine 1 is ready." -ForegroundColor Green
    Write-Host "Copy this installer folder to machine 2, then run:" -ForegroundColor Yellow
    Write-Host "  .\setup.ps1 -Machine 2 -Token `"$pairingToken`"" -ForegroundColor White
    Write-Host ""
    Write-Host "Keep this token private." -ForegroundColor Yellow
    exit 0
}

Write-Host "=== ClipboardSync setup: machine 2 ===" -ForegroundColor Cyan

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw 'Machine 2 requires the token printed by machine 1. Example: .\setup.ps1 -Machine 2 -Token "<token-from-machine-1>"'
}

$installArgs = @("-Token", $Token) + $installArgs
Invoke-Install $installArgs

Write-Host ""
Write-Host "Machine 2 is ready." -ForegroundColor Green
