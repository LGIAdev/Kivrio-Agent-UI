@echo off
setlocal
for %%I in ("%~dp0.") do set "ROOT=%%~fI"
set "PORT=8000"
set "SERVER_EXE=%ROOT%\bin\kivrio-agent-ui-server.exe"
set "SERVER_SRC=%ROOT%\server\KivrioAgentUiServer.cs"
set "WAIT_SECONDS=30"
set "NEED_COMPILE=0"

if not exist "%SERVER_EXE%" set "NEED_COMPILE=1"
if "%NEED_COMPILE%"=="0" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "if ((Get-Item -LiteralPath '%SERVER_SRC%').LastWriteTimeUtc -gt (Get-Item -LiteralPath '%SERVER_EXE%').LastWriteTimeUtc) { exit 0 } exit 1" >nul 2>nul
  if not errorlevel 1 set "NEED_COMPILE=1"
)

if "%NEED_COMPILE%"=="1" (
  call :compile_server
  if errorlevel 1 (
    pause
    exit /b 1
  )
)

netstat -ano | findstr /R /C:":%PORT% .*LISTENING" >nul && set "PORT=8001"

start "" /D "%ROOT%" "%SERVER_EXE%" --root "%ROOT%" --host 127.0.0.1 --port %PORT%
set "STATUS_URL=http://127.0.0.1:%PORT%/api/auth/status"
for /L %%I in (1,1,%WAIT_SECONDS%) do (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $r = Invoke-WebRequest -UseBasicParsing -Uri '%STATUS_URL%' -TimeoutSec 2; if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 500) { exit 0 } else { exit 1 } } catch { exit 1 }" >nul 2>nul
  if not errorlevel 1 goto open_browser
  timeout /t 1 /nobreak >nul
)
echo [ERREUR] Kivrio Agent UI n'a pas demarre sur le port %PORT%.
exit /b 1

:open_browser
start "" "http://127.0.0.1:%PORT%/index.html?t=%RANDOM%"
exit /b 0

:compile_server
if not exist "%SERVER_SRC%" (
  echo [ERREUR] Serveur autonome introuvable: "%SERVER_SRC%"
  exit /b 1
)
set "CSC="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not defined CSC (
  echo [ERREUR] Compilateur C# .NET Framework introuvable.
  exit /b 1
)
if not exist "%ROOT%\bin" mkdir "%ROOT%\bin"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%SERVER_EXE%" /r:System.Web.Extensions.dll /r:System.Net.WebSockets.dll /r:System.Net.WebSockets.Client.dll "%SERVER_SRC%"
if errorlevel 1 (
  echo [ERREUR] Compilation du serveur autonome impossible.
  exit /b 1
)
exit /b 0
