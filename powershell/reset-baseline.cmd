@echo off
chcp 936 >nul
setlocal EnableExtensions
title QuietMode - rebuild baseline
rem Rebuilds baseline.json so the old-schema warning goes away.
rem MUST run in the daytime phase (maintenance tasks enabled), otherwise the
rem disabled state gets recorded as the original baseline.
rem Pass "nopause" as first arg to auto-close (used when launched by the agent).

set "PAUSEIT=1"
if /i "%~1"=="nopause" set "PAUSEIT="

set "SRC=%~dp0_publish\quiet-windows-toolkit\powershell\Reset-Baseline.ps1"
set "LOG=%~dp0_baseline.log"

net session >nul 2>&1
if not errorlevel 1 goto elevated
echo.
echo   Administrator rights required. Click YES on the UAC prompt.
set "ARGS="
if not defined PAUSEIT set "ARGS=-ArgumentList 'nopause'"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs %ARGS%"
exit /b

:elevated
if not exist "%SRC%" goto nosrc

echo.
echo ================= BASELINE REBUILD =================
powershell -NoProfile -ExecutionPolicy Bypass -File "%SRC%" -Apply > "%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
powershell -NoProfile -Command "$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); Get-Content -LiteralPath '%LOG%' -Encoding UTF8"
echo.
echo   exit code = %RC%   [0 ok / 2 no admin or no qmctl / 3 night phase or backup failed / 4 create failed / 5 verify failed]
echo   log       = %LOG%
echo.
if defined PAUSEIT pause
exit /b %RC%

:nosrc
echo [ERROR] script not found: %SRC%
if defined PAUSEIT pause
exit /b 3
