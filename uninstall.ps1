# STATUS 2026-07-29: DEV-ONLY, NOT SHIPPED.
# This script existed to register an unpacked PORTABLE copy per-user. The portable package is
# discontinued (owner's decision), so nothing ships this file any more and no document points at
# it. It is kept because it is still the quickest way to put a freshly built dev copy into the
# machine without admin rights - Setup.exe covers the same ground for users, and honestly offers
# a profile folder when it cannot get elevation.
# DO NOT add it back to a package without also adding it to FieldProbe's install/uninstall
# symmetry matrix: "seven ways in, three ways out" is precisely how the paths drifted apart here
# once, and every path that ships must be measured.
# ValeraScreenshot uninstaller - reverses everything install.ps1 did (per-user, no admin).
# The folder itself and the Screenshots library are NEVER deleted (your data stays).
# -RemoveSettings additionally deletes settings.ini.
# ASCII-only on purpose (PS 5.1 reads .ps1 as ANSI).
param([switch]$RemoveSettings)

$ErrorActionPreference = "SilentlyContinue"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1) stop the app
$p = Get-Process -Name "ValeraScreenshot" -ErrorAction SilentlyContinue
if ($p) { $p | Stop-Process -Force; Write-Host "OK  process stopped" }

# 2) shortcuts
$startLnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ValeraScreenshot.lnk"
if (Test-Path $startLnk) { Remove-Item $startLnk -Force; Write-Host "OK  Start Menu shortcut removed" }
$deskLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "ValeraScreenshot.lnk"
if (Test-Path $deskLnk) { Remove-Item $deskLnk -Force; Write-Host "OK  Desktop shortcut removed" }

# 3) autostart
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Get-ItemProperty -Path $run -Name "ValeraScreenshot" -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $run -Name "ValeraScreenshot"
    Write-Host "OK  autostart removed"
}

# 4) Apps & Features entry
$un = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ValeraScreenshot"
if (Test-Path $un) { Remove-Item $un -Recurse -Force; Write-Host "OK  Apps & Features entry removed" }

# 5) settings (only on request)
if ($RemoveSettings) {
    Remove-Item (Join-Path $root "settings.ini") -Force
    Remove-Item (Join-Path $env:APPDATA "ValeraScreenshot") -Recurse -Force
    Write-Host "OK  settings removed"
}

Write-Host ""
Write-Host "UNINSTALLED. The folder and Screenshots\ are kept (delete manually if not needed)."
