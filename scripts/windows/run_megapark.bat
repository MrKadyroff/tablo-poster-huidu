@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM Usage:
REM   run_megapark.bat [WIDTH] [HEIGHT]
REM Example:
REM   run_megapark.bat 560 80

set "TARGET_SSID=BX-Y04291B"
set "WIDTH=%~1"
set "HEIGHT=%~2"

if "%WIDTH%"=="" set "WIDTH=560"
if "%HEIGHT%"=="" set "HEIGHT=80"

echo [1/4] Checking Wi-Fi connection...
for /f "tokens=2 delims=:" %%A in ('netsh wlan show interfaces ^| findstr /R /C:"^[ ]*SSID[ ]*:[ ]*" ^| findstr /V "BSSID"') do (
  set "CURRENT_SSID=%%A"
)

if not defined CURRENT_SSID (
  echo ERROR: Could not detect current Wi-Fi SSID.
  echo Make sure Wi-Fi is enabled and connected.
  exit /b 1
)

set "CURRENT_SSID=!CURRENT_SSID:~1!"

echo Current SSID: !CURRENT_SSID!
if /I not "!CURRENT_SSID!"=="%TARGET_SSID%" (
  echo ERROR: Connected SSID is not %TARGET_SSID%.
  echo Connect to Wi-Fi "%TARGET_SSID%" and run again.
  exit /b 1
)

echo [2/4] Composing final image for screen %WIDTH%x%HEIGHT%...
python layout/tools/compose_ordered_images.py --auto-layout --save-generated-config --point megapark --width %WIDTH% --height %HEIGHT% --config layout/points/megapark.compose.json
if errorlevel 1 (
  echo ERROR: Compose step failed.
  exit /b 1
)

echo [3/4] Starting uploader service...
echo It will try to send final image from content/points/megapark/output/final.jpg
dotnet run
if errorlevel 1 (
  echo ERROR: dotnet run failed.
  exit /b 1
)

echo [4/4] Done.
exit /b 0
