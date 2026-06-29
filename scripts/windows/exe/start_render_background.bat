@echo off
setlocal EnableExtensions

set "EXE=publish\win-x64\LedImageUpdaterService.exe"
if not exist "%EXE%" (
  echo ERROR: EXE not found: %EXE%
  echo Run scripts\windows\exe\build_render_exe.bat first.
  exit /b 1
)

REM Start minimized in background
start "LedImageUpdaterService" /min "%EXE%"

echo Started in background: %EXE%
echo It will re-generate final.jpg when rates.json or source images change.
exit /b 0
