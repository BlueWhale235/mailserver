@echo off
setlocal
set "PSModulePath="
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-windows.ps1"
set "result=%errorlevel%"
if not "%result%"=="0" pause
exit /b %result%
