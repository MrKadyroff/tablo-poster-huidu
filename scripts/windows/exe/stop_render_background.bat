@echo off
setlocal EnableExtensions

taskkill /IM LedImageUpdaterService.exe /F >nul 2>&1
if errorlevel 1 (
  echo Process not running.
  exit /b 0
)

echo Stopped LedImageUpdaterService.exe
exit /b 0
