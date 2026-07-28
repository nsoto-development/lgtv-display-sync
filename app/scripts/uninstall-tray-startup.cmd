@echo off
setlocal
rem Wrapper so release zips (Mark of the Web / RemoteSigned) can run the unsigned .ps1.
rem No elevation required (HKCU Run).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-tray-startup.ps1" %*
exit /b %ERRORLEVEL%
