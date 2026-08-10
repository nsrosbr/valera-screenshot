# test.ps1 - THE build+test gate (STD-GATE-01). Prints a result and exits non-zero on any failure,
# so release.ps1 can abort before packaging. ASCII-only (PS 5.1 reads .ps1 as ANSI).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Stop a running instance so the exe is not locked during rebuild - but ONLY one launched from
# THIS folder.
#
# * IT USED TO KILL EVERY PROCESS WITH THAT NAME, and that is not a detail. The owner has the
#   product INSTALLED on this machine (per-user, under %LOCALAPPDATA%\Programs) and uses it while
#   the gate runs. Matching purely by process name meant every single gate run reached across and
#   killed the installed copy: a tray app with global hotkeys silently vanished dozens of times a
#   day, looking for all the world like a defect in the product. A test harness that damages the
#   thing it is testing produces bug reports about itself.
$mine = @()
foreach ($p in @(Get-Process ValeraScreenshot -ErrorAction SilentlyContinue)) {
    $path = $null
    try { $path = $p.Path } catch { }   # a process we may not query is, by definition, not ours
    if ($path -and $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $mine += $p }
}
if ($mine.Count) {
    Write-Host ("GATE: stopping {0} instance(s) started from this folder (installed copies untouched)" -f $mine.Count)
    $mine | Stop-Process -Force
}
Start-Sleep -Milliseconds 500

& (Join-Path $root "build.ps1") -All
if ($LASTEXITCODE -ne 0) { Write-Host "GATE: BUILD FAILED"; exit 1 }

# The PACKAGE compiler is a gate layer, not a release step. build.ps1 above builds the APP and the
# tools; the installer and uninstaller are compiled by build_setup.ps1, which nothing ever ran
# outside a release. On 2026-07-29 BOTH of them turned out not to compile at all - the uninstaller
# since Uninstall.cs gained `using ValeraScreenshot;` (bilingual work) without Loc.cs/Ident.cs being
# added to its source list, the installer since Setup.cs started calling Seed. A release would have
# died on its first command, months after the breaking edit. -CompileOnly writes nothing into the
# tree and needs no signed exe, so it belongs HERE, where every run pays four seconds for it.
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build_setup.ps1") -CompileOnly
if ($LASTEXITCODE -ne 0) { Write-Host "GATE: PACKAGE COMPILE FAILED"; exit 1 }

& (Join-Path $root "tests\Test.exe")
if ($LASTEXITCODE -ne 0) { Write-Host "GATE: TESTS FAILED"; exit 1 }

# Drive.exe runs the REAL app and drives it with REAL input. It is part of the GATE, not a tool
# sitting beside it. ShotTest, ThemeProof and UpdaterProbe were all written as "proof" and none of
# them was ever executed by a gate, which is how a green 51/51 shipped with a broken autostart
# checkbox. All three are now GONE: the first two were absorbed by ProofGate, and UpdaterProbe was
# deleted on 2026-07-29 once its two file-based checks moved into the UPDATER matrix as U11/U12.
# It had rotted exactly as an unrun artefact does - hardcoded folder, a reference to a binary that
# no longer existed, and anti-rollback arithmetic comparing the current version against itself.
#
# THIS IS THE ONE LAYER THAT OWNS THE SCREEN, and it cannot be otherwise: global hotkeys
# (RegisterHotKey) only fire for input injected into the INPUT desktop, so SendInput must go to
# the desktop the user is looking at. Budget ~90 seconds of a busy screen. Run it when the machine
# is free - and it only runs when someone ASKS (see the opt-in note below). The skip is PRINTED,
# because a criterion that can vanish silently is not a criterion (STD-GATE-10).
# ProofGate is the VISUAL half of the gate. Same lesson as Drive, one layer up: the render proofs
# were produced by tools nothing ever ran, so a "light" proof that actually showed the DARK theme -
# and was byte-identical to the dark one - sat on disk unnoticed. Every proof is now regenerated
# from scratch and MEASURED (WCAG contrast, theme family of every control background, title bar,
# light != dark). The owner's eye stays the final check (STD-PROOF-01).
#
# It has its OWN switch, separate from Drive. mutate.ps1 skips Drive for SPEED (minutes per run),
# and if the visual gate rode on the same switch, every theme mutation would be reported SURVIVED
# for the wrong reason. ProofGate costs seconds, so it runs in mutation passes too.
#
# ProofGate does NOT take over the screen. Windows are created OFF the visible area and asked to
# draw themselves via PrintWindow - nothing is raised over the user's work, nothing is activated,
# the mouse is not moved. It still needs a logged-in desktop session (WinForms + DWM), which CI
# does not have: VALERASCREENSHOT_SKIP_PROOF is for that, and it PRINTS the skip.
if ($env:VALERASCREENSHOT_SKIP_PROOF -eq "1") {
    Write-Host "GATE: PROOF GATE SKIPPED (VALERASCREENSHOT_SKIP_PROOF=1) - VISUAL promises are UNVERIFIED in this run"
} else {
    & (Join-Path $root "tools\ProofGate.exe")
    if ($LASTEXITCODE -ne 0) { Write-Host "GATE: PROOF GATE FAILED"; exit 1 }
}

# *** TAKING THE SCREEN IS NOW OPT-IN, AND THE DEFAULT SIDE WAS CHANGED ON PURPOSE (2026-07-29).
#   It used to be opt-OUT: Drive ran unless VALERASCREENSHOT_SKIP_DRIVE=1 was remembered. That put
#   the burden of not seizing the owner's desktop on whoever typed the command, and the burden was
#   dropped - a single call of this script without the variable was one keystroke away from taking
#   the machine while the owner was working on it. A default whose safe side depends on memory is
#   not a safe default. Now the screen is taken ONLY when someone explicitly says so, and the
#   skip is still PRINTED loudly, because a criterion that vanishes silently is not a criterion
#   (STD-GATE-10). The old variable is still honoured so nothing that sets it starts running Drive.
$runDrive = ($env:VALERASCREENSHOT_RUN_DRIVE -eq "1") -and ($env:VALERASCREENSHOT_SKIP_DRIVE -ne "1")
if (-not $runDrive) {
    Write-Host "GATE: DRIVE SKIPPED (opt-in: set VALERASCREENSHOT_RUN_DRIVE=1) - UI promises are UNVERIFIED in this run"
} else {
    Write-Host "GATE: DRIVE RUNNING - it OWNS the screen and injects real input for ~90 seconds."
    & (Join-Path $root "tools\Drive.exe")
    if ($LASTEXITCODE -ne 0) { Write-Host "GATE: DRIVE FAILED"; exit 1 }

    # FieldProbe installs and uninstalls for real, then proves the two are symmetric and that the
    # host machine is left exactly as it was found. It restores the owner's shortcuts and Run key
    # itself - an earlier version did not, and the first run wiped them.
    #
    # Exit 3 means REFUSED, not failed: the probe found a real installation in the very folder it
    # would install into, and it does not restore that folder's contents (the exe and settings.ini
    # next to it), so running would take the owner's hotkeys and autostart with it. That is a
    # machine that cannot host this check - NOT a defect in the product. Conflating the two would
    # paint the gate red on a healthy build, and a red that means nothing is how a gate dies.
    # The skip is PRINTED and names what is now unverified (STD-GATE-10).
    & (Join-Path $root "tools\FieldProbe.exe")
    $fp = $LASTEXITCODE
    if ($fp -eq 3) {
        Write-Host "GATE: FIELD PROBE REFUSED - the product is installed on this machine, so install/uninstall symmetry is UNVERIFIED in this run"
    } elseif ($fp -ne 0) { Write-Host "GATE: FIELD PROBE FAILED"; exit 1 }
}

# WHICH LAYERS ACTUALLY RAN, on the record. release.ps1 is a FROZEN studio file - byte-identical
# across every app in the studio, and not ours to edit (conform G4 is the canary, and it caught the
# attempt). It calls this gate and then package.ps1, judging only the exit code. Since Drive became
# opt-in, a release would therefore ship with the UI promises UNVERIFIED, announced by one printed
# line in a long log. So the gate records what it did, and package.ps1 REFUSES to build a release
# off a run that skipped the live layer. The lock goes where we are allowed to put it.
$layers = @(
    ("drive=" + $(if ($runDrive) { "1" } else { "0" })),
    ("proof=" + $(if ($env:VALERASCREENSHOT_SKIP_PROOF -eq "1") { "0" } else { "1" })),
    ("utc=" + (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
) -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $root "_gate_layers.txt"), $layers + "`r`n",
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host "GATE: ALL TESTS PASSED"
exit 0
