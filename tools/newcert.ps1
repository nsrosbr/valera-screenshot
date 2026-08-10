# newcert.ps1 - OWNER-RUN ONLY. Mints ValeraScreenshot's OWN publisher certificate and moves the pin.
#
# CROWN twice over (CLAUDE.md sec.18.1): item 1 - the pinned thumbprint; item 6 - the certificate
# store (STD-SIGN-03). The environment also blocks the agent from both, so this cannot run by
# accident. The agent authored it; the owner executes it. ASCII-only (STD-ENC-01).
#
# CONTEXT: the owner ordered the old organisation out of the product entirely. Every textual
# mention is already gone (2026-07-21); the product builds UNSIGNED. The ONLY remaining carrier of
# the old identity is the still-pinned SHARED certificate. This script replaces it:
#   1. mints a personal certificate (Subject taken from the bind's CertSubject - no literals here),
#   2. moves the pin (old thumbprint read from the bind at run time) across the tree,
#   3. exports the new .cer beside the exe / setup\ / dist\,
#   4. proves sign.ps1 + deploy\trust_cert.cmd still agree with the etalon, then signs the build.
# The OLD cert is NOT removed from the store - the studio's published app still signs with it.
#
# PREREQUISITE: tools\studio_percert.ps1 -Apply must have run (it wires CertSubject/CertOrg/
# Thumbprint into the studio normalizer). This script refuses to run before it.
#
# Usage:  powershell -ExecutionPolicy Bypass -File .\tools\newcert.ps1           (dry run)
#         powershell -ExecutionPolicy Bypass -File .\tools\newcert.ps1 -Apply
param(
    [string]$Root   = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)),
    [string]$Studio = "D:\Soft\_studio",
    [string]$Etalon = "D:\Soft\Valera",
    [switch]$Apply
)
$ErrorActionPreference = "Stop"
. (Join-Path $Studio "stdlib.ps1")

$bind = Get-Content (Join-Path $Root "standard.bind.json") -Raw | ConvertFrom-Json
$OLD_TP  = [string]$bind.Identity.Thumbprint
$SUBJECT = [string]$bind.Identity.CertSubject
$ORG     = [string]$bind.Identity.CertOrg
if (-not $OLD_TP -or -not $SUBJECT -or -not $ORG) { throw "bind Identity lacks Thumbprint/CertSubject/CertOrg - ABORT" }
$OLD_TP8  = $OLD_TP.Substring(0, 8)   # docs cite the pin abbreviated - a full-string replace misses it
$FRIENDLY = "ValeraScreenshot Code Signing ($ORG)"

# every file that carries the pin. docs\_MAINTENANCE_LOG.md is ABSENT on purpose where possible:
# append-only history (the owner explicitly ordered the org purge there; the pin hex carries no org).
$TARGETS = @("src\Ident.cs", "src\Updater.cs", "sign.ps1", "CLAUDE.md", "SIGNING.md", "STRUCTURE.md", "standard.bind.json")
$CER_COPIES = @("ValeraScreenshotCodeSign.cer", "setup\ValeraScreenshotCodeSign.cer", "dist\ValeraScreenshotCodeSign.cer")

function Read-Text([string]$p) { return [System.IO.File]::ReadAllText($p) }
function Has-Bom([string]$p) {
    $b = [System.IO.File]::ReadAllBytes($p)
    return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}
function Write-Text([string]$p, [string]$t, [bool]$bom) {
    [System.IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($bom)))
}

# ---------------------------------------------------------------- preflight
Write-Host "=== ValeraScreenshot: mint own publisher certificate ==="
$patched = (Read-Text (Join-Path $Studio "stdlib.ps1")).Contains("<THUMBPRINT>")
Write-Host ("  studio normalizer wired : {0}" -f $(if ($patched) { "YES" } else { "NO" }))
Write-Host ("  Subject (from bind)     : {0}" -f $SUBJECT)
Write-Host ("  old pin (stays in store): {0}" -f $OLD_TP)
$found = @()
foreach ($rel in $TARGETS) {
    $p = Join-Path $Root $rel
    if (-not (Test-Path $p)) { continue }
    $t = Read-Text $p
    $n = ([regex]::Matches($t, $OLD_TP8)).Count
    if ($n) { $found += $rel; Write-Host ("  {0,-26} {1} pin reference(s)" -f $rel, $n) }
}
if (-not $patched) {
    Write-Host ""
    Write-Host "REFUSING: run .\tools\studio_percert.ps1 -Apply first."
    Write-Host "Without it the new thumbprint forks sign.ps1 out of the studio core."
    exit 1
}
if (-not $Apply) {
    Write-Host ""
    Write-Host "DRY RUN. No certificate minted, nothing written. Re-run with -Apply."
    exit 0
}

# ---------------------------------------------------------------- backup
$bak = Join-Path $Root ("_newcert_backup_" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force -Path $bak | Out-Null
foreach ($rel in $found) { Copy-Item (Join-Path $Root $rel) (Join-Path $bak ($rel -replace '[\\/]', '_')) -Force }
Write-Host ("  backup: {0}" -f $bak)

# ---------------------------------------------------------------- mint
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $SUBJECT `
    -FriendlyName $FRIENDLY -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyUsage DigitalSignature -KeyExportPolicy Exportable -NotAfter ((Get-Date).AddYears(5))
$NEW_TP = $cert.Thumbprint.ToUpperInvariant()
Write-Host ""
Write-Host ("  MINTED: {0}  (valid until {1:yyyy-MM-dd})" -f $NEW_TP, $cert.NotAfter)
if ($NEW_TP -eq $OLD_TP) { throw "impossible: new thumbprint equals the old one - ABORT" }

# ---------------------------------------------------------------- move the pin
# ORDER MATTERS: full 40-char pin first, then the 8-char abbreviation - otherwise the abbreviation
# would eat the long pin's prefix and leave a corrupted hybrid thumbprint behind.
foreach ($rel in $found) {
    $p = Join-Path $Root $rel
    $t = (Read-Text $p).Replace($OLD_TP, $NEW_TP).Replace($OLD_TP8, $NEW_TP.Substring(0, 8))
    Write-Text $p $t (Has-Bom $p)
}
foreach ($rel in $found) {
    if ((Read-Text (Join-Path $Root $rel)).Contains($OLD_TP8)) { throw ($rel + " still carries the old pin - ABORT, restore from " + $bak) }
}
try { Get-Content (Join-Path $Root "standard.bind.json") -Raw | ConvertFrom-Json | Out-Null }
catch { throw ("bind no longer parses as JSON - restore from " + $bak) }
Write-Host ("  pin moved in {0} file(s)" -f $found.Count)

# ---------------------------------------------------------------- export .cer
$tmp = Join-Path $env:TEMP "ValeraScreenshotCodeSign.cer"
Export-Certificate -Cert $cert -FilePath $tmp -Type CERT | Out-Null
foreach ($rel in $CER_COPIES) {
    $dst = Join-Path $Root $rel
    if (Test-Path (Split-Path $dst -Parent)) { Copy-Item $tmp $dst -Force; Write-Host ("  exported: {0}" -f $rel) }
}
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- proof + sign
Write-Host ""
Write-Host "=== PROOF: own certificate did NOT fork the shared core ==="
$lid = (Get-Content (Join-Path $Root   "standard.bind.json") -Raw | ConvertFrom-Json).Identity
$vid = (Get-Content (Join-Path $Etalon "standard.bind.json") -Raw | ConvertFrom-Json).Identity
$bad = 0
foreach ($rel in @("sign.ps1", "deploy\trust_cert.cmd")) {
    $lf = Std-Fingerprint (Join-Path $Root $rel) $lid
    $vf = Std-Fingerprint (Join-Path $Etalon $rel) $vid
    if ($lf -ne $vf) { $bad++ }
    Write-Host ("  {0,-26} {1}" -f $rel, $(if ($lf -eq $vf) { "AGREE with etalon" } else { "FORK <<< " + $lf }))
}
if ($bad) { Write-Host ("  REGRESSION - restore from {0}" -f $bak); exit 1 }

Write-Host ""
Write-Host "=== DONE ==="
Write-Host "NEXT (agent can do these):"
Write-Host "  1. .\build.ps1 -All -Dist  rebuild, sign with the NEW cert, produce packages"
Write-Host "  2. .\test.ps1 + .\tools\mutate.ps1 + conform - full verification"
Write-Host "  3. remove deviations LS-DEV-05 and LS-DEV-06 from standard.bind.json"
Write-Host "THEN (owner, CROWN): _studio\seal-sign.ps1 to sign the payload seal."
