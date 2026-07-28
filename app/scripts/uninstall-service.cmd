@echo off
setlocal
rem Wrapper so release zips (Mark of the Web / RemoteSigned) can run the unsigned .ps1.
rem Self-elevates when not already admin (service uninstall requires elevation).
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -LiteralPath '%~f0' -Verb RunAs -Wait"
  exit /b %ERRORLEVEL%
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-service.ps1" %*
exit /b %ERRORLEVEL%
