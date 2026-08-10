# verify.ps1 - THE single verification command. ASCII-only (STD-ENC-01).
#
# WHY THIS EXISTS. A full check used to mean remembering to run three scripts in the right order
# (test.ps1, .standard\conform.ps1, tools\mutate.ps1) and reading three separate outputs. That is a
# PROCEDURE, and this line already learned what procedures are worth: CLAUDE.md called the test gate
# mandatory while nothing called test.ps1, so every release could sail past it (STD-GATE-01).
# A check nobody is forced to run is a hope, not a gate. One command, one verdict, one exit code.
#
# Ported from the studio reference (Valera) after the owner pointed at it on 2026-07-29. The shape
# is the same on purpose - a second, different verification story in the same studio would be the
# same class of drift this file exists to catch.
#
# LEVELS (pick by what you are about to do):
#   .\verify.ps1              secrets + conformance + gate        ~3 min   before any commit
#   .\verify.ps1 -Full        + the whole mutation catalogue      ~25 min  before a release
#   .\verify.ps1 -Quick       conformance only                    ~5 s     doc-only changes
#
# NOTHING HERE TAKES THE SCREEN UNLESS YOU ASK. The owner works on this machine, and a
# verification run has no right to seize the desktop. Only the live layer (Drive.exe) does that -
# it injects real mouse and keyboard input, so it must own the input desktop - and it now runs
# ONLY with -WithDrive:
#   .\verify.ps1 -WithDrive   adds the live UI layer; budget ~90 seconds of a busy screen
# The arrangement used to be the other way round (Drive on, a variable to turn it off), and that
# put "do not seize the owner's desktop" on someone's memory instead of on the default. It failed
# exactly as you would expect. The skip is still PRINTED every run (STD-GATE-10).
#
# ProofGate is NOT affected: it draws windows OFF the visible area via PrintWindow, activates
# nothing and never moves the mouse, so it runs by default. -NoScreen turns it off too, and exists
# for CI, which has no desktop session at all.
#
# Exit code is 0 only when EVERY level that ran was green.
param(
    [switch]$Full,
    [switch]$Quick,
    [switch]$NoScreen,
    [switch]$WithDrive
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fail = @()
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Section([string]$name) {
    Write-Host ""
    Write-Host ("=== " + $name + " ===")
}

# ---------------------------------------------------------------- automation wiring (always)
# The pre-commit hook lives in .githooks\ so it travels with the folder, but git only looks there
# after core.hooksPath is set - a per-clone local setting. Unset, the automation is silently OFF and
# every "the hook will catch it" assumption becomes false. Silent absence is the failure mode this
# whole file exists to end, so it is REPORTED, never assumed.
Section "AUTOMATION"
$hasGit = Test-Path (Join-Path $root ".git")
if (-not $hasGit) {
    Write-Host "  no .git yet - the hook cannot be wired, and the secret probes below cannot run."
    Write-Host "  This is a STATE, not a pass: after git init, run verify again before any push."
} else {
    $hp = ""
    try { $hp = (& git -C $root config core.hooksPath) } catch { }
    if ($hp -eq ".githooks") {
        Write-Host "  pre-commit hook wired (core.hooksPath = .githooks)"
    } else {
        Write-Host "  WARNING: pre-commit hook NOT wired - nothing stops a red commit."
        Write-Host "  Fix (once per machine):  git config core.hooksPath .githooks"
        $fail += "AUTOMATION"
    }
}

# ---------------------------------------------------------------- secrets (always, instant)
# The cheapest and most valuable check here: a crown-jewel secret becoming committable is silent
# right up to the moment it is public. STD-SEC-02 demands the PROBE, not the .gitignore rule - a
# rule that LOOKS right and does not work is exactly the defect that has shipped in this studio.
Section "SECRETS"
$secrets = @("_ghtoken.txt", "_codesign.pfx", "_codesign_pwd.txt")
if (-not $hasGit) {
    $present = @()
    foreach ($s in $secrets) { if (Test-Path (Join-Path $root $s)) { $present += $s } }
    if ($present.Count) {
        Write-Host ("  present on disk, UNVERIFIED without git: " + ($present -join ", "))
    } else {
        Write-Host "  none of the three secret files exists in this folder"
    }
} else {
    $leaky = @()
    foreach ($s in $secrets) {
        if (-not (Test-Path (Join-Path $root $s))) { continue }   # absent here: nothing to leak
        & git -C $root check-ignore -q $s 2>$null
        if ($LASTEXITCODE -ne 0) { $leaky += $s }
    }
    # Tracked is worse than un-ignored: it means the secret is already IN history.
    # Plain ls-files on purpose - --error-unmatch reports a miss on STDERR, and PS 5.1 wraps a native
    # command's stderr into an ErrorRecord, which under ErrorActionPreference=Stop turns the normal
    # "not tracked" answer into a crash. The guard would then fail LOUDLY on the healthy case.
    $tracked = @()
    foreach ($s in $secrets) {
        $o = & git -C $root ls-files -- $s
        if ($o) { $tracked += $s }
    }
    if ($leaky.Count -or $tracked.Count) {
        if ($leaky.Count)   { Write-Host ("  NOT IGNORED   : " + ($leaky -join ", ")) }
        if ($tracked.Count) { Write-Host ("  TRACKED IN GIT: " + ($tracked -join ", ")) }
        $fail += "SECRETS"
    } else {
        Write-Host "  every present secret is ignored and untracked"
    }
}

# ---------------------------------------------------------------- conformance (always)
Section "CONFORMANCE"
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root ".standard\conform.ps1") -Root $root
if ($LASTEXITCODE -ne 0) { $fail += "CONFORM" }

# ---------------------------------------------------------------- gate (unless -Quick)
# The gate also compiles the PACKAGE now (build_setup.ps1 -CompileOnly, inside test.ps1). It is not
# a separate level here on purpose: running it twice per verify would cost double for one answer.
if (-not $Quick) {
    Section "GATE"
    # -WithDrive is the ONLY way the live layer takes the desktop, and it exists because the
    # opposite arrangement failed in practice: with Drive on by default, not seizing the owner's
    # screen depended on remembering a variable, and one call without it was a keystroke away from
    # taking the machine mid-work. ProofGate is unaffected - it draws OFF the visible area and
    # never activates a window, so it keeps running by default; -NoScreen exists for CI, which has
    # no desktop session at all. Both skips are PRINTED (STD-GATE-10).
    if ($WithDrive) {
        $env:VALERASCREENSHOT_RUN_DRIVE = "1"
        Write-Host "  -WithDrive: the live layer WILL take the screen for ~90 seconds."
    }
    if ($NoScreen) {
        $env:VALERASCREENSHOT_RUN_DRIVE = $null
        $env:VALERASCREENSHOT_SKIP_PROOF = "1"
        Write-Host "  -NoScreen: the live run and the visual proofs are SKIPPED in this verdict."
    }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "test.ps1")
    if ($LASTEXITCODE -ne 0) { $fail += "GATE" }
    $env:VALERASCREENSHOT_RUN_DRIVE = $null
    $env:VALERASCREENSHOT_SKIP_PROOF = $null
}

# ---------------------------------------------------------------- mutations (only -Full)
# Not run by default ON PURPOSE: the catalogue rebuilds the whole test binary once per mutation, so
# it costs minutes, and a check that makes every commit painful is a check people learn to skip.
if ($Full) {
    Section "MUTATIONS"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "tools\mutate.ps1")
    if ($LASTEXITCODE -ne 0) { $fail += "MUTATIONS" }
}

# ---------------------------------------------------------------- verdict
$sw.Stop()
Write-Host ""
Write-Host "============================================================"
if ($fail.Count -eq 0) {
    $lvl = "secrets + conformance + gate (incl. package)"
    if ($Quick) { $lvl = "conformance only" }
    if ($Full)  { $lvl = "secrets + conformance + gate (incl. package) + mutations" }
    if ($NoScreen) { $lvl = $lvl + ", WITHOUT the screen layers" }
    Write-Host ("VERIFY OK  (" + $lvl + ", " + [int]$sw.Elapsed.TotalSeconds + "s)")
    if (-not $Full) { Write-Host "Before a release or after touching a CROWN guarantee: .\verify.ps1 -Full" }
    exit 0
}
Write-Host ("VERIFY FAILED: " + ($fail -join ", "))
Write-Host "Nothing is committed or released off a red verdict. Fix the cause, do not lower the bar."
exit 1
