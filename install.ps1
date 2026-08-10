# STATUS 2026-07-29: DEV-ONLY, NOT SHIPPED.
# This script existed to register an unpacked PORTABLE copy per-user. The portable package is
# discontinued (owner's decision), so nothing ships this file any more and no document points at
# it. It is kept because it is still the quickest way to put a freshly built dev copy into the
# machine without admin rights - Setup.exe covers the same ground for users, and honestly offers
# a profile folder when it cannot get elevation.
# DO NOT add it back to a package without also adding it to FieldProbe's install/uninstall
# symmetry matrix: "seven ways in, three ways out" is precisely how the paths drifted apart here
# once, and every path that ships must be measured.
# ValeraScreenshot installer - per-user, NO admin rights needed. The app stays in THIS folder
# (portable, "everything in one working folder"); the installer only registers it:
#   * Start Menu shortcut
#   * "Apps & Features" (uninstall) entry under HKCU
#   * optional: -Autostart (HKCU Run), -DesktopShortcut, -FreePrtScr (unbind Snipping Tool)
# ASCII-only on purpose (PS 5.1 reads .ps1 as ANSI).
param(
    [switch]$Autostart,
    [switch]$DesktopShortcut,
    [switch]$FreePrtScr,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $root "ValeraScreenshot.exe"
if (-not (Test-Path $exe)) { throw "ValeraScreenshot.exe not found - run build.ps1 first" }

# The portable zip ships portable.txt so an unpacked copy never installs itself. Running THIS
# script is the explicit opposite intent, so the marker is retired here - otherwise every
# registry step below would be silently refused by the app's portable guard.
$marker = Join-Path $root "portable.txt"
if (Test-Path $marker) {
    Remove-Item $marker -Force
    Write-Host "OK  portable marker removed (you asked for a real install)"
}

# The app decides where its config and screenshots live (Config.Dir: next to the exe when that
# folder is writable, else %APPDATA%). Creating a Screenshots folder here by hand used to make
# an always-empty decoy next to a Program-Files install.
$seedIni = Join-Path $root "settings.ini"
if ($Autostart) {
    # Seed the CONFIG before the Run key: the app aligns the key to the config on every start,
    # so a key written without the config is erased by the first launch.
    $lines = @()
    if (Test-Path $seedIni) {
        $lines = [System.IO.File]::ReadAllLines($seedIni, [System.Text.Encoding]::UTF8) |
                 Where-Object { $_ -notmatch '^StartWithWindows=' }
    }
    $lines += "StartWithWindows=True"
    [System.IO.File]::WriteAllLines($seedIni, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "OK  Autostart intent seeded into settings.ini"
}

# 1) Start Menu shortcut (+ optional Desktop)
$ws = New-Object -ComObject WScript.Shell
$startLnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ValeraScreenshot.lnk"
$sc = $ws.CreateShortcut($startLnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $root
$sc.IconLocation = "$exe,0"
$sc.Description = "ValeraScreenshot - screenshots"
$sc.Save()
Write-Host "OK  Start Menu shortcut: $startLnk"

if ($DesktopShortcut) {
    $deskLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "ValeraScreenshot.lnk"
    $sc = $ws.CreateShortcut($deskLnk)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = $root
    $sc.IconLocation = "$exe,0"
    $sc.Save()
    Write-Host "OK  Desktop shortcut: $deskLnk"
}

# 2) autostart (also toggleable later in the app settings)
if ($Autostart) {
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
        -Name "ValeraScreenshot" -Value "`"$exe`""
    Write-Host "OK  Autostart enabled (HKCU Run)"
}

# 3) free PrtScr from Snipping Tool (per-user setting)
if ($FreePrtScr) {
    Set-ItemProperty -Path "HKCU:\Control Panel\Keyboard" `
        -Name "PrintScreenKeyForSnippingEnabled" -Value 0 -Type DWord
    Write-Host "OK  PrtScr freed from Snipping Tool"
}

# 4) Apps and Features entry.
# The card is written by the APP (Arp.cs, the single writer - STD-LIFE-04), not by hand here.
# This script used to keep its own copy, and it had already drifted: DisplayName "ValeraScreenshot"
# instead of the real one, DisplayVersion frozen at "1.0.0", no QuietUninstallString. The app's
# SelfHeal then saw the stale version, refreshed the card and ADDED a QuietUninstallString
# pointing at its own silent uninstall - a path this script never intended to arm.
& $exe /install-card "$root" 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "OK  Apps and Features entry registered (via Arp.cs, single writer)"
} else {
    Write-Host "WARN Apps and Features entry not written (app returned $LASTEXITCODE)"
}

# 5) launch
if (-not $NoLaunch) {
    if (-not (Get-Process -Name "ValeraScreenshot" -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $exe -WorkingDirectory $root
        Write-Host "OK  ValeraScreenshot started (tray icon)"
    } else {
        Write-Host "OK  ValeraScreenshot already running"
    }
}

Write-Host ""
Write-Host "INSTALLED. Ctrl+Shift+4 = region capture, Ctrl+Shift+3 = full screen"
Write-Host "(PrtScr / Shift+PrtScr also work as a bonus when the key exists)."
Write-Host "Uninstall: UNINSTALL.bat (or Apps & Features -> ValeraScreenshot)."
