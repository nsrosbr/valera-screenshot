# studio_percert.ps1 - OWNER-RUN. Teaches the studio normalizer that the publisher certificate is
# PER-APPLICATION identity, not shared literal text. ASCII-only (STD-ENC-01).
#
# WHY OWNER-RUN: the change is mechanical and proven safe, but the agent's writes into D:\Soft are
# blocked by the environment. The agent authored the edit; the owner executes it - the same split
# the studio already uses for seal-sign.ps1.
#
# WHAT IT CHANGES
#   1. _studio\stdlib.ps1  -> Std-IdentityPairs gains CertSubject / CertOrg / Thumbprint.
#   2. Every registered project's standard.bind.json gains CertSubject + CertOrg IF MISSING,
#      seeded from THAT project's own sign.ps1 (its current truth, read at run time - this script
#      carries no publisher literals of its own). Projects that already declare the fields
#      (ValeraScreenshot does) are left untouched.
#   3. rebuild.ps1 regenerates ref\ + the (unsigned) payload seal.
#
# SAFETY: values seeded from each project's own files replace identical strings, so every
# fingerprint that agreed still agrees. The script PROVES it: ref\ membership is recorded before
# and after, and it ABORTS with a rollback pointer if any file leaves the agreed core.
# ValeraScreenshot's sign.ps1/trust_cert.cmd already carry the personal identity + declared fields,
# so the same rebuild brings them back INTO agreement (closing deviation LS-DEV-06).
#
# Usage:  powershell -ExecutionPolicy Bypass -File .\tools\studio_percert.ps1        (dry run)
#         powershell -ExecutionPolicy Bypass -File .\tools\studio_percert.ps1 -Apply
param(
    [string]$Studio = "D:\Soft\_studio",
    [switch]$Apply
)
$ErrorActionPreference = "Stop"

$PROJECTS = @("D:\Soft\Valera", "D:\Soft\ValeraZSU", "D:\ValeraScreenshot")
$ANCHOR   = "        @{ v = [string]`$id.ResourceId;                       p = '<RESOURCE_ID>' }"
$INSERT   = @"
        # PUBLISHER IDENTITY. These three were the reason ONE certificate had to serve every app:
        # left un-normalized, the pinned thumbprint and the cert Subject stay literal text inside
        # frozen files (sign.ps1, deploy\trust_cert.cmd, src\Updater.cs), so giving an app its own
        # certificate silently forked it out of the shared core - the exact defect G2 exists to
        # catch. Normalized, each app carries its own publisher cert and the core stays byte-equal.
        # Empty/absent values are skipped below, so a project that has not declared them is unaffected.
        @{ v = [string]`$id.CertSubject;                      p = '<CERT_SUBJECT>' }
        @{ v = [string]`$id.CertOrg;                          p = '<CERT_ORG>' }
        @{ v = [string]`$id.Thumbprint;                       p = '<THUMBPRINT>' }
"@

function Read-Text([string]$p) { return [System.IO.File]::ReadAllText($p) }
function Has-Bom([string]$p) {
    $b = [System.IO.File]::ReadAllBytes($p)
    return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}
function Write-Text([string]$p, [string]$t, [bool]$bom) {
    [System.IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($bom)))
}
function Core-Members([string]$studio) {
    $f = Join-Path $studio "frozen.sha256"
    if (-not (Test-Path $f)) { return @() }
    return @([System.IO.File]::ReadAllLines($f) |
             Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { ($_ -split ' = ')[0] })
}
# a project's CURRENT publisher identity = the Subject default in its own sign.ps1
function Project-CertIdentity([string]$proj) {
    $sp = Join-Path $proj "sign.ps1"
    if (-not (Test-Path $sp)) { return $null }
    $m = [regex]::Match((Read-Text $sp), '(?m)^\s*\[string\]\$Subject\s*=\s*"([^"]+)"')
    if (-not $m.Success) { return $null }
    $subj = $m.Groups[1].Value
    $o = [regex]::Match($subj, 'O=([^,]+)$')
    $org = if ($o.Success) { $o.Groups[1].Value.Trim() } else { "" }
    return @{ Subject = $subj; Org = $org }
}

$stdlib = Join-Path $Studio "stdlib.ps1"
if (-not (Test-Path $stdlib)) { throw "stdlib.ps1 not found under $Studio" }

# ---------------------------------------------------------------- preflight
$already = (Read-Text $stdlib).Contains("<THUMBPRINT>")
$text    = Read-Text $stdlib
$hits    = ([regex]::Matches($text, [regex]::Escape($ANCHOR))).Count

Write-Host "=== studio per-app certificate wiring ==="
Write-Host ("  studio        : {0}" -f $Studio)
Write-Host ("  stdlib patched: {0}" -f $(if ($already) { "YES (nothing to do)" } else { "no" }))
Write-Host ("  anchor hits   : {0} (must be exactly 1)" -f $hits)
$coreBefore = Core-Members $Studio
Write-Host ("  agreed core   : {0} file(s) before" -f $coreBefore.Count)
foreach ($p in $PROJECTS) {
    $b = Join-Path $p "standard.bind.json"
    if (-not (Test-Path $b)) { Write-Host ("  {0,-22} MISSING bind" -f (Split-Path $p -Leaf)); continue }
    if ((Read-Text $b).Contains('"CertSubject"')) {
        Write-Host ("  {0,-22} already declares CertSubject - untouched" -f (Split-Path $p -Leaf))
    } else {
        $ci = Project-CertIdentity $p
        $what = if ($ci) { "will gain CertSubject + CertOrg (from its own sign.ps1)" } else { "CANNOT seed - sign.ps1 Subject not found" }
        Write-Host ("  {0,-22} {1}" -f (Split-Path $p -Leaf), $what)
        if (-not $ci) { throw ("cannot derive publisher identity for " + $p + " - ABORT") }
    }
}
if (-not $already -and $hits -ne 1) { throw "anchor not found exactly once - stdlib.ps1 differs from the reviewed version; ABORT" }
if (-not $Apply) {
    Write-Host ""
    Write-Host "DRY RUN. Nothing written. Re-run with -Apply to perform the change."
    exit 0
}

# ---------------------------------------------------------------- backup
$stamp = (Get-Date -Format "yyyyMMdd-HHmmss")
$bak   = Join-Path $Studio ("_percert_backup_" + $stamp)
New-Item -ItemType Directory -Force -Path $bak | Out-Null
Copy-Item $stdlib (Join-Path $bak "stdlib.ps1") -Force
foreach ($f in @("frozen.sha256","payload.sha256","drift.pending.txt")) {
    $s = Join-Path $Studio $f
    if (Test-Path $s) { Copy-Item $s (Join-Path $bak $f) -Force }
}
foreach ($p in $PROJECTS) {
    $b = Join-Path $p "standard.bind.json"
    if (Test-Path $b) { Copy-Item $b (Join-Path $bak ((Split-Path $p -Leaf) + ".bind.json")) -Force }
}
Write-Host ("  backup        : {0}" -f $bak)

# ---------------------------------------------------------------- 1. stdlib
if (-not $already) {
    $bom = Has-Bom $stdlib
    Write-Text $stdlib $text.Replace($ANCHOR, ($ANCHOR + "`r`n" + $INSERT.TrimEnd())) $bom
    $chk = Read-Text $stdlib
    if (-not $chk.Contains("<THUMBPRINT>")) { throw "patch did not take - ABORT" }
    if (([regex]::Matches($chk, "[^\x00-\x7F]")).Count -gt 0) { throw "stdlib.ps1 is no longer pure ASCII (STD-ENC-01) - ABORT" }
    Write-Host "  stdlib.ps1    : patched"
}

# ---------------------------------------------------------------- 2. binds
foreach ($p in $PROJECTS) {
    $b = Join-Path $p "standard.bind.json"
    if (-not (Test-Path $b)) { continue }
    $t = Read-Text $b
    if ($t.Contains('"CertSubject"')) { continue }
    $ci = Project-CertIdentity $p
    $m = [regex]::Match($t, '(?m)^(\s*)"Thumbprint":(\s*)"([0-9A-Fa-f]{40})",\s*$')
    if (-not $m.Success) { throw ("Thumbprint line not found in " + $b + " - ABORT") }
    $pad = $m.Groups[1].Value
    $add = "`r`n" + $pad + '"CertSubject": "' + $ci.Subject + '",' +
           "`r`n" + $pad + '"CertOrg":     "' + $ci.Org  + '",'
    Write-Text $b ($t.Insert($m.Index + $m.Length, $add)) (Has-Bom $b)
    try { Get-Content $b -Raw | ConvertFrom-Json | Out-Null }
    catch { throw ("bind no longer parses as JSON: " + $b + " - restore from " + $bak) }
    Write-Host ("  {0,-14}: bind updated" -f (Split-Path $p -Leaf))
}

# ---------------------------------------------------------------- 3. rebuild + proof
Write-Host ""
& (Join-Path $Studio "rebuild.ps1")
$coreAfter = Core-Members $Studio
$lost = @($coreBefore | Where-Object { $coreAfter -notcontains $_ })

Write-Host ""
Write-Host "=== VERDICT ==="
Write-Host ("  agreed core: {0} -> {1}" -f $coreBefore.Count, $coreAfter.Count)
if ($lost.Count) {
    Write-Host ("  LOST FROM CORE: {0}" -f ($lost -join ", "))
    Write-Host ("  REGRESSION - restore from {0} and re-run rebuild.ps1" -f $bak)
    exit 1
}
Write-Host "  no file left the agreed core - the published app is unaffected."
Write-Host ""
Write-Host "NEXT: mint ValeraScreenshot's own certificate ->  .\tools\newcert.ps1 -Apply"
Write-Host "THEN: remove deviation LS-DEV-06 from ValeraScreenshot's standard.bind.json (G2 must be green without it)."
Write-Host "NOTE: the payload seal is UNSIGNED until you run _studio\seal-sign.ps1 (CROWN)."
