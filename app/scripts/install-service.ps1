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
$DisplayName = 'LG TV Power Resume Sync Utility (nsoto.dev)'
$Description = 'Watches Windows display on/off and syncs an LG webOS TV (Wake-on-LAN + SSAP). Runs in session 0 so resume still works when no user is logged on.'
$DataDir = Join-Path $env:ProgramData 'nsoto.dev\lg-tv-display-sync'

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

# Shared data dir + SYSTEM ACL (LocalSystem must read keys written by an interactive user).
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
& icacls.exe $DataDir /grant '*S-1-5-18:(OI)(CI)M' /T | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "icacls grant for SYSTEM on $DataDir returned exit code $LASTEXITCODE"
}

# Copy legacy LocalAppData key into ProgramData when missing (SYSTEM cannot see the user profile store).
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
    $programDataKey = Join-Path $DataDir "${tvIp}_ClientKey.txt"
    if ((Test-Path -LiteralPath $legacyKey) -and -not (Test-Path -LiteralPath $programDataKey)) {
        Copy-Item -LiteralPath $legacyKey -Destination $programDataKey -Force
        Write-Host "Copied client key from LocalAppData to ProgramData: $programDataKey"
    } elseif (-not (Test-Path -LiteralPath $programDataKey) -and -not (Test-Path -LiteralPath $legacyKey)) {
        Write-Warning "No client key for IP $tvIp in ProgramData or LocalAppData. Pair interactively first (run the exe with --pair), then re-run this script or copy the key into $DataDir."
    }
} else {
    Write-Warning "Skipping key copy (no Ip from config). Pair interactively so a key lands under $DataDir before relying on the service."
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
Write-Host "Startup type: Automatic (starts on boot). Logs: $(Join-Path $DataDir 'log.txt')"
