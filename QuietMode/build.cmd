@echo off
setlocal
pushd "%~dp0build"
set DOTNET=%ProgramFiles%\dotnet\dotnet.exe
if not exist "%DOTNET%" set DOTNET=dotnet
"%DOTNET%" build QuietMode.csproj -c Release -v minimal --nologo
if errorlevel 1 (
  popd
  echo BUILD FAILED
  pause
  exit /b 1
)
taskkill /f /im QuietMode.exe >nul 2>&1
copy /Y "bin\Release\QuietMode.exe" "..\QuietMode.exe" >nul
if exist "bin\Release\QuietMode.exe.config" copy /Y "bin\Release\QuietMode.exe.config" "..\QuietMode.exe.config" >nul
popd
echo BUILD OK - %~dp0QuietMode.exe
pause
