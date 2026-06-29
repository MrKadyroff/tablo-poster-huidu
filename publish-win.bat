@echo off
setlocal EnableDelayedExpansion

set "PROJ=%~dp0LedImageUpdaterService.csproj"
set "SRC=%~dp0"
set "OUT=%~dp0publish-win"
set "ZIP=%~dp0LedImageUpdater-deploy.zip"

echo ============================================================
echo  LedImageUpdater -- сборка Windows x64 (self-contained)
echo ============================================================
echo.

:: ── 1. Удалить старый publish ─────────────────────────────────────────────
if exist "%OUT%" rd /s /q "%OUT%"

:: ── 2. Собрать single-file exe ────────────────────────────────────────────
dotnet publish "%PROJ%" ^
  --configuration Release ^
  --runtime win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=none ^
  --output "%OUT%"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] dotnet publish вернул ошибку. Проверьте вывод выше.
    exit /b 1
)

:: ── 3. Скрипты управления ─────────────────────────────────────────────────
copy /Y "%SRC%install-service.ps1" "%OUT%\" >nul
copy /Y "%SRC%start.bat"           "%OUT%\" >nul
copy /Y "%SRC%stop.bat"            "%OUT%\" >nul

:: ── 4. Конфиг-файлы ───────────────────────────────────────────────────────
:: appsettings.json уже скопировал dotnet publish
:: config/points - переопределения конфига для каждой точки
if not exist "%OUT%\config\points" mkdir "%OUT%\config\points"
xcopy /E /I /Y "%SRC%config" "%OUT%\config" >nul

:: ── 5. layout (compose-конфиги и индекс точек) ────────────────────────────
xcopy /E /I /Y "%SRC%layout" "%OUT%\layout" >nul

:: ── 6. Общие ресурсы (флаги, шрифты, overlays) ───────────────────────────
xcopy /E /I /Y "%SRC%content\common" "%OUT%\content\common" >nul

:: ── 7. Контент точек (images/rates/network/...) ──────────────────────────
:: Копируем ВСЮ папку точки, чтобы в поставке были актуальные rates.json,
:: network.json, images и прочие point-specific файлы.
for %%P in (
    megapark
    grand-park
    forum
    zeleny-bazar
    aport
    aport2
    khanshatyr
    saryarka
    asia-park
    eurasia
    arujan
) do (
    if exist "%SRC%content\points\%%P" (
        xcopy /E /I /Y "%SRC%content\points\%%P" "%OUT%\content\points\%%P" >nul
    ) else (
        if not exist "%OUT%\content\points\%%P" mkdir "%OUT%\content\points\%%P"
    )
    if not exist "%OUT%\content\points\%%P\output" mkdir "%OUT%\content\points\%%P\output"
)

:: ── 8. Служебные папки ────────────────────────────────────────────────────
if not exist "%OUT%\logs"         mkdir "%OUT%\logs"
if not exist "%OUT%\relay-output" mkdir "%OUT%\relay-output"

echo.
echo [OK] Опубликовано в: %OUT%
echo.

:: ── 9. Упаковать в zip ────────────────────────────────────────────────────
echo Упаковываю в zip...
powershell -NoProfile -Command ^
  "Compress-Archive -Path '%OUT%\*' -DestinationPath '%ZIP%' -Force"

if %ERRORLEVEL% neq 0 (
    echo [WARN] zip не создан, но папка готова.
) else (
    echo [OK] Архив: %ZIP%
)

echo.
echo ============================================================
echo  Точки в поставке:
echo    megapark, grand-park, forum, zeleny-bazar,
echo    aport, aport2, khanshatyr, saryarka,
echo    asia-park, eurasia, arujan
echo ============================================================
echo  Деплой на Windows-комп:
echo ============================================================
echo  1. Скопировать %ZIP% на целевой ПК (или папку publish-win\)
echo  2. Распаковать в любую папку, например C:\LedImageUpdater\
echo  3. Открыть appsettings.json и проверить:
echo       - ActivePointId        -- id точки (например: aport2)
echo       - config\points\<pointId^>.json -- IP/порт/размер экрана
echo       - layout\points\index.json      -- depCode для API курсов
echo  4. Запустить install-service.ps1 от Администратора
echo  5. Сервис будет стартовать автоматически с Windows
echo ============================================================
echo.
