<#
.SYNOPSIS
  Registers the --tray companion to start at user logon (current-user Run key).

  Does not require elevation. Intended to run from the build/publish output folder
  (copied next to the exe). Resolves the exe as a sibling of this script unless
  -ExePath is supplied.

.PARAMETER ExePath
  Optional absolute path to lgtv-display-sync.exe. Defaults to
  <this-script-directory>\lgtv-display-sync.exe.

.PARAMETER StartNow
  Also launch the tray companion in this session after registering.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string] $ExePath,

    [Parameter()]
    [switch] $StartNow
)

$ErrorActionPreference = 'Stop'

$RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValueName = 'LG TV Display Power Sync'
$LegacyRunValueName = 'lgtv-display-sync-tray'

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $PSScriptRoot 'lgtv-display-sync.exe'
} else {
    $ExePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Executable not found: $ExePath (build/publish first, then run this script from the output folder)."
}

$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
# Quote the exe path so spaces are safe; --tray stays outside the quotes.
$command = "`"$ExePath`" --tray"

if (-not (Test-Path -LiteralPath $RunKeyPath)) {
    New-Item -Path $RunKeyPath -Force | Out-Null
}

Set-ItemProperty -LiteralPath $RunKeyPath -Name $RunValueName -Value $command -Type String

# Drop the older registry value name if present (pre-rename installs).
$legacy = Get-ItemProperty -LiteralPath $RunKeyPath -Name $LegacyRunValueName -ErrorAction SilentlyContinue
if ($legacy) {
    Remove-ItemProperty -LiteralPath $RunKeyPath -Name $LegacyRunValueName -ErrorAction SilentlyContinue
}

Write-Host "Registered tray logon startup (HKCU Run '$RunValueName'):"
Write-Host "  $command"
Write-Host "Remove with uninstall-tray-startup.ps1. The Windows service is separate (install-service.ps1)."

if ($StartNow) {
    Start-Process -FilePath $ExePath -ArgumentList '--tray'
    Write-Host "Started tray companion in this session."
}
