@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "TARGET_SSID=BX-Y04291B"
for /f "tokens=2 delims=:" %%A in ('netsh wlan show interfaces ^| findstr /R /C:"^[ ]*SSID[ ]*:[ ]*" ^| findstr /V "BSSID"') do (
  set "CURRENT_SSID=%%A"
)

if not defined CURRENT_SSID (
  echo ERROR: Could not detect current Wi-Fi SSID.
  exit /b 1
)

set "CURRENT_SSID=!CURRENT_SSID:~1!"
echo Current SSID: !CURRENT_SSID!

if /I "!CURRENT_SSID!"=="%TARGET_SSID%" (
  echo OK: Connected to %TARGET_SSID%.
  exit /b 0
)

echo ERROR: Not connected to %TARGET_SSID%.
exit /b 1
