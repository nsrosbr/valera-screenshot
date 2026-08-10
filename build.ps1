# ValeraScreenshot build - uses the C# compiler built into Windows (.NET Framework 4.x).
# build.ps1          -> ValeraScreenshot.exe
# build.ps1 -All     -> + tests\Test.exe, tools\ProofGate.exe, tools\Drive.exe, tools\FieldProbe.exe
# build.ps1 -Dist    -> + dist\ValeraScreenshot-Setup-<ver>.exe (installer with embedded payload)
#                       + dist\ValeraScreenshot-<ver>-portable.zip   (all exes signed)
param([switch]$All, [switch]$Dist)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe (.NET Framework 4.x) not found" }

# STD-VER-01: app.manifest carries a version too. Bind it to Ver.Build instead of letting it rot -
# a manifest that silently disagrees with the single source of truth is a comment that lies.
$verBuild = ([regex]::Match((Get-Content (Join-Path $root "src\Ver.cs") -Raw), 'Build\s*=\s*"([^"]+)"')).Groups[1].Value
$manVer = ([regex]::Match((Get-Content (Join-Path $root "app.manifest") -Raw), 'assemblyIdentity version="([^"]+)"')).Groups[1].Value
if ($verBuild -ne $manVer) { throw "VERSION DRIFT: app.manifest says '$manVer' but src\Ver.cs Build is '$verBuild'" }

$refs = @("System.dll","System.Core.dll","System.Windows.Forms.dll","System.Drawing.dll") -join ","
$src  = Get-ChildItem -Path (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName }

# icon (generate once via tools\MakeIcon.cs)
$ico = Join-Path $root "app.ico"
if (($All) -or (-not (Test-Path $ico))) {
    $mi = Join-Path $root "tools\MakeIcon.exe"
    & $csc /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ /warn:0 `
        "/out:$mi" "/reference:System.dll,System.Drawing.dll" (Join-Path $root "tools\MakeIcon.cs")
    if ($LASTEXITCODE -ne 0) { throw "MakeIcon build failed" }
    & $mi $ico
    if ($LASTEXITCODE -ne 0) { throw "MakeIcon run failed" }
}

# embedded resources: the AUTHOR's portrait for the About dialog (light + dark variants).
# This product carries the author's personal brand only - no organisation appears anywhere in it.
$res = @()
$authorPhoto = Join-Path $root "data\author.png"
if (Test-Path $authorPhoto) { $res += "/resource:$authorPhoto,authorphoto" }
$authorPhotoDark = Join-Path $root "data\author_dark.png"
if (Test-Path $authorPhotoDark) { $res += "/resource:$authorPhotoDark,authorphoto_dark" }

# main app
$out = Join-Path $root "ValeraScreenshot.exe"
$manifest = Join-Path $root "app.manifest"
& $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /warn:0 `
    "/out:$out" "/reference:$refs" "/win32icon:$ico" "/win32manifest:$manifest" $res $src
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED (exit $LASTEXITCODE)"; exit 1 }
Write-Host "BUILD OK ->" $out
Write-Host ("Size: {0:N0} bytes" -f (Get-Item $out).Length)

if ($All) {
    # STD-GATE-06: the gate compiles the SAME sources and the SAME /resource: set as build.ps1.
    # A subset build would let the matrices measure a mock instead of the shipping code - Diag and Updater
    # carry the crown guarantees, so they must be the real ones under test.
    $tout = Join-Path $root "tests\Test.exe"
    & $csc /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ /warn:0 /main:TestMain `
        "/out:$tout" "/reference:$refs" $res $src (Join-Path $root "tests\TestMain.cs")
    if ($LASTEXITCODE -ne 0) { throw "TEST BUILD FAILED (csc exit $LASTEXITCODE)" }
    # tests\corpus.tsv already sits next to Test.exe - the gate reads it from its own folder (STD-GATE-10)
    Write-Host "BUILD OK ->" $tout

    # ProofGate: renders every visual surface from a REAL screen capture AND MEASURES it (WCAG
    # contrast, theme-family of every control background, light != dark). It replaces ShotTest and
    # ThemeProof, which were built here but never RUN by any gate - which is how two byte-identical
    # "light" and "dark" proofs sat on disk unnoticed while the light one showed dark cards.
    # setup\Setup.cs joins the compile so the INSTALLER can be rendered and measured like every
    # other surface (STD-UI-07). It was the one window nothing had ever shot - and it was broken:
    # Setup never called Theme.Init, so the first window a person sees ignored dark mode and High
    # Contrast alike. Its Installer class is in the GLOBAL namespace while src\Installer.cs is in
    # ValeraScreenshot, so the two coexist; the extra references are what Setup.cs needs on top.
    $pgRefs = $refs + ",System.IO.Compression.dll,Microsoft.CSharp.dll"
    $pgout = Join-Path $root "tools\ProofGate.exe"
    & $csc /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ /warn:0 /main:ProofGate `
        "/out:$pgout" "/reference:$pgRefs" $res $src (Join-Path $root "tools\ProofGate.cs") `
        (Join-Path $root "setup\Setup.cs")
    if ($LASTEXITCODE -ne 0) { throw "PROOFGATE BUILD FAILED (csc exit $LASTEXITCODE)" }
    Write-Host "BUILD OK ->" $pgout

    # Drive: runs the REAL app and drives it with REAL input. The layer the gate never had - every
    # earlier assertion was a pure function or a direct file/registry call, so nothing could see a
    # feature that compiles but does not work.
    $dout = Join-Path $root "tools\Drive.exe"
    & $csc /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ /warn:0 /main:Drive `
        "/out:$dout" "/reference:$refs" $res $src (Join-Path $root "tools\Drive.cs")
    if ($LASTEXITCODE -ne 0) { throw "DRIVE BUILD FAILED (csc exit $LASTEXITCODE)" }
    Write-Host "BUILD OK ->" $dout

    # FieldProbe: snapshots the machine around each install/uninstall path and proves the two are
    # symmetric. Seven ways in, three ways out, written at different times - nothing ever compared
    # them, so they drifted (a path that made a shortcut vs one that never did, an uninstall that
    # left the diagnostic log on disk forever).
    $fpout = Join-Path $root "tools\FieldProbe.exe"
    & $csc /nologo /codepage:65001 /target:exe /platform:anycpu /optimize+ /warn:0 /main:FieldProbe `
        "/out:$fpout" "/reference:$refs" $res $src (Join-Path $root "tools\FieldProbe.cs")
    if ($LASTEXITCODE -ne 0) { throw "FIELDPROBE BUILD FAILED (csc exit $LASTEXITCODE)" }
    Write-Host "BUILD OK ->" $fpout
}

if ($Dist) {
    $ver = ([regex]'Number\s*=\s*"([^"]+)"').Match((Get-Content (Join-Path $root "src\Ver.cs") -Raw)).Groups[1].Value
    if (-not $ver) { throw "cannot read version from src\Ver.cs" }
    # NB: not "$dist" - PS variables are case-insensitive and would clash with [switch]$Dist
    $distDir = Join-Path $root "dist"
    New-Item -ItemType Directory -Force $distDir | Out-Null
    $setupManifest = Join-Path $root "setup\setup.manifest"

    # 1) uninstaller
    # STD-IDENT-01: Ident.cs is compiled into the uninstaller too. It used to hardcode the app
    # name, the .lnk name and both registry keys in its own string literals - a third copy of the
    # identity that drifts on the first change.
    $unout = Join-Path $root "setup\Uninstall.exe"
    & $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /warn:0 `
        "/out:$unout" "/reference:System.dll,System.Windows.Forms.dll,System.Drawing.dll" `
        "/win32icon:$ico" "/win32manifest:$setupManifest" `
        (Join-Path $root "setup\Uninstall.cs") (Join-Path $root "src\Ident.cs") `
        (Join-Path $root "src\Loc.cs")
    if ($LASTEXITCODE -ne 0) { Write-Host "UNINSTALL BUILD FAILED"; exit 1 }
    Write-Host "BUILD OK ->" $unout

    # 2) sign app + uninstaller (before packing)
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "sign.ps1") -Exe $out | Out-Null
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "sign.ps1") -Exe $unout | Out-Null
    Write-Host "SIGNED: ValeraScreenshot.exe, Uninstall.exe"

    # 3) payload for the installer
    $stage = Join-Path $distDir "_payload"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    # Both languages ship in the package: the UI is bilingual, so a Ukrainian-only manual
    # would be an English user's first broken promise.
    Copy-Item $out, (Join-Path $root "MANUAL.txt"), (Join-Path $root "MANUAL_EN.txt"), `
        (Join-Path $root "README.md"), (Join-Path $root "README_EN.md"), (Join-Path $root "LICENSE"), `
        (Join-Path $root "ValeraScreenshotCodeSign.cer"), $unout $stage
    $payload = Join-Path $distDir "payload.zip"
    if (Test-Path $payload) { Remove-Item $payload -Force }
    Compress-Archive -Path "$stage\*" -DestinationPath $payload
    Remove-Item $stage -Recurse -Force

    # 4) installer exe (payload embedded as a resource)
    $setup = Join-Path $distDir ("ValeraScreenshot-Setup-" + $ver + ".exe")
    & $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /warn:0 /main:SetupMain `
        "/out:$setup" `
        "/reference:System.dll,System.Core.dll,System.Windows.Forms.dll,System.Drawing.dll,System.IO.Compression.dll,Microsoft.CSharp.dll" `
        "/resource:$payload,valerascreenshot_exe" "/win32icon:$ico" "/win32manifest:$setupManifest" `
        (Join-Path $root "setup\Setup.cs") (Join-Path $root "src\Ui.cs") (Join-Path $root "src\Ver.cs") `
        (Join-Path $root "src\Loc.cs") (Join-Path $root "src\Ident.cs") (Join-Path $root "src\Arp.cs") `
        (Join-Path $root "src\Seed.cs")
    if ($LASTEXITCODE -ne 0) { Write-Host "SETUP BUILD FAILED"; exit 1 }
    Remove-Item $payload -Force
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "sign.ps1") -Exe $setup | Out-Null
    Write-Host "BUILD OK ->" $setup ("({0:N0} bytes, signed)" -f (Get-Item $setup).Length)

    # 5) NO PORTABLE PACKAGE.
    # Owner's decision 2026-07-29: the portable build is discontinued. It doubled every packaging
    # path (two artifact names, two doc sets, two install stories) for a mode almost nobody used,
    # and the update channel cannot reach it anyway - a portable copy has no installed location to
    # replace. The portable.txt MARKER stays: it is what keeps a dev build and the Drive sandbox
    # from ever self-installing, and that is a safety mechanism, not a product.
}
