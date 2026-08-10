# build_setup.ps1 - build the installer exe. Embeds the signed app + docs + uninstaller as the
# valerascreenshot_exe resource (id from standard.bind.json), which setup\Setup.cs reads back.
# Assumes build.ps1 + sign.ps1 already produced a SIGNED ValeraScreenshot.exe. ASCII-only.
#
#   -CompileOnly   compile BOTH package binaries to a scratch folder and throw away the output.
#                  No signed exe needed, no payload, no signing, nothing written into the tree.
#                  This exists so a GATE can exercise the package compiler on every run: see the
#                  note at step 1 for the failure it would have caught months earlier.
param([switch]$CompileOnly)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe (.NET Framework 4.x) not found" }

$ico = Join-Path $root "app.ico"
$setupManifest = Join-Path $root "setup\setup.manifest"
$exe = Join-Path $root "ValeraScreenshot.exe"
if (-not $CompileOnly -and -not (Test-Path $exe)) {
    throw "ValeraScreenshot.exe not found - run build.ps1 + sign.ps1 first"
}

# THE SOURCE LISTS LIVE HERE AND NOWHERE ELSE. Duplicating them into a gate would recreate the
# very drift the gate is meant to catch, so the gate calls this script with -CompileOnly instead.
$unSrc = @(
    (Join-Path $root "setup\Uninstall.cs"),
    (Join-Path $root "src\Loc.cs"),
    (Join-Path $root "src\Ident.cs")
)
$setupSrc = @(
    (Join-Path $root "setup\Setup.cs"),
    (Join-Path $root "src\Ui.cs"),
    (Join-Path $root "src\Ver.cs"),
    (Join-Path $root "src\Loc.cs"),
    (Join-Path $root "src\Ident.cs"),
    (Join-Path $root "src\Arp.cs"),
    (Join-Path $root "src\Seed.cs")
)
$unRefs    = "System.dll,System.Windows.Forms.dll,System.Drawing.dll"
$setupRefs = "System.dll,System.Core.dll,System.Windows.Forms.dll,System.Drawing.dll,System.IO.Compression.dll,Microsoft.CSharp.dll"

if ($CompileOnly) {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ("vs_pkgcompile_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force $scratch | Out-Null
    try {
        & $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /warn:0 /main:UninstallMain `
            "/out:$scratch\Uninstall.exe" "/reference:$unRefs" $unSrc
        if ($LASTEXITCODE -ne 0) { throw "PACKAGE COMPILE FAILED: uninstaller (csc exit $LASTEXITCODE)" }

        # No /resource here: the payload is a build product, and its absence must not be able to
        # mask a source error. Setup.cs reads the resource at RUN time, not at compile time.
        & $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /warn:0 /main:SetupMain `
            "/out:$scratch\Setup.exe" "/reference:$setupRefs" $setupSrc
        if ($LASTEXITCODE -ne 0) { throw "PACKAGE COMPILE FAILED: installer (csc exit $LASTEXITCODE)" }

        Write-Host "  PASS  the package binaries still compile  [installer + uninstaller]"
    }
    finally { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue }
    return
}

# 1) uninstaller (fail fast on a native csc exit code - STD-PIPE-01)
# * Loc.cs + Ident.cs were NOT in this list, and the uninstaller STOPPED COMPILING the moment
#   Uninstall.cs gained `using ValeraScreenshot;` (bilingual work: L.S + Ident.AppId). Nobody
#   noticed, because neither build.ps1 nor test.ps1 touches the package - this script is called
#   separately, and nothing measured it. Exactly the class of rot as "proofs that no gate runs".
#   The installer was broken the same way (src\Seed.cs missing). Both are now under -CompileOnly.
$unout = Join-Path $root "setup\Uninstall.exe"
& $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /warn:0 /main:UninstallMain `
    "/out:$unout" "/reference:$unRefs" `
    "/win32icon:$ico" "/win32manifest:$setupManifest" $unSrc
if ($LASTEXITCODE -ne 0) { throw "UNINSTALL BUILD FAILED (csc exit $LASTEXITCODE)" }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "sign.ps1") -Exe $unout | Out-Null

# 2) payload (the valerascreenshot_exe resource) = signed exe + docs + cert + uninstaller
#
# THE DOC LIST LIVES HERE AND NOWHERE ELSE, and it must agree with Installer.OwnedFiles in
# src\Installer.cs and UninstallMain.OwnedFiles in setup\Uninstall.cs. Placing a file the
# uninstaller does not know about leaves litter behind AND blocks the non-recursive rmdir, so
# the install folder survives an uninstall. Checked by L24/L25 in the gate.
# * MANUAL_EN.txt and README_EN.md were MISSING here until 2026-07-29: the product had shipped a
#   full English UI while packaging only the Ukrainian manual, so an English-speaking user got an
#   English app and documentation they could not read.
$payloadDocs = @("MANUAL.txt", "MANUAL_EN.txt", "README.md", "README_EN.md")
$stage = Join-Path $root "dist\_payload"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $exe, (Join-Path $root "ValeraScreenshotCodeSign.cer"), $unout $stage
foreach ($d in $payloadDocs) { Copy-Item (Join-Path $root $d) $stage -Force }
$payload = Join-Path $root "dist\valerascreenshot_exe.zip"
if (Test-Path $payload) { Remove-Item $payload -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $payload
Remove-Item $stage -Recurse -Force

# 3) installer exe (payload embedded as the valerascreenshot_exe resource; fail fast - STD-PIPE-01)
$setup = Join-Path $root "ValeraScreenshotSetup.exe"
& $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /warn:0 /main:SetupMain `
    "/out:$setup" "/reference:$setupRefs" `
    "/resource:$payload,valerascreenshot_exe" "/win32icon:$ico" "/win32manifest:$setupManifest" `
    $setupSrc
if ($LASTEXITCODE -ne 0) { throw "SETUP BUILD FAILED (csc exit $LASTEXITCODE)" }
Remove-Item $payload -Force
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "sign.ps1") -Exe $setup | Out-Null
Write-Host ("BUILD OK -> {0} ({1:N0} bytes, signed)" -f $setup, (Get-Item $setup).Length)
