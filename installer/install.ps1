param(
    [string]$Token,
    [switch]$Uninstall,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $PSScriptRoot "ClipboardSync.exe"
$configPath = Join-Path $PSScriptRoot "appsettings.json"
$taskName = "ClipboardSync"
$appName = "ClipboardSync"
$description = "Secure P2P clipboard sync for Windows"
$logDir = Join-Path $env:LOCALAPPDATA "ClipboardSync\logs"
$startupShortcut = Join-Path ([Environment]::GetFolderPath("Startup")) "ClipboardSync.lnk"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-Base64Url([byte[]]$Bytes) {
    return [Convert]::ToBase64String($Bytes).TrimEnd("=").Replace("+", "-").Replace("/", "_")
}

function New-ClipboardSyncToken {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return ConvertTo-Base64Url $bytes
}

function Save-Token([string]$Value) {
    if (-not (Test-Path $configPath)) {
        throw "appsettings.json not found at $configPath"
    }

    $json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if (-not $json.Auth) {
        $json | Add-Member -MemberType NoteProperty -Name Auth -Value ([pscustomobject]@{})
    }
    $json.Auth.Token = $Value
    $json | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding UTF8
}

function Get-ConfigToken {
    if (-not (Test-Path $configPath)) { return "" }
    try {
        $json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        return [string]$json.Auth.Token
    } catch {
        return ""
    }
}

function Remove-Startup {
    $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($existingTask) {
        try {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
            Write-Host "  Scheduled task removed." -ForegroundColor Cyan
        } catch {
            Write-Host "  Could not remove existing scheduled task: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    if (Test-Path $startupShortcut) {
        Remove-Item -LiteralPath $startupShortcut -Force
        Write-Host "  Startup shortcut removed." -ForegroundColor Cyan
    }
}

function Stop-ClipboardSyncProcesses {
    $running = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "ClipboardSync*" }

    if ($running) {
        $running | Stop-Process -Force
        Write-Host "  Running ClipboardSync processes stopped." -ForegroundColor Cyan
    }
}

function Register-Startup {
    Remove-Startup

    $action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $PSScriptRoot
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $principal = New-ScheduledTaskPrincipal `
        -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) `
        -LogonType Interactive `
        -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -Hidden

    try {
        Register-ScheduledTask `
            -TaskName $taskName `
            -Action $action `
            -Trigger $trigger `
            -Principal $principal `
            -Settings $settings `
            -Description $description `
            -ErrorAction Stop | Out-Null
        Write-Host "  Startup task registered for the current user." -ForegroundColor Gray
        return
    } catch {
        Write-Host "  Task Scheduler registration failed, using Startup shortcut fallback." -ForegroundColor Yellow
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startupShortcut)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.Description = $description
    $shortcut.WindowStyle = 7
    $shortcut.Save()
    Write-Host "  Startup shortcut created: $startupShortcut" -ForegroundColor Gray
}

function Add-FirewallRulesIfPossible {
    if (-not (Test-IsAdmin)) {
        Write-Host "  Firewall rules not changed because this shell is not elevated." -ForegroundColor Yellow
        Write-Host "  If peers cannot connect, run this once as Administrator:" -ForegroundColor Yellow
        Write-Host "    New-NetFirewallRule -DisplayName ClipboardSync -Direction Inbound -Program `"$exePath`" -Action Allow" -ForegroundColor Gray
        return
    }

    $existing = Get-NetFirewallRule -DisplayName $appName -ErrorAction SilentlyContinue
    if ($existing) {
        $existing | Remove-NetFirewallRule
    }

    New-NetFirewallRule `
        -DisplayName $appName `
        -Direction Inbound `
        -Program $exePath `
        -Action Allow `
        -Profile Private `
        -Description $description | Out-Null

    Write-Host "  Firewall rule added for Private networks." -ForegroundColor Gray
}

if ($Uninstall) {
    Write-Host "Uninstalling ClipboardSync..." -ForegroundColor Yellow
    Remove-Startup

    Stop-ClipboardSyncProcesses

    if (Test-IsAdmin) {
        Get-NetFirewallRule -DisplayName $appName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        Write-Host "  Firewall rule removed." -ForegroundColor Cyan
    }

    Write-Host ""
    Write-Host "ClipboardSync has been uninstalled. Application files were left in place." -ForegroundColor Green
    exit 0
}

Write-Host "Installing ClipboardSync..." -ForegroundColor Cyan

if (-not (Test-Path $exePath)) {
    throw "ClipboardSync.exe not found. Run .\publish.ps1 first."
}

if (-not (Test-Path $configPath)) {
    throw "appsettings.json not found. Run .\publish.ps1 first."
}

$configuredToken = Get-ConfigToken
$generatedToken = $false
if ($PSBoundParameters.ContainsKey("Token") -and [string]::IsNullOrWhiteSpace($Token)) {
    throw 'Token cannot be empty. On the first machine run .\install.ps1, then copy the printed -Token value to the second machine.'
}

if ([string]::IsNullOrWhiteSpace($Token)) {
    if ([string]::IsNullOrWhiteSpace($configuredToken) -or $configuredToken.Trim().ToLowerInvariant() -eq "changeme") {
        $Token = New-ClipboardSyncToken
        $generatedToken = $true
    } else {
        $Token = $configuredToken.Trim()
    }
}

Save-Token $Token
Register-Startup
Add-FirewallRulesIfPossible

try {
    [void][IO.Directory]::CreateDirectory($logDir)
} catch {
    Write-Host "  Could not create log directory: $logDir" -ForegroundColor Yellow
}

if (-not $NoStart) {
    Stop-ClipboardSyncProcesses
    Start-Process -FilePath $exePath -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
    Write-Host "  ClipboardSync started." -ForegroundColor Gray
}

Write-Host ""
Write-Host "SUCCESS: ClipboardSync is installed for the current user." -ForegroundColor Green
Write-Host "  Tray app: look for ClipboardSync in the system tray." -ForegroundColor Gray
Write-Host "  Logs: $logDir" -ForegroundColor Gray
Write-Host "  Uninstall: .\install.ps1 -Uninstall" -ForegroundColor Gray

if ($generatedToken) {
    Write-Host ""
    Write-Host "Pair another Windows machine with this command:" -ForegroundColor Yellow
    Write-Host "  .\install.ps1 -Token `"$Token`"" -ForegroundColor White
    Write-Host "Keep this token private. Anyone with it can join this clipboard sync group." -ForegroundColor Yellow
}
