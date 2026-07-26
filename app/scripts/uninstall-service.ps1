#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Stops and removes the lgtv-display-sync Windows service.

  Copied next to the exe in the build/publish output; run from that folder (or anywhere).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$ServiceName = 'lgtv-display-sync'

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$ServiceName' is not installed."
    return
}

if ($svc.Status -ne 'Stopped') {
    Write-Host "Stopping '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force
}

# Prefer sc delete for broad Windows PowerShell compatibility (Remove-Service needs PS 6+).
& sc.exe delete $ServiceName
if ($LASTEXITCODE -ne 0) {
    throw "sc delete failed with exit code $LASTEXITCODE"
}

Write-Host "Removed service '$ServiceName'."
Write-Host "ProgramData keys/logs were left in place under $(Join-Path $env:ProgramData 'nsoto.dev\lg-tv-display-sync')."
