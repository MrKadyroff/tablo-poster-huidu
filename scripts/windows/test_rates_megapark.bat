@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "WIDTH=%~1"
set "HEIGHT=%~2"
if "%WIDTH%"=="" set "WIDTH=560"
if "%HEIGHT%"=="" set "HEIGHT=80"
set "RATES_FILE=content\points\megapark\rates.json"

echo === Megapark JSON test rate input ===
set /p USD_BUY=Enter USD buy (example 89.45): 
set /p USD_SELL=Enter USD sell (example 90.15): 
set /p EUR_BUY=Enter EUR buy (example 96.73): 
set /p EUR_SELL=Enter EUR sell (example 97.48): 

if "%USD_BUY%"=="" set "USD_BUY=89.45"
if "%USD_SELL%"=="" set "USD_SELL=90.15"
if "%EUR_BUY%"=="" set "EUR_BUY=96.73"
if "%EUR_SELL%"=="" set "EUR_SELL=97.48"

echo Writing %RATES_FILE% ...
(
  echo {
  echo   "USD": {
  echo     "buy": %USD_BUY%,
  echo     "sell": %USD_SELL%
  echo   },
  echo   "EUR": {
  echo     "buy": %EUR_BUY%,
  echo     "sell": %EUR_SELL%
  echo   }
  echo }
) > "%RATES_FILE%"

echo Building final.jpg from rates.json...
python layout/tools/compose_ordered_images.py --auto-layout --save-generated-config --point megapark --width %WIDTH% --height %HEIGHT% --config layout/points/megapark.compose.json --rates-json "%RATES_FILE%"
if errorlevel 1 (
  echo ERROR: compose failed.
  exit /b 1
)

echo Done: content\points\megapark\output\final.jpg
exit /b 0
