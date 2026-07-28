<#
.SYNOPSIS
  Removes the --tray companion from current-user logon startup (HKCU Run key).

  Does not require elevation. Does not stop a tray process that is already running.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValueNames = @(
    'LG TV Display Power Sync',
    'lgtv-display-sync-tray' # legacy name from earlier installs
)

if (-not (Test-Path -LiteralPath $RunKeyPath)) {
    Write-Host "HKCU Run key not found; nothing to remove."
    return
}

$removed = 0
foreach ($name in $RunValueNames) {
    $existing = Get-ItemProperty -LiteralPath $RunKeyPath -Name $name -ErrorAction SilentlyContinue
    if (-not $existing) { continue }
    Remove-ItemProperty -LiteralPath $RunKeyPath -Name $name -ErrorAction Stop
    Write-Host "Removed tray logon startup (HKCU Run '$name')."
    $removed++
}

if ($removed -eq 0) {
    Write-Host "Tray startup is not registered."
    return
}

Write-Host "If the tray is already running, Exit from its menu (or end the process) to quit this session."
