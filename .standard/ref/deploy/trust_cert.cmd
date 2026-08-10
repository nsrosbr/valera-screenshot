@echo off
REM ============================================================================
REM  <APP> - trust the publisher certificate on THIS machine.
REM  Adds "<CERT_ORG>" cert to Trusted Root + Trusted Publishers, so the
REM  self-signed signature validates as TRUSTED (removes "unknown publisher",
REM  calms SmartScreen and many AV heuristics).
REM  RIGHT-CLICK -> "Run as administrator".
REM ============================================================================
setlocal
set "CER=%~dp0<CER>"
if not exist "%CER%" set "CER=%~dp0..\<CER>"
if not exist "%CER%" (
  echo ERROR: <CER> not found next to this script.
  pause & exit /b 1
)

net session >nul 2>&1
if errorlevel 1 (
  echo ERROR: run this as Administrator ^(right-click -^> Run as administrator^).
  pause & exit /b 1
)

echo Installing certificate into Trusted Root...
certutil -addstore -f Root "%CER%"
echo.
echo Installing certificate into Trusted Publishers...
certutil -addstore -f TrustedPublisher "%CER%"
echo.
echo DONE. <APP> is now a trusted publisher on this machine.
pause
