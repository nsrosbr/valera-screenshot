@echo off
rem ValeraScreenshot installer (per-user, no admin). Options are passed through, e.g.:
rem   INSTALL.bat -Autostart -DesktopShortcut -FreePrtScr
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
pause
