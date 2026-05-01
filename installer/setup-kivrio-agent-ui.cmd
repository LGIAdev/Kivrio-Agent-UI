@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-kivrio-agent-ui.ps1" -PackageDir "%~dp0"
exit /b %errorlevel%
