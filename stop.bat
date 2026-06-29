@echo off
:: Остановить задачу в Планировщике (если установлена через install-service.ps1)
PowerShell -NoProfile -Command "Stop-ScheduledTask -TaskName 'LedImageUpdater' -ErrorAction SilentlyContinue"
if %ERRORLEVEL% equ 0 (
    echo [OK] Задача LedImageUpdater остановлена.
) else (
    echo [WARN] Задача не найдена или уже остановлена.
)
pause
