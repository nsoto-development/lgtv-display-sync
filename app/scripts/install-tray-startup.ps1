<#
.SYNOPSIS
  Registers the --tray companion to start at user logon (current-user Run key)
  and launches the tray icon in this session.

  Does not require elevation. Intended to run from the build/publish output folder
  (copied next to the exe). Resolves the exe as a sibling of this script unless
  -ExePath is supplied.

.PARAMETER ExePath
  Optional absolute path to lgtv-display-sync.exe. Defaults to
  <this-script-directory>\lgtv-display-sync.exe.

.PARAMETER NoStart
  Register logon startup only; do not launch the tray in this session.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string] $ExePath,

    [Parameter()]
    [switch] $NoStart
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
Write-Host "Remove with uninstall-tray-startup.cmd (or .ps1). The Windows service is separate (install-service.cmd)."

if ($NoStart) {
    Write-Host "Skipped launching tray (-NoStart)."
    return
}

$trayAlreadyRunning = Get-CimInstance Win32_Process -Filter "Name = 'lgtv-display-sync.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and ($_.CommandLine -match '(^|\s)--tray(\s|$)') }

if ($trayAlreadyRunning) {
    Write-Host "Tray companion is already running; left existing icon as-is."
    return
}

$exeDir = Split-Path -Parent $ExePath
$proc = Start-Process -FilePath $ExePath -ArgumentList '--tray' -WorkingDirectory $exeDir -PassThru
Start-Sleep -Milliseconds 1500
if ($proc.HasExited) {
    throw @"
Tray companion exited immediately (exit code $($proc.ExitCode)).
Often this means the install folder is a broken mix of framework-dependent and self-contained files
(local hostfxr.dll + a runtimeconfig that still requires Microsoft.NETCore.App).
Republish with: dotnet publish app -c Release -r win-x64 --self-contained true -o `"$exeDir`"
Or run from a Debug/Release build output that matches how it was built.
"@
}

Write-Host "Started tray companion in this session (PID $($proc.Id))."
