# conform.ps1 - THE CONFORMANCE GATE for the VALERA studio standard.
# ASCII-only on purpose (PS 5.1 reads .ps1 as ANSI - STD-ENC-01). Prints a NUMBER and exits
# non-zero, in the same idiom as test.ps1, because the owner's credo (STANDARD.md sec.16) accepts
# "done" only with a measurable proof.
#
# Runs entirely offline: file reads + hashes. No network, no studio, no git required (git-based
# checks self-skip when there is no repo). This is what keeps the folder portable.
#
# Reads standard.bind.json and SKIPS declared deviations - that is the mechanism which stops
# "one studio" from degrading into "make everything look like Valera" (STANDARD.md sec.18.3).
#
# Usage:  .\conform.ps1  [-Root <path>]  [-Verbose]
param(
    [string]$Root = (Split-Path -Parent $MyInvocation.MyCommand.Path),
    [switch]$ShowPassed
)
$ErrorActionPreference = "Continue"

# ---------------------------------------------------------------- infrastructure
$script:Results = @()
$script:Bind = $null
$script:Skips = @{}

function Read-Utf8([string]$p) { return [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8) }
function Bytes([string]$p)     { return [System.IO.File]::ReadAllBytes($p) }
function NonAscii([string]$p)  { return @((Bytes $p) | Where-Object { $_ -gt 127 }).Count }
function HasBom([string]$p) {
    $b = Bytes $p
    return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}
function Eols([string]$p) {
    $b = Bytes $p; $lf = 0; $crlf = 0
    for ($i = 0; $i -lt $b.Length; $i++) {
        if ($b[$i] -eq 0x0A) { if ($i -gt 0 -and $b[$i-1] -eq 0x0D) { $crlf++ } else { $lf++ } }
    }
    return @{ Lf = $lf; Crlf = $crlf }
}
function P([string]$rel) { return (Join-Path $Root $rel) }

# A check records: id, group, title, ok, detail, fixable
function Check([string]$id, [string]$group, [string]$title, [bool]$ok, [string]$detail, [bool]$fixable) {
    $state = "PASS"
    if (-not $ok) { $state = "FAIL" }
    if ($script:Skips.ContainsKey($id)) { $state = "SKIP" }
    $script:Results += [pscustomobject]@{
        Id = $id; Group = $group; Title = $title; State = $state; Detail = $detail; Fixable = $fixable
    }
}
function Skip([string]$id, [string]$group, [string]$title, [string]$why) {
    $script:Results += [pscustomobject]@{
        Id = $id; Group = $group; Title = $title; State = "N/A"; Detail = $why; Fixable = $false
    }
}

# ---------------------------------------------------------------- bind file
$bindPath = P "standard.bind.json"
if (-not (Test-Path $bindPath)) {
    Write-Host "FATAL: standard.bind.json not found in $Root"
    Write-Host "This project is not bound to the studio standard. See STANDARD.md sec.17 step 6."
    exit 2
}
try {
    # STD-ENC-09: explicit decoder, never Import-PowerShellDataFile.
    $script:Bind = Read-Utf8 $bindPath | ConvertFrom-Json
} catch {
    Write-Host "FATAL: standard.bind.json does not parse: $($_.Exception.Message)"
    exit 2
}
$APP = $script:Bind.Identity.AppId
foreach ($d in $script:Bind.Deviations) { if ($d.Check) { $script:Skips[$d.Check] = $d } }

$idsPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "known_identities.json"
if (-not (Test-Path $idsPath)) { $idsPath = P ".standard\known_identities.json" }
$KnownIds = $null
if (Test-Path $idsPath) { $KnownIds = (Read-Utf8 $idsPath | ConvertFrom-Json).apps }

$hasGit = Test-Path (P ".git")

# stdlib.ps1 carries the identity normalizer shared with rebuild.ps1 - the drift checks [G1]/[G2]
# need it. Located next to this script (studio source) or in the vendored .standard\. Absent -> the
# G-checks self-skip, so the checker still runs everywhere the seal has not been vendored yet.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$stdlibPath = Join-Path $ScriptDir "stdlib.ps1"
if (-not (Test-Path $stdlibPath)) { $stdlibPath = P ".standard\stdlib.ps1" }
$haveStdlib = Test-Path $stdlibPath
if ($haveStdlib) { . $stdlibPath }
$frozenPath = Join-Path $ScriptDir "frozen.sha256"
if (-not (Test-Path $frozenPath)) { $frozenPath = P ".standard\frozen.sha256" }
$payloadPath = Join-Path $ScriptDir "payload.sha256"
if (-not (Test-Path $payloadPath)) { $payloadPath = P ".standard\payload.sha256" }

Write-Host ""
# The checker's own standard version comes from standard.version travelling NEXT TO this script
# (studio root or the vendored .standard\) - a hardcoded "1" here survived the v2 bump unnoticed.
$ownVerPath = Join-Path $ScriptDir "standard.version"
$ownVer = "?"
if (Test-Path $ownVerPath) { $ownVer = (Read-Utf8 $ownVerPath).Trim() }
Write-Host ("=== CONFORMANCE: {0}  (standard v{1}, bind v{2}) ===" -f $APP, $ownVer, $script:Bind.StandardVersion)
Write-Host ""

# ================================================================ [A] BYTES & ENCODING
# Exclude .git\ and .standard\: the vendored standard (checker, stdlib, and the normalized ref\
# bodies which are intentionally LF + <PLACEHOLDER>) is not project-authored source. Its integrity
# is G1's job, not [A]'s - scanning ref\*.cmd/*.ps1 as if they were project files is a category error.
$ps1 = @(Get-ChildItem $Root -Filter *.ps1 -File -Recurse -ErrorAction SilentlyContinue |
         Where-Object { $_.FullName -notmatch '\\(\.git|\.standard)\\' })
$bad = @($ps1 | Where-Object { (NonAscii $_.FullName) -gt 0 })
Check "A1" "A" "every .ps1 is pure ASCII (STD-ENC-01)" ($bad.Count -eq 0) `
    ("{0} scanned; offenders: {1}" -f $ps1.Count, (($bad | ForEach-Object { $_.Name }) -join ", ")) $false

$bom = @($ps1 | Where-Object { HasBom $_.FullName })
Check "A2" "A" ".ps1 files carry no BOM (STD-ENC-01)" ($bom.Count -eq 0) `
    (($bom | ForEach-Object { $_.Name }) -join ", ") $true

$cmds = @(Get-ChildItem $Root -Filter *.cmd -File -Recurse -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -notmatch '\\(\.git|\.standard)\\' })
$cbad = @($cmds | Where-Object { (NonAscii $_.FullName) -gt 0 })
Check "A3" "A" ".cmd files are ASCII + transliterated (STD-ENC-02)" ($cbad.Count -eq 0) `
    ("{0} scanned; offenders: {1}" -f $cmds.Count, (($cbad | ForEach-Object { $_.Name }) -join ", ")) $false

$lfOnly = @($cmds | Where-Object { $e = Eols $_.FullName; $e.Lf -gt 0 })
Check "A4" "A" ".cmd files use CRLF - cmd.exe parses by byte offset (STD-ENC-02)" ($lfOnly.Count -eq 0) `
    ("LF-only: " + (($lfOnly | ForEach-Object { $_.Name }) -join ", ")) $true

$manuals = @(Get-ChildItem (P ("dist\" + $APP)) -Filter *.txt -File -ErrorAction SilentlyContinue)
$mbad = @($manuals | Where-Object { -not (HasBom $_.FullName) -or (Eols $_.FullName).Lf -gt 0 })
Check "A5" "A" "shipped manuals are UTF-8 BOM + CRLF (STD-ENC-03)" ($mbad.Count -eq 0) `
    ("{0} scanned; offenders: {1}" -f $manuals.Count, (($mbad | ForEach-Object { $_.Name }) -join ", ")) $true

$gaPath = P ".gitattributes"
$gaOk = $false; $gaDetail = ".gitattributes missing"
if (Test-Path $gaPath) {
    $gaHasText = ((Read-Utf8 $gaPath) -match '(?m)^\s*\*\s+-text\s*$')
    $autocrlf = "unset"
    if ($hasGit) { $autocrlf = (& git -C $Root config --get core.autocrlf) }
    $gaOk = ($gaHasText -and ($autocrlf -eq "false" -or -not $hasGit))
    $gaDetail = ("'* -text' present={0}; core.autocrlf={1}" -f $gaHasText, $autocrlf)
}
Check "A6" "A" "git does not rewrite bytes: '* -text' + autocrlf=false (STD-ENC-06)" $gaOk $gaDetail $true

# ================================================================ [B] SECRETS & GITIGNORE
$giPath = P ".gitignore"
$trailing = @()
if (Test-Path $giPath) {
    $n = 0
    foreach ($ln in [System.IO.File]::ReadAllLines($giPath, [System.Text.Encoding]::UTF8)) {
        $n++
        $t = $ln.Trim()
        if ($t.Length -eq 0) { continue }
        if ($t.StartsWith("#")) { continue }
        # a '#' anywhere after the pattern start is NOT a comment - it is part of the pattern
        if ($t -match '\S\s+#') { $trailing += ("line {0}: {1}" -f $n, $t) }
    }
}
Check "B1" "B" ".gitignore has no trailing-comment patterns (STD-SEC-01)" ($trailing.Count -eq 0) `
    ($trailing -join " | ") $true

$secrets = @("_ghtoken.txt", "_codesign_pwd.txt", "_codesign.pfx")
if ($hasGit) {
    foreach ($s in $secrets) {
        $rule = & git -C $Root check-ignore -v $s
        $ok = ($LASTEXITCODE -eq 0)
        $i = "B" + (2 + [array]::IndexOf($secrets, $s))
        Check $i "B" ("secret is provably ignored: {0} (STD-SEC-02)" -f $s) $ok `
            $(if ($ok) { $rule } else { "NOT IGNORED - git would commit this" }) $true
    }
    $tracked = @(& git -C $Root ls-files | Where-Object { $_ -match '_ghtoken|_codesign_pwd|\.pfx$' })
    Check "B5" "B" "no secret is tracked by git (STD-SEC-03)" ($tracked.Count -eq 0) ($tracked -join ", ") $false
    & git -C $Root check-ignore "release/x" | Out-Null
    $relIgnored = ($LASTEXITCODE -eq 0)
    Check "B6" "B" "build artifacts are ignored (STD-TREE-03)" $relIgnored "release/" $true
} else {
    foreach ($i in @("B2","B3","B4","B5","B6")) { Skip $i "B" "git-dependent secret probe" "no .git in this folder - probe cannot run" }
}

# ================================================================ [C] IDENTITY
$srcFiles = @(Get-ChildItem (P "src") -Filter *.cs -File -ErrorAction SilentlyContinue)
$sweepRoots = @("src", "setup", "tools")
$foreign = @()
if ($KnownIds) {
    $mine = $script:Bind.Identity.AppId
    foreach ($other in ($KnownIds | Where-Object { $_.AppId -ne $mine })) {
        $tokens = @($other.DisplayName, $other.Mutex, $other.ResourceId) | Where-Object { $_ }
        foreach ($dir in $sweepRoots) {
            $dp = P $dir
            if (-not (Test-Path $dp)) { continue }
            foreach ($f in (Get-ChildItem $dp -File -Recurse -Include *.cs, *.ps1)) {
                $txt = Read-Utf8 $f.FullName
                # A RETIRED identity is legitimately named by the migration path - that is the
                # migration's whole job (invariant #1). Skip any file that is part of it:
                # Migrate.cs itself, its call site in App.cs, and the comment in Loc.cs that
                # explains the identity reset. Sweeping those produced false FAILs. The live
                # signal for retired-identity rot is the doc title (H3), not the source.
                if ($other.Retired -and ($f.Name -eq "Migrate.cs" -or $txt.Contains("Migrate"))) { continue }
                foreach ($t in $tokens) {
                    if ($txt.Contains($t)) { $foreign += ("{0}: '{1}' (belongs to {2})" -f $f.Name, $t, $other.AppId) }
                }
            }
        }
    }
}
Check "C1" "C" "no foreign app's identity token in the tree (STD-IDENT-04)" ($foreign.Count -eq 0) `
    ($foreign -join " | ") $true

$myMutexHex = $script:Bind.Identity.MutexHex
$clash = @()
if ($KnownIds) {
    foreach ($o in ($KnownIds | Where-Object { $_.AppId -ne $APP -and $_.Mutex })) {
        if ($o.Mutex -match '\{([0-9A-Fa-f]+)\}' -and $Matches[1] -eq $myMutexHex) { $clash += $o.AppId }
    }
}
Check "C2" "C" "mutex hex is unique across the studio (STD-IDENT-03)" ($clash.Count -eq 0) `
    ("{0} vs {1}" -f $myMutexHex, ($clash -join ", ")) $false

# C3 (semantics fixed 2026-07-21): the mutex literal must live in EXACTLY ONE identity source.
# Ident.cs is the constants registry (Appendix E) and the preferred home; App.cs references
# Ident.Mutex. The old check demanded the literal IN App.cs - so when Valera removed the App.cs
# duplicate (the very duplicate-constant defect Ident.cs exists to kill, STD-IDENT-01), the fix
# turned C3 red and LiteScribe had to DECLARE A DEVIATION for doing it right from day one. A check
# that goes red on the correct layout is a wrong check - the projects led the checker (sec.22.2).
# Two hits = the duplicate is back; zero = the bind value is not pinned anywhere.
$appCs = P "src\App.cs"   # also used by the [E] composition checks below
$want = "{0}_SingleInstance_{{{1}}}" -f $APP, $myMutexHex
$mutexHits = @()
foreach ($cand in @("src\Ident.cs", "src\App.cs")) {
    $fp = P $cand
    if ((Test-Path $fp) -and (Read-Utf8 $fp).Contains($want)) { $mutexHits += $cand }
}
$mutexOk = ($mutexHits.Count -eq 1)
if ($mutexHits.Count -eq 0)     { $mutexDetail = "literal '$want' found in NEITHER src\Ident.cs nor src\App.cs" }
elseif ($mutexHits.Count -gt 1) { $mutexDetail = "DUPLICATE: literal in both {0} - one must reference the other (STD-IDENT-01)" -f ($mutexHits -join " and ") }
else                            { $mutexDetail = "single source: {0}" -f $mutexHits[0] }
Check "C3" "C" "mutex literal lives in exactly one identity source (STD-IDENT-03)" $mutexOk $mutexDetail $true

$bsPath = P "build_setup.ps1"; $setupCs = P "setup\Setup.cs"
$resOk = $false; $resDetail = "build_setup.ps1 or setup\Setup.cs missing"
if ((Test-Path $bsPath) -and (Test-Path $setupCs)) {
    $rid = $script:Bind.Identity.ResourceId
    $inBuild = (Read-Utf8 $bsPath).Contains($rid)
    $inSetup = (Read-Utf8 $setupCs).Contains($rid)
    $resOk = ($inBuild -and $inSetup)
    $resDetail = ("'{0}' in build_setup.ps1={1}, in Setup.cs={2}" -f $rid, $inBuild, $inSetup)
}
Check "C4" "C" "embedded resource id matches what the installer reads (STD-IDENT-05)" $resOk $resDetail $true

$identCs = P "src\Ident.cs"
$identOk = Test-Path $identCs
$identDetail = "src\Ident.cs is the single source of AppName/DisplayName/UninstallKey/RunKey/ResourceId"
if (-not $identOk) {
    $identDetail = "src\Ident.cs ABSENT - identity constants are hand-copied into files that never see each other. This is the root cause of the ValeraZSU three-descriptor bug (Installer.cs registering the WRONG product)."
}
Check "C5" "C" "src\Ident.cs exists and is the single identity source (STD-IDENT-01)" $identOk $identDetail $true

Check "C6" "C" "AppId contains no space (STD-IDENT-06)" (-not $APP.Contains(" ")) $APP $false

# ================================================================ [D] BUILD CHAIN
function HasFailFast([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    $t = Read-Utf8 $path
    if ($t -notmatch '&\s*\$csc') { return $true }   # no csc call -> nothing to guard
    return ($t -match '(?m)if\s*\(\s*\$LASTEXITCODE\s*-ne\s*0\s*\)\s*\{\s*(throw|exit)')
}
Check "D1" "D" "build.ps1 fails fast after csc (STD-PIPE-01)" (HasFailFast (P "build.ps1")) `
    "ErrorActionPreference does not catch a native exe's exit code - an explicit throw is required" $true
Check "D2" "D" "build_setup.ps1 fails fast after csc (STD-PIPE-01)" (HasFailFast (P "build_setup.ps1")) `
    "same reasoning as build.ps1" $true

$signPath = P "sign.ps1"
$signTxt = ""
if (Test-Path $signPath) { $signTxt = Read-Utf8 $signPath }
$selOk = ($signTxt -match '(?m)Where-Object\s*\{[^}]*\$_\.Thumbprint\s*-eq\s*\$Pinned' ) -and
         ($signTxt -notmatch '(?m)Where-Object\s*\{[^}]*FriendlyName\s*-eq')
Check "D3" "D" "sign.ps1 selects the cert by THUMBPRINT ONLY (STD-SIGN-01)" $selOk `
    "a FriendlyName branch can only ever match a self-signed fake minted by the fallback - the real cert's friendly name is a pre-rename identity nothing searches for" $true

$closedOk = $false
if ($signTxt) {
    # Look for the ASSIGNMENT that actually mints a cert, not the cmdlet name - sign.ps1's own
    # header comment mentions New-SelfSignedCertificate, and matching that gave a false FAIL.
    $iThrow = $signTxt.IndexOf("REFUSING TO SIGN")
    $mint   = [regex]::Match($signTxt, '\$cert\s*=\s*New-SelfSignedCertificate')
    $iMint  = -1
    if ($mint.Success) { $iMint = $mint.Index }
    $closedOk = ($iThrow -ge 0 -and ($iMint -lt 0 -or $iThrow -lt $iMint) -and $signTxt.Contains("AllowSelfSigned"))
}
Check "D4" "D" "sign.ps1 fails CLOSED; minting needs -AllowSelfSigned (STD-SIGN-02)" $closedOk `
    "refusing to sign is always cheaper than shipping a build the whole installed base rejects" $true

$updCs = P "src\Updater.cs"
$pinOk = $false; $pinDetail = "src\Updater.cs or sign.ps1 missing"
if ((Test-Path $updCs) -and $signTxt) {
    $p1 = ([regex]::Match($signTxt, '\$Pinned\s*=\s*"([0-9A-Fa-f]+)"')).Groups[1].Value
    $p2 = ([regex]::Match((Read-Utf8 $updCs), 'TrustedThumbprint\s*=\s*"([0-9A-Fa-f]+)"')).Groups[1].Value
    $p3 = $script:Bind.Identity.Thumbprint
    $pinOk = ($p1 -and $p1 -eq $p2 -and $p1 -eq $p3)
    $pinDetail = ("sign.ps1={0} Updater.cs={1} bind={2}" -f $p1, $p2, $p3)
}
Check "D5" "D" "pinned thumbprint agrees: sign.ps1 == Updater.cs == bind (STD-UPD-02)" $pinOk $pinDetail $false

$relTxt = ""
if (Test-Path (P "release.ps1")) { $relTxt = Read-Utf8 (P "release.ps1") }
$gateOk = $false; $gateDetail = "release.ps1 missing"
if ($relTxt) {
    $g = $relTxt.IndexOf("test.ps1"); $k = $relTxt.IndexOf('"package.ps1"')
    $gateOk = ($g -ge 0 -and $k -ge 0 -and $g -lt $k -and $relTxt -match 'LASTEXITCODE\s*-ne\s*0')
    $gateDetail = ("gate@{0} package@{1}" -f $g, $k)
}
Check "D6" "D" "test.ps1 gate runs BEFORE package.ps1 and aborts on red (STD-GATE-01)" $gateOk `
    ($gateDetail + " - a procedural invariant a script bypasses is a hope, not a gate") $true

$cscFiles = @("build.ps1", "build_setup.ps1", "test.ps1")
$noCp = @()
foreach ($f in $cscFiles) {
    $fp = P $f
    if (-not (Test-Path $fp)) { continue }
    $t = Read-Utf8 $fp
    if ($t -match '&\s*\$csc' -and $t -notmatch '/codepage:65001') { $noCp += $f }
}
Check "D7" "D" "every csc call carries /codepage:65001 (STD-ENC-05)" ($noCp.Count -eq 0) `
    ("missing in: " + ($noCp -join ", ") + " - sources contain Cyrillic char literals") $true

# ================================================================ [E] UI & CODE
$nonUi = @($srcFiles | Where-Object { $_.Name -ne "Ui.cs" })
$mb = @($nonUi | Where-Object { (Read-Utf8 $_.FullName) -match 'MessageBox\.Show' })
Check "E1" "E" "no MessageBox.Show outside Ui.cs (STD-UI-01)" ($mb.Count -eq 0) `
    (($mb | ForEach-Object { $_.Name }) -join ", ") $true

$col = @($nonUi | Where-Object {
    $t = Read-Utf8 $_.FullName
    ($t -match 'Color\.FromArgb|SystemColors\.') -and $_.Name -ne "Ident.cs"
})
Check "E2" "E" "no hardcoded colour outside Ui.cs (STD-UI-02)" ($col.Count -eq 0) `
    (($col | ForEach-Object { $_.Name }) -join ", ") $true

$forms = @($srcFiles | Where-Object { (Read-Utf8 $_.FullName) -match ':\s*(ThemedForm|Form)\b' })
$noDpi = @($forms | Where-Object { (Read-Utf8 $_.FullName) -notmatch 'AutoScaleMode\s*=\s*AutoScaleMode\.Dpi' })
Check "E3" "E" "every Form sets AutoScaleMode.Dpi (STD-UI-03)" ($noDpi.Count -eq 0) `
    (($noDpi | ForEach-Object { $_.Name }) -join ", ") $true

$guardOk = $false
if (Test-Path $appCs) {
    $t = Read-Utf8 $appCs
    $guardOk = ($t -match 'SetUnhandledExceptionMode' -and $t -match 'ThreadException' -and $t -match 'UnhandledException')
}
Check "E4" "E" "crash guards installed in Main (STD-DIAG-03)" $guardOk `
    "without them a fault leaves NO trace and the TRIAGE role has nothing to read" $true

$diagCs = P "src\Diag.cs"
$lcOk = $false
if (Test-Path $diagCs) { $lcOk = (Read-Utf8 $diagCs) -match 'LogCrash' }
Check "E5" "E" "Diag.LogCrash exists - writes even when logging is off (STD-DIAG-02)" $lcOk `
    "the queue and writer thread only exist while logging is on; a crashing process may not survive to flush" $true

# AssemblyInfo.cs is attributes-only and declares no namespace by design - excluding it, not
# relaxing the rule. A checker that cries wolf gets ignored, and an ignored gate is worse than none.
$nsBad = @($srcFiles | Where-Object { $_.Name -ne "AssemblyInfo.cs" } |
           Where-Object { (Read-Utf8 $_.FullName) -notmatch ("(?m)^namespace\s+" + [regex]::Escape($APP) + "\s*$") })
Check "E6" "E" ("every src file declares namespace {0} (STD-CODE-01)" -f $APP) ($nsBad.Count -eq 0) `
    (($nsBad | ForEach-Object { $_.Name }) -join ", ") $true

# F-REG CONTRACTS. App.cs/Config.cs/Diag.cs are region-frozen, not whole-file (they diverge wildly:
# App.cs is 94 lines here, 451 in the etalon). Hashing them is the wrong tool - the frozen part is a
# CONTRACT, so it is checked as one. These turn three ACTIVE-but-unchecked MUST rules into gates
# (STD-GATE-07: an undocumented untested invariant is how the next PuntoFree happens).

# [E7] STD-LIFE-01: install/uninstall/apply-update MUST be handled before the single-instance mutex -
# apply-update runs as a second instance while the first lives; behind the mutex it would deadlock and
# no update would ever apply (the H-1 TOCTOU point). Order by text index, robust across architectures.
$lifeOk = $false; $lifeDetail = "src\App.cs not found"
if (Test-Path $appCs) {
    $t = Read-Utf8 $appCs
    $iApply = $t.IndexOf("apply-update"); $iMutex = $t.IndexOf("new Mutex")
    $lifeOk = ($iApply -ge 0 -and $iMutex -ge 0 -and $iApply -lt $iMutex)
    $lifeDetail = ("apply-update@{0} mutex@{1}" -f $iApply, $iMutex)
}
Check "E7" "E" "args (install/apply-update) handled before the mutex (STD-LIFE-01)" $lifeOk $lifeDetail $true

# [E8] STD-CFG-01: config write MUST be atomic (temp + File.Replace) so a crash mid-write cannot
# truncate settings.ini into an empty file the next tolerant read then defaults away silently.
$cfgCs = P "src\Config.cs"
$cfgAtomicOk = $false; $cfgDetail = "src\Config.cs not found"
if (Test-Path $cfgCs) {
    $t = Read-Utf8 $cfgCs
    $cfgAtomicOk = (($t -match 'File\.Replace') -and ($t -match '\.tmp'))
    $cfgDetail = ("File.Replace={0} temp={1}" -f ($t -match 'File\.Replace'), ($t -match '\.tmp'))
}
Check "E8" "E" "config write is atomic: temp + File.Replace (STD-CFG-01)" $cfgAtomicOk $cfgDetail $true

# [E9] STD-DIAG-01: the log MUST be opt-in (off by default), gated on a debug.on marker or the
# per-app *_DEBUG env var. A log that is on by default is telemetry on disk - and privacy is the crown.
$diagOptOk = $false; $diagOptDetail = "src\Diag.cs not found"
if (Test-Path $diagCs) {
    $t = Read-Utf8 $diagCs
    $diagOptOk = (($t -match 'debug\.on') -and ($t -match '_DEBUG'))
    $diagOptDetail = ("debug.on={0} env={1}" -f ($t -match 'debug\.on'), ($t -match '_DEBUG'))
}
Check "E9" "E" "diagnostics are opt-in (debug.on marker / *_DEBUG env) (STD-DIAG-01)" $diagOptOk $diagOptDetail $true

# [E10] STD-LIFE-02: the autostart Run key MUST be written from exactly one place (Installer). Any
# other file that opens CurrentVersion\Run and SetValues it is a second source of truth for one
# registry key, and the two diverge on the first change. Migrate.cs is exempt (DEV-VAL-02: it only
# DELETES the retired identity's value) as is Installer.cs itself.
$life2Bad = @()
foreach ($f in $srcFiles) {
    if ($f.Name -eq "Installer.cs" -or $f.Name -eq "Migrate.cs") { continue }
    $t = Read-Utf8 $f.FullName
    if (($t -match 'CurrentVersion\\Run') -and ($t -match 'SetValue')) { $life2Bad += $f.Name }
}
Check "E10" "E" "autostart Run key written only from Installer (STD-LIFE-02)" ($life2Bad.Count -eq 0) `
    ((($life2Bad -join ", ")) + " also writes the Run key - two truths about one key") $true

# ================================================================ [F] VERSION & RELEASE
$verCs = P "src\Ver.cs"
$vNum = ""; $vBuild = ""; $vDate = ""
if (Test-Path $verCs) {
    $t = Read-Utf8 $verCs
    $vNum   = ([regex]::Match($t, 'Number\s*=\s*"([^"]+)"')).Groups[1].Value
    $vBuild = ([regex]::Match($t, 'Build\s*=\s*"([^"]+)"')).Groups[1].Value
    $vDate  = ([regex]::Match($t, 'Date\s*=\s*"([^"]+)"')).Groups[1].Value
}
Check "F1" "F" "src\Ver.cs declares Number / Build / Date (STD-VER-01)" `
    ($vNum -and $vBuild -and $vDate) ("Number={0} Build={1} Date={2}" -f $vNum, $vBuild, $vDate) $false
Check "F2" "F" "Ver.Build is Ver.Number + '.0' shape (STD-VER-01)" `
    ($vBuild -like ($vNum + "*")) ("{0} vs {1}" -f $vBuild, $vNum) $true

$rnPath = P "RELEASE_NOTES.txt"
$rnOk = $false; $rnTop = ""
if (Test-Path $rnPath) {
    foreach ($ln in [System.IO.File]::ReadAllLines($rnPath, [System.Text.Encoding]::UTF8)) {
        $t = $ln.Trim()
        if ($t.StartsWith("#") -or $t.Length -eq 0) { continue }
        if ($t -match '^\d+\.\d+(\.\d+)?$') { $rnTop = $t; break }
    }
    $rnOk = ($rnTop -eq $vNum)
}
Check "F3" "F" "RELEASE_NOTES.txt top block == Ver.Number (STD-VER-02)" $rnOk `
    ("notes top={0}, Ver.Number={1}" -f $rnTop, $vNum) $true

$ltPath = P "release\latest.txt"
if (Test-Path $ltPath) {
    $lb = Bytes $ltPath
    $ltBom = ($lb.Length -ge 3 -and $lb[0] -eq 0xEF)
    $ltEol = Eols $ltPath
    $lines = @([System.IO.File]::ReadAllLines($ltPath, [System.Text.Encoding]::UTF8) | Where-Object { $_.Trim().Length -gt 0 })
    $keys = @($lines | ForEach-Object { ($_ -split '=', 2)[0] })
    $orderOk = (($keys -join ",") -eq "version,url,sha256,notes")
    Check "F4" "F" "latest.txt: UTF-8 NO BOM + CRLF + 4 keys in order (STD-ENC-04)" `
        ((-not $ltBom) -and $ltEol.Lf -eq 0 -and $orderOk) `
        ("bom={0} lf={1} keys={2}" -f $ltBom, $ltEol.Lf, ($keys -join ",")) $true
    $ltVer = ([regex]::Match((Read-Utf8 $ltPath), '(?m)^version=(.+)$')).Groups[1].Value.Trim()
    Check "F5" "F" "latest.txt version == Ver.Number (STD-PIPE-02)" ($ltVer -eq $vNum) `
        ("latest.txt={0} Ver.cs={1}" -f $ltVer, $vNum) $true
} else {
    Skip "F4" "F" "latest.txt contract" "release\latest.txt not present (never packaged)"
    Skip "F5" "F" "latest.txt version agreement" "release\latest.txt not present"
}

# ================================================================ [G] FROZEN & DRIFT
# [G1] payload seal integrity: recompute the sealed set's hashes and compare to payload.sha256. An
# unsigned but matching seal proves the standard the checker is running was not edited (integrity);
# signing payload.sha256.ps1 with the pinned cert adds authenticity and is a CROWN owner step.
if ($haveStdlib -and (Test-Path $payloadPath)) {
    $sealDir = Split-Path -Parent $payloadPath
    $sealBad = @()
    foreach ($ln in [System.IO.File]::ReadAllLines($payloadPath, [System.Text.Encoding]::UTF8)) {
        if ($ln.Trim().Length -eq 0) { continue }
        $parts = $ln -split ' = ', 2
        if ($parts.Count -lt 2) { continue }
        $rel = $parts[0]; $want = $parts[1]
        $fp = Join-Path $sealDir $rel
        if (-not (Test-Path $fp)) { $sealBad += ($rel + " (absent)"); continue }
        if ((Get-FileHash -LiteralPath $fp -Algorithm SHA256).Hash -ne $want) { $sealBad += $rel }
    }
    # authenticity: if the owner signed payload.sha256.ps1 (CROWN, seal-sign.ps1), verify it -
    # Authenticode Valid + signer == STUDIO seal signer + embedded hash == recomputed payload hash.
    # Fails CLOSED on any doubt. Absent wrapper -> integrity-only (unsigned), still a PASS.
    #
    # NOTE: the signer compared here is the STUDIO's, NOT the project's own pin. The seal is a studio
    # artifact: one wrapper is signed once and vendored to every app. Comparing it to the project's
    # Identity.Thumbprint false-reds any app that carries its OWN publisher certificate - found on
    # LiteScribe (pins A30B626A..., seal signed 8D623A7E...), which reported SEAL MISMATCH while the
    # seal was perfectly good. The constant is safe to carry in this file because this file's own
    # hash is inside the sealed payload: altering it is exactly what the signature detects.
    $signed = Test-Path (Join-Path $sealDir "payload.sha256.ps1")
    $note = "integrity verified"
    if ($signed) {
        $sigFile = Join-Path $sealDir "payload.sha256.ps1"
        $sig = Get-AuthenticodeSignature -FilePath $sigFile
        $embedded = ([regex]::Match((Read-Utf8 $sigFile), '\$PayloadSha256\s*=\s*"([0-9A-Fa-f]+)"')).Groups[1].Value
        $wantHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
        $signerTp = ""
        if ($sig.SignerCertificate) { $signerTp = $sig.SignerCertificate.Thumbprint }
        $StudioSealSigner = "8D623A7EFA278C2ADCE78D1A47612AD5754CD551"   # studio publisher (seals the standard)
        if ($sig.Status -eq 'Valid' -and $signerTp -eq $StudioSealSigner -and $embedded -eq $wantHash) {
            $note = "integrity + authenticity verified (studio seal signer)"
        } else {
            $sealBad += "signature"
            $note = ("SEAL SIGNATURE INVALID: status={0} signer={1} hashMatch={2}" -f $sig.Status, $signerTp, ($embedded -eq $wantHash))
        }
    } else {
        $note += "; UNSIGNED - owner signs payload.sha256.ps1 for authenticity (STD-SIGN-03 CROWN)"
    }
    Check "G1" "G" "vendored standard payload seal (STD-CONF-04)" ($sealBad.Count -eq 0) `
        $(if ($sealBad.Count -eq 0) { $note } else { "SEAL MISMATCH: " + ($sealBad -join ", ") }) $false
} else {
    Skip "G1" "G" "vendored standard payload seal (STD-CONF-04)" "payload.sha256 or stdlib.ps1 not found (not vendored here)"
}

# [G2] frozen-file drift: each agreed-core frozen file, normalized for identity, must hash to its
# recorded fingerprint. RED means a family-core file was edited in THIS project and not the others -
# the silent crown fork the standard exists to prevent (STD-CONF-02). Known forks (Ui.cs ahead,
# Installer via Ident.cs, per-app resource lists) are NOT in the core - they are open items in
# drift.pending.txt / fanout, and resolving them is the owner's call, not a red gate (STD-CONF-03).
if ($haveStdlib -and (Test-Path $frozenPath)) {
    $drift = @(); $missing = @(); $core = 0
    foreach ($ln in [System.IO.File]::ReadAllLines($frozenPath, [System.Text.Encoding]::UTF8)) {
        if ($ln.Trim().Length -eq 0) { continue }
        $parts = $ln -split ' = ', 2
        if ($parts.Count -lt 2) { continue }
        $core++
        $rel = $parts[0]; $want = $parts[1]
        $fp = P $rel
        if (-not (Test-Path $fp)) { $missing += $rel; continue }
        if ((Std-Fingerprint $fp $script:Bind.Identity) -ne $want) { $drift += $rel }
    }
    # Drift is a defect; ABSENCE is not. A core file the studio shares is not required to exist in
    # every app - LiteScribe legitimately has no Translit.cs / MakeIco.cs / emblem96.png, and failing
    # it for that would punish an app for being a different product (STANDARD.md sec.18.2 ABSENCE).
    # What G2 guards is the file a project DOES ship silently diverging from the family. Absence is
    # reported in the detail line so it stays visible, and "must exist" stays [H1]/tree's job.
    $ok = ($drift.Count -eq 0)
    $pend = ""
    $pendPath = Join-Path $ScriptDir "drift.pending.txt"
    if (Test-Path $pendPath) {
        $pn = @([System.IO.File]::ReadAllLines($pendPath, [System.Text.Encoding]::UTF8) |
               Where-Object { $_.Trim().Length -gt 0 -and -not $_.StartsWith("#") }).Count
        $pend = ("; {0} fork(s) pending owner decision (drift.pending.txt)" -f $pn)
    }
    $detail = ("{0} core file(s) verified{1}" -f $core, $pend)
    if ($drift.Count)   { $detail = "DRIFT (silent fork of family core): " + ($drift -join ", ") }
    if ($missing.Count) { $detail += "; MISSING here: " + ($missing -join ", ") }
    Check "G2" "G" "no silent drift in agreed frozen core (STD-CONF-02)" $ok $detail $false
} else {
    Skip "G2" "G" "frozen-file drift vs reference (STD-CONF-02)" "frozen.sha256 or stdlib.ps1 not found (run rebuild.ps1)"
}

$sv = $script:Bind.StandardVersion
$studioVer = $null
$studioVerPath = "D:\Soft\_studio\standard.version"
if (Test-Path $studioVerPath) { $studioVer = [int](Read-Utf8 $studioVerPath).Trim() }
if ($studioVer -eq $null) {
    Check "G3" "G" "standard version pin (STD-CONF-03)" $true ("pinned v{0}; studio not reachable - offline OK" -f $sv) $false
} elseif ($studioVer -gt $sv) {
    # BEHIND IS A STATE, NOT A DEFECT - it must not turn the number red (STD-CONF-03)
    Check "G3" "G" "standard version pin (STD-CONF-03)" $true ("pinned v{0}; studio v{1} -> BEHIND by {2}" -f $sv, $studioVer, ($studioVer - $sv)) $false
} else {
    Check "G3" "G" "standard version pin (STD-CONF-03)" $true ("pinned v{0}; studio v{1} - current" -f $sv, $studioVer) $false
}

# release.ps1 is the canary: it has ZERO placeholders, so it must be byte-identical studio-wide.
$sibs = @()
if (Test-Path "D:\Soft\_studio\projects.json") {
    $sibs = @((Read-Utf8 "D:\Soft\_studio\projects.json" | ConvertFrom-Json).projects | Where-Object { $_.AppId -ne $APP })
}
$relHash = ""
if (Test-Path (P "release.ps1")) { $relHash = (Get-FileHash (P "release.ps1") -Algorithm SHA256).Hash }
$diverged = @()
foreach ($s in $sibs) {
    $o = Join-Path $s.Path "release.ps1"
    if (Test-Path $o) {
        $oh = (Get-FileHash $o -Algorithm SHA256).Hash
        if ($oh -ne $relHash) { $diverged += $s.AppId }
    }
}
if ($sibs.Count -eq 0) {
    Skip "G4" "G" "release.ps1 byte-identical across the studio" "no sibling projects reachable"
} else {
    Check "G4" "G" "release.ps1 byte-identical across the studio (STD-UPD-01 canary)" ($diverged.Count -eq 0) `
        ("hash {0}; diverged: {1}" -f $relHash.Substring(0,16), ($diverged -join ", ")) $false
}

$reqBind = @("StandardVersion", "MinStandardVersion", "Identity", "Deviations")
$missBind = @($reqBind | Where-Object { -not $script:Bind.PSObject.Properties.Name.Contains($_) })
Check "G5" "G" "standard.bind.json has every required key" ($missBind.Count -eq 0) ($missBind -join ", ") $true

# ================================================================ [H] DOC-SET & TRUTH
$reqDocs = @("CLAUDE.md", "STRUCTURE.md", "README.md", "SIGNING.md", "RELEASE_NOTES.txt",
             "docs\HANDOFF.md", "docs\_MAINTAIN.md", "docs\_MAINTENANCE_LOG.md")
$missDocs = @($reqDocs | Where-Object { -not (Test-Path (P $_)) })
Check "H1" "H" "required doc set present (STD-DOC-01)" ($missDocs.Count -eq 0) ($missDocs -join ", ") $true

# STD-DOC-03: every path a doc claims exists, must exist. This is the rule that produced the
# whole standard: docs\DESIGN_STANDARD.md was cited as present in 8 places and never existed.
$phantom = @()
foreach ($d in @("CLAUDE.md", "STRUCTURE.md", "docs\HANDOFF.md")) {
    $dp = P $d
    if (-not (Test-Path $dp)) { continue }
    foreach ($m in [regex]::Matches((Read-Utf8 $dp), '`([A-Za-z_][A-Za-z0-9_.]*\\[A-Za-z0-9_.\\]+\.(md|cs|ps1|txt|json))`')) {
        $rel = $m.Groups[1].Value
        if ($rel -match '^\.standard\\' ) { continue }
        if (-not (Test-Path (P $rel))) { $phantom += ("{0} -> {1}" -f $d, $rel) }
    }
}
Check "H2" "H" "no doc references a nonexistent path (STD-DOC-03)" ($phantom.Count -eq 0) `
    (($phantom | Select-Object -Unique) -join " | ") $true

$logPath = P "docs\_MAINTENANCE_LOG.md"
$logOk = $false; $logDetail = "log missing"
if (Test-Path $logPath) {
    $first = @([System.IO.File]::ReadAllLines($logPath, [System.Text.Encoding]::UTF8))[0]
    # Case-insensitive: the brand is written upper-case in headings ("VALERA ZSU" vs "Valera ZSU").
    $lo = $first.ToLowerInvariant()
    $logOk = ($lo.Contains($APP.ToLowerInvariant()) -or $lo.Contains($script:Bind.Identity.BrandUk.ToLowerInvariant()))
    $logDetail = "title: " + $first
}
Check "H3" "H" "journal title matches the current identity (STD-DOC-06)" $logOk $logDetail $true

$structPath = P "STRUCTURE.md"
$devOk = $false
# STD-ENC-01: this file is ASCII-only, so the Ukrainian search token is built from code points.
# Reads: "vidkhylen" - the stem of "svidomi VIDKHYLENnia vid etalona" (deviations register).
$DEV_STEM = -join ([int[]]@(0x0432,0x0456,0x0434,0x0445,0x0438,0x043B,0x0435,0x043D) | ForEach-Object { [char]$_ })
if (Test-Path $structPath) { $devOk = (Read-Utf8 $structPath).Contains($DEV_STEM) }
Check "H4" "H" "STRUCTURE.md carries the deviations register (STD-DOC-07)" $devOk `
    "absence must read as a DECISION, not as rot - this is what stops 'one studio' becoming 'look like Valera'" $true

$claudePath = P "CLAUDE.md"
$svOk = $false
if (Test-Path $claudePath) { $svOk = (Read-Utf8 $claudePath) -match 'STANDARD_VERSION' }
Check "H5" "H" "CLAUDE.md stamps STANDARD_VERSION (STD-DOC-02)" $svOk "" $true

$enPath = P ("dist\" + $APP + "\README_EN.txt")
$wantsEn = ($script:Bind.Identity.UiLangs -contains "en")
$hasEn = Test-Path $enPath
Check "H6" "H" "README_EN.txt present only if UiLangs includes 'en' (STD-LOC-03)" ($wantsEn -eq $hasEn) `
    ("UiLangs={0}; README_EN present={1}" -f ($script:Bind.Identity.UiLangs -join ","), $hasEn) $true

$absPaths = @()
foreach ($dir in @("src", "setup")) {
    $dp = P $dir
    if (-not (Test-Path $dp)) { continue }
    foreach ($f in (Get-ChildItem $dp -File -Recurse -Include *.cs)) {
        if ((Read-Utf8 $f.FullName) -match '@"[A-Za-z]:\\') { $absPaths += $f.Name }
    }
}
Check "H7" "H" "no absolute path hardcoded in src\ or setup\ (STD-TREE-02)" ($absPaths.Count -eq 0) `
    (($absPaths | Select-Object -Unique) -join ", ") $true

# [H8] STD-CC-01: the AUTHORED policy file must exist and parse. settings.local.json is the
# machine-accumulated allowlist (class X) and does NOT satisfy this - that confusion is the whole
# point: until v3 both projects carried only the local file and nobody noticed, because no check
# existed. The agent never writes permissions here (STD-CC-02); this check only makes the gap
# VISIBLE, and the owner closes it - by authoring the file or by declaring a deviation.
$ccPath = P ".claude\settings.json"
$ccOk = $false; $ccDetail = ".claude\settings.json absent (settings.local.json does NOT count - class X)"
if (Test-Path $ccPath) {
    try { (Read-Utf8 $ccPath | ConvertFrom-Json) | Out-Null; $ccOk = $true; $ccDetail = "present and parses" }
    catch { $ccDetail = "present but does NOT parse as JSON: $($_.Exception.Message)" }
}
Check "H8" "H" "authored .claude\settings.json exists and parses (STD-CC-01)" $ccOk $ccDetail $false

# [H9] STD-VER-01: app.manifest carries an assembly identity version that has drifted from Ver.cs
# in BOTH projects since the line was written ("1.0.0.0" vs a live 1.0.9.0) - inert today, and prose
# alone never moved it. Two honest outcomes: bind it to Ver.Build, or DECLARE the divergence in
# standard.bind.json (Deviations -> Check "H9"). Silence is no longer one of them.
$manPath = P "app.manifest"
if (Test-Path $manPath) {
    $manVer = ([regex]::Match((Read-Utf8 $manPath), '<assemblyIdentity[^>]*\sversion="([0-9.]+)"')).Groups[1].Value
    $manOk = ($manVer -eq $vBuild)
    Check "H9" "H" "app.manifest version == Ver.Build, or declared (STD-VER-01)" $manOk `
        $(if ($manOk) { "both $vBuild" } else { "app.manifest={0} Ver.Build={1} - bind it or declare a deviation" -f $manVer, $vBuild }) $true
} else {
    Skip "H9" "H" "app.manifest version agreement (STD-VER-01)" "app.manifest not present"
}

# ================================================================ [M] METHOD (standard v2)
# Gated on the project's PIN, not the studio version: BEHIND IS A STATE (STD-CONF-03). A project
# pinned at v1 skips these until it responds to the v2 amendment; nothing goes red for being behind.
# Provenance for all four: the 2026-07-20 coverage audit - one green test hid a privacy hole,
# the first mutation run scored 3/10, a missing corpus silently passed the gate.
if ([int]$script:Bind.StandardVersion -ge 2) {
    # M1: STD-GATE-09 (and via it STD-GATE-08 / STD-LIFE-03 / STD-LIFE-04, whose named proof is the
    # mutation catalogue). Presence + the verdict machinery; the actual 10/10 run is a release-time
    # gate, not something a read-only checker can execute.
    $mutPath = P "tools\mutate.ps1"
    $mutOk = $false; $mutDetail = "tools\mutate.ps1 not found"
    if (Test-Path $mutPath) {
        $mt = Read-Utf8 $mutPath
        $mutOk = ($mt.Contains('$mutations') -and $mt.Contains("SURVIVED") -and $mt.Contains("CAUGHT"))
        $mutDetail = "catalogue + CAUGHT/SURVIVED verdicts present"
        if (-not $mutOk) { $mutDetail = "file exists but lacks the catalogue or verdict machinery" }
    }
    Check "M1" "M" "crown-claim mutation catalogue tools\mutate.ps1 (STD-GATE-09)" $mutOk $mutDetail $false

    # M2: STD-GATE-10 - a corpus the tests reference must exist; the gate may never 'soft-skip' it.
    $tmPath = P "tests\TestMain.cs"
    if (Test-Path $tmPath) {
        $tm = Read-Utf8 $tmPath
        $refs = @()
        foreach ($corpus in @("corpus.tsv", "qa_typing.tsv")) {
            if ($tm.Contains($corpus) -and -not (Test-Path (P ("tests\" + $corpus)))) { $refs += $corpus }
        }
        Check "M2" "M" "referenced test corpora exist on disk (STD-GATE-10)" ($refs.Count -eq 0) `
            $(if ($refs.Count) { "referenced but MISSING: " + ($refs -join ", ") } else { "all referenced corpora present" }) $false
    } else {
        Skip "M2" "M" "referenced test corpora exist (STD-GATE-10)" "tests\TestMain.cs not present"
    }

    # M3: STD-DIAG-06 - env flags arm on truthy values only, through the shared helper.
    $envBad = @()
    $srcDir = P "src"
    if (Test-Path $srcDir) {
        foreach ($f in (Get-ChildItem $srcDir -File -Include *.cs -Recurse)) {
            if ((Read-Utf8 $f.FullName) -match 'IsNullOrEmpty\s*\(\s*Environment\.GetEnvironmentVariable') { $envBad += $f.Name }
        }
    }
    $truthyOk = $false
    $diagPath = P "src\Diag.cs"
    if (Test-Path $diagPath) { $truthyOk = (Read-Utf8 $diagPath).Contains("IsTruthy") }
    Check "M3" "M" "env flags arm on truthy only, via IsTruthy (STD-DIAG-06)" (($envBad.Count -eq 0) -and $truthyOk) `
        $(if ($envBad.Count) { "IsNullOrEmpty(GetEnvironmentVariable) in: " + (($envBad | Select-Object -Unique) -join ", ") }
          elseif (-not $truthyOk) { "src\Diag.cs lacks the IsTruthy helper" } else { "no IsNullOrEmpty-armed flag; IsTruthy present" }) $true

    # M4: STD-DIAG-01b - the consent marker is re-checked on the WRITE path (StillEnabled), so a
    # withdrawn debug.on silences the log without a restart, and a failed check silences it too.
    $markOk = $false; $markDetail = "src\Diag.cs not present"
    if (Test-Path $diagPath) {
        $dg = Read-Utf8 $diagPath
        $markOk = ($dg.Contains("StillEnabled") -and $dg.Contains("debug.on"))
        $markDetail = $(if ($markOk) { "write-path marker recheck present" } else { "Diag.cs lacks the StillEnabled write-path recheck" })
    }
    Check "M4" "M" "consent marker re-checked on the write path (STD-DIAG-01b)" $markOk $markDetail $false
} else {
    Skip "M1" "M" "crown-claim mutation catalogue (STD-GATE-09)" ("project pinned v" + $script:Bind.StandardVersion + " - v2 checks apply after it responds to the amendment")
    Skip "M2" "M" "referenced test corpora exist (STD-GATE-10)" "pinned below v2"
    Skip "M3" "M" "env flags truthy-only (STD-DIAG-06)" "pinned below v2"
    Skip "M4" "M" "consent marker on write path (STD-DIAG-01b)" "pinned below v2"
}

# ================================================================ report
Write-Host ""
$groups = @{ A = "BYTES & ENCODING"; B = "SECRETS & GITIGNORE"; C = "IDENTITY"; D = "BUILD CHAIN";
             E = "UI & CODE"; F = "VERSION & RELEASE"; G = "FROZEN & DRIFT"; H = "DOC-SET & TRUTH";
             M = "METHOD" }
# The print order is DERIVED from the results, not hardcoded. A hardcoded list silently swallowed
# every [M] result when v2 added the group: the summary counted "4 FAILED" while printing three -
# a checker whose number disagrees with its own output cannot be acted on, which is worse than a
# missing check. Known groups keep their canonical order; anything new is appended, never dropped.
$known = @("A","B","C","D","E","F","G","H","M")
$present = @($script:Results | ForEach-Object { $_.Group } | Select-Object -Unique)
$order = @($known | Where-Object { $present -contains $_ }) + @($present | Where-Object { $known -notcontains $_ } | Sort-Object)
foreach ($g in $order) {
    if (-not $groups.ContainsKey($g)) { $groups[$g] = "UNNAMED GROUP $g" }
    $rs = @($script:Results | Where-Object { $_.Group -eq $g })
    if ($rs.Count -eq 0) { continue }
    Write-Host ("[{0}] {1}" -f $g, $groups[$g])
    foreach ($r in $rs) {
        if ($r.State -eq "PASS" -and -not $ShowPassed) { continue }
        $mark = "  "
        if ($r.State -eq "FAIL") { $mark = "X " }
        if ($r.State -eq "SKIP") { $mark = "~ " }
        if ($r.State -eq "N/A")  { $mark = "- " }
        Write-Host ("  {0}{1,-4} {2,-5} {3}" -f $mark, $r.Id, $r.State, $r.Title)
        if ($r.State -ne "PASS" -and $r.Detail) { Write-Host ("        {0}" -f $r.Detail) }
    }
}

$total   = @($script:Results | Where-Object { $_.State -ne "N/A" }).Count
$passed  = @($script:Results | Where-Object { $_.State -eq "PASS" }).Count
$skipped = @($script:Results | Where-Object { $_.State -eq "SKIP" }).Count
$failed  = @($script:Results | Where-Object { $_.State -eq "FAIL" }).Count
$na      = @($script:Results | Where-Object { $_.State -eq "N/A" }).Count

Write-Host ""
Write-Host ("=== CONFORMANCE: {0}/{1} checks passed ===" -f ($passed + $skipped), $total)
if ($skipped -gt 0) { Write-Host ("    {0} skipped by a DECLARED deviation (STANDARD.md sec.18)" -f $skipped) }
if ($na -gt 0)      { Write-Host ("    {0} not applicable / pending" -f $na) }
if ($failed -gt 0)  { Write-Host ("    {0} FAILED" -f $failed) }
Write-Host ""
if ($failed -gt 0) { exit 1 }
exit 0
