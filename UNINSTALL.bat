@echo off
rem ValeraScreenshot uninstaller. Add -RemoveSettings to also delete settings.ini.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
pause
