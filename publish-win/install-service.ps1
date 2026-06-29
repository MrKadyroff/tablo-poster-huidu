#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Добавляет LedImageUpdaterService в автозапуск Windows через Планировщик задач.
    Не требует сторонних программ (NSSM не нужен).
    Запускать от имени Администратора.

.PARAMETER TaskName
    Имя задачи. По умолчанию "LedImageUpdater".

.PARAMETER AppDir
    Папка с exe. По умолчанию — папка рядом со скриптом.

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -TaskName "LedRates-Megapark"
#>

param(
    [string]$TaskName = "LedImageUpdater",
    [string]$AppDir   = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

# ─── Найти exe ────────────────────────────────────────────────────────────────
$exe = Join-Path $AppDir "LedImageUpdaterService.exe"
if (-not (Test-Path $exe)) {
    Write-Host ""
    Write-Host "ОШИБКА: Файл не найден: $exe" -ForegroundColor Red
    Write-Host "Убедитесь что скрипт лежит в той же папке что и LedImageUpdaterService.exe" -ForegroundColor Red
    Write-Host ""
    exit 1
}

# ─── Удалить старую задачу (если есть) ───────────────────────────────────────
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Задача '$TaskName' уже есть — обновляю..." -ForegroundColor Yellow
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

# ─── Создать задачу ───────────────────────────────────────────────────────────
$action = New-ScheduledTaskAction `
    -Execute    $exe `
    -WorkingDirectory $AppDir

# Запуск при старте системы (до входа пользователя)
$trigger = New-ScheduledTaskTrigger -AtStartup

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit      (New-TimeSpan -Hours 0) `
    -RestartCount            5 `
    -RestartInterval         (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable `
    -MultipleInstances       IgnoreNew

# Запуск от SYSTEM — работает без входа в Windows
$principal = New-ScheduledTaskPrincipal `
    -UserId    "SYSTEM" `
    -LogonType ServiceAccount `
    -RunLevel  Highest

Register-ScheduledTask `
    -TaskName   $TaskName `
    -Action     $action `
    -Trigger    $trigger `
    -Settings   $settings `
    -Principal  $principal `
    -Description "LED курсы валют — автообновление изображения на табло" `
    | Out-Null

# ─── Запустить сразу ─────────────────────────────────────────────────────────
Write-Host "Запускаю '$TaskName'..." -ForegroundColor Cyan
Start-ScheduledTask -TaskName $TaskName

Start-Sleep -Seconds 2
$status = (Get-ScheduledTask -TaskName $TaskName).State

Write-Host ""
Write-Host "  Задача:  $TaskName" -ForegroundColor Green
Write-Host "  Статус:  $status" -ForegroundColor Green
Write-Host "  Папка:   $AppDir" -ForegroundColor Green
Write-Host "  Логи:    $AppDir\logs\" -ForegroundColor Green
Write-Host ""
Write-Host "Управление:" -ForegroundColor DarkGray
Write-Host "  Запустить:    Start-ScheduledTask -TaskName $TaskName"
Write-Host "  Остановить:   Stop-ScheduledTask  -TaskName $TaskName"
Write-Host "  Удалить:      Unregister-ScheduledTask -TaskName $TaskName -Confirm:`$false"
Write-Host "  Через GUI:    taskschd.msc → Библиотека планировщика → $TaskName"
Write-Host ""


.PARAMETER ServiceDir
    Папка, откуда запускать сервис. По умолчанию — папка рядом со скриптом.

.PARAMETER ServiceName
    Имя Windows-службы. По умолчанию "LedImageUpdater".

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -ServiceName "LedRates-MegaPark"
#>

param(
    [string]$ServiceName = "LedImageUpdater",
    [string]$ServiceDir  = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

# ─── Проверка наличия NSSM ────────────────────────────────────────────────────
$nssm = Get-Command nssm -ErrorAction SilentlyContinue
if (-not $nssm) {
    Write-Host "NSSM не найден. Устанавливаю через winget..." -ForegroundColor Yellow
    winget install --id NSSM.NSSM -e --accept-package-agreements --accept-source-agreements
    # Обновляем PATH
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH","Machine")
    $nssm = Get-Command nssm -ErrorAction SilentlyContinue
    if (-not $nssm) {
        Write-Error "Не удалось установить NSSM. Скачайте вручную: https://nssm.cc/download"
    }
}

$nssmExe = $nssm.Source

# ─── Параметры сервиса ────────────────────────────────────────────────────────
$exePath  = Join-Path $ServiceDir "LedImageUpdaterService.exe"
if (-not (Test-Path $exePath)) {
    # Если нет готового exe — используем dotnet run через dotnet.exe
    $dotnet  = (Get-Command dotnet).Source
    $dllPath = Join-Path $ServiceDir "bin\Release\net8.0\LedImageUpdaterService.dll"
    if (-not (Test-Path $dllPath)) {
        Write-Error "Не найден ни LedImageUpdaterService.exe, ни DLL в bin\Release\net8.0. Сначала запустите: dotnet publish -c Release"
    }
    $appExe  = $dotnet
    $appArgs = "`"$dllPath`""
} else {
    $appExe  = $exePath
    $appArgs = ""
}

# ─── Удалить существующий сервис (если есть) ──────────────────────────────────
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Сервис '$ServiceName' уже существует — останавливаю и пересоздаю..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & $nssmExe remove $ServiceName confirm
}

# ─── Установка ────────────────────────────────────────────────────────────────
Write-Host "Устанавливаю '$ServiceName'..." -ForegroundColor Cyan

& $nssmExe install $ServiceName $appExe $appArgs
& $nssmExe set     $ServiceName AppDirectory     $ServiceDir
& $nssmExe set     $ServiceName DisplayName      "LED Image Updater — курсы на табло"
& $nssmExe set     $ServiceName Description      "Получает курсы валют и отправляет изображение на LED-контроллер по FTP."
& $nssmExe set     $ServiceName Start            SERVICE_AUTO_START
& $nssmExe set     $ServiceName AppStdout        "$ServiceDir\logs\service-stdout.log"
& $nssmExe set     $ServiceName AppStderr        "$ServiceDir\logs\service-stderr.log"
& $nssmExe set     $ServiceName AppRotateFiles   1
& $nssmExe set     $ServiceName AppRotateOnline  1
& $nssmExe set     $ServiceName AppRotateBytes   5242880  # 5 MB

# Создать папку логов заранее
New-Item -ItemType Directory -Path "$ServiceDir\logs" -Force | Out-Null

# ─── Старт ────────────────────────────────────────────────────────────────────
Write-Host "Запускаю '$ServiceName'..." -ForegroundColor Cyan
Start-Service -Name $ServiceName

$svc = Get-Service -Name $ServiceName
Write-Host ""
Write-Host "✓ Сервис установлен и запущен." -ForegroundColor Green
Write-Host "  Имя:    $ServiceName"
Write-Host "  Статус: $($svc.Status)"
Write-Host "  Логи:   $ServiceDir\logs\"
Write-Host ""
Write-Host "Команды управления:" -ForegroundColor DarkGray
Write-Host "  Start-Service $ServiceName"
Write-Host "  Stop-Service  $ServiceName"
Write-Host "  Restart-Service $ServiceName"
Write-Host "  nssm remove $ServiceName confirm   # удалить"
