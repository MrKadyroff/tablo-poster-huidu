@echo off
setlocal EnableExtensions

REM Build single-file Windows EXE
set "RID=win-x64"
set "OUT=publish\%RID%"

echo Publishing EXE for %RID%...
dotnet publish -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o %OUT%
if errorlevel 1 (
  echo ERROR: publish failed.
  exit /b 1
)

echo.
echo EXE ready: %OUT%\LedImageUpdaterService.exe
exit /b 0
