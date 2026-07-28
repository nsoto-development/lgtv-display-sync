#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs lgtv-display-sync as an auto-start LocalSystem Windows service.

  Intended to run from the build/publish output folder (copied next to the exe).
  Resolves the exe as a sibling of this script unless -ExePath is supplied.

.PARAMETER ExePath
  Optional absolute path to lgtv-display-sync.exe. Defaults to
  <this-script-directory>\lgtv-display-sync.exe (config.json must sit beside it).
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string] $ExePath
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'lgtv-display-sync'
$DisplayName = 'LG TV Display Power Sync Service'
$Description = 'Watches Windows display on/off and syncs an LG webOS TV (Wake-on-LAN + SSAP). Runs in session 0 so resume still works when no user is logged on.'
$DataDir = Join-Path $env:ProgramData 'nsoto.dev\lg-tv-display-sync'
$ConfigDir = Join-Path $DataDir 'config'
$LogDir = Join-Path $DataDir 'log'

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $PSScriptRoot 'lgtv-display-sync.exe'
} else {
    $ExePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Executable not found: $ExePath (build/publish first, then run this script from the output folder)."
}

$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
Write-Host "Installing service for: $ExePath"

$exeDir = Split-Path -Parent $ExePath
$configPath = Join-Path $exeDir 'config.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    Write-Warning "No config.json next to the exe ($configPath). The service will use built-in placeholder IP/MAC until you add one."
}

# Shared data dir (config\ keys, log\) + SYSTEM ACL (LocalSystem must read keys written by an interactive user).
New-Item -ItemType Directory -Force -Path $ConfigDir, $LogDir | Out-Null
& icacls.exe $DataDir /grant '*S-1-5-18:(OI)(CI)M' /T | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "icacls grant for SYSTEM on $DataDir returned exit code $LASTEXITCODE"
}

# Copy legacy LocalAppData / flat ProgramData key into config\ when missing (SYSTEM cannot see the user profile store).
$tvIp = $null
if (Test-Path -LiteralPath $configPath) {
    try {
        $tvIp = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).Ip
    } catch {
        Write-Warning "Could not parse Ip from config.json: $_"
    }
}

if ($tvIp) {
    $legacyKey = Join-Path $env:LOCALAPPDATA "lgtv-display-sync\${tvIp}_ClientKey.txt"
    $flatKey = Join-Path $DataDir "${tvIp}_ClientKey.txt"
    $programDataKey = Join-Path $ConfigDir "${tvIp}_ClientKey.txt"
    if (-not (Test-Path -LiteralPath $programDataKey)) {
        if (Test-Path -LiteralPath $flatKey) {
            Move-Item -LiteralPath $flatKey -Destination $programDataKey -Force
            Write-Host "Moved flat ProgramData client key into config\: $programDataKey"
        } elseif (Test-Path -LiteralPath $legacyKey) {
            Copy-Item -LiteralPath $legacyKey -Destination $programDataKey -Force
            Write-Host "Copied client key from LocalAppData to ProgramData config\: $programDataKey"
        } else {
            Write-Warning "No client key for IP $tvIp in ProgramData config\ or LocalAppData. Pair interactively first (run the exe with --pair), then re-run this script or copy the key into $ConfigDir."
        }
    }
} else {
    Write-Warning "Skipping key copy (no Ip from config). Pair interactively so a key lands under $ConfigDir before relying on the service."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service '$ServiceName' already exists. Run uninstall-service.ps1 first, or use services.msc to remove it."
}

# New-Service defaults to LocalSystem; DisplayName is what services.msc shows.
New-Service `
    -Name $ServiceName `
    -BinaryPathName $ExePath `
    -DisplayName $DisplayName `
    -Description $Description `
    -StartupType Automatic | Out-Null

Start-Service -Name $ServiceName
Write-Host "Installed and started '$ServiceName' ($DisplayName)."
Write-Host "Startup type: Automatic (starts on boot). Logs: $(Join-Path $LogDir 'log.txt')"
