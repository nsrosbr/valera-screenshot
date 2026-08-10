# stdlib.ps1 - shared primitives for the VALERA standard tooling (rebuild.ps1 + conform.ps1 G2).
# ASCII-only (STD-ENC-01). Dot-sourced, never run directly. It exists so the identity NORMALIZER
# lives in exactly one place: if rebuild.ps1 (which writes the reference) and conform.ps1 (which
# checks against it) each carried their own copy, the two would drift and the drift checker would
# itself become the thing it warns about.
#
# The normalizer is the inverse of the placeholder binding in STANDARD.md sec.0.5: it replaces a
# project's concrete identity tokens with the fixed <PLACEHOLDER>s, so two frozen files that differ
# ONLY by identity (e.g. Updater.cs: 14 lines, all namespace/URL/cer/appdata) normalize to the same
# text and hash equal. Proven on the crown: ValeraZSU\Updater.cs and Valera\Updater.cs collapse to a
# single normalized form.
#
# FAILURE DIRECTION IS SAFE. Normalization only REMOVES identity; it can never make genuinely
# different code hash equal. So a MISSED token yields a false "drift" (annoying, owner reviews, sees
# it is identity) - never a false "clean". A drift checker is only dangerous when it says clean and
# is wrong; this one cannot.

function Std-ReadUtf8([string]$p) { return [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8) }
function Std-Bytes([string]$p)    { return [System.IO.File]::ReadAllBytes($p) }
function Std-Sha256Text([string]$t) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $h = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($t))
    $sha.Dispose()
    return ([System.BitConverter]::ToString($h) -replace '-', '')
}
function Std-Sha256Bytes([byte[]]$b) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $h = $sha.ComputeHash($b)
    $sha.Dispose()
    return ([System.BitConverter]::ToString($h) -replace '-', '')
}

# A file is treated as binary (hashed raw, never normalized) if it carries a NUL in the first 8 KB.
function Std-IsBinary([string]$p) {
    $b = Std-Bytes $p
    $n = [Math]::Min($b.Length, 8192)
    for ($i = 0; $i -lt $n; $i++) { if ($b[$i] -eq 0) { return $true } }
    return $false
}

# Build the ordered (value -> placeholder) replacement table from a bind Identity object.
# Derived values follow the sec.0.5 formulas; provided values come straight from the bind.
# Deduped by value (first wins) and sorted LONGEST-FIRST so a superstring (ValeraZSUCodeSign.cer)
# is replaced before its substring (ValeraZSU) - the single ordering rule the whole thing rests on.
function Std-IdentityPairs($id) {
    $app = [string]$id.AppId
    $raw = @(
        @{ v = [string]$id.DisplayName;                      p = '<DISPLAY_NAME>' }
        @{ v = ($app + '_SingleInstance_{' + [string]$id.MutexHex + '}'); p = '<MUTEX>' }
        @{ v = ($app + 'CodeSign.cer');                      p = '<CER>' }
        @{ v = ($app + 'Setup.exe');                         p = '<SETUP_EXE>' }
        @{ v = ($app + '.exe');                              p = '<EXE>' }
        @{ v = ('%APPDATA%\' + $app);                        p = '<APPDATA>' }
        @{ v = [string]$id.Repo;                             p = '<REPO>' }
        @{ v = [string]$id.ReleaseTitle;                     p = '<RELEASE_TITLE>' }
        @{ v = $app.ToUpperInvariant();                      p = '<ENV_PREFIX>' }
        @{ v = [string]$id.ResourceId;                       p = '<RESOURCE_ID>' }
        # PUBLISHER IDENTITY. These three were the reason ONE certificate had to serve every app:
        # left un-normalized, the pinned thumbprint and the cert Subject stay literal text inside
        # frozen files (sign.ps1, deploy\trust_cert.cmd, src\Updater.cs), so giving an app its own
        # certificate silently forked it out of the shared core - the exact defect G2 exists to
        # catch. Normalized, each app carries its own publisher cert and the core stays byte-equal.
        # Empty/absent values are skipped below, so a project that has not declared them is unaffected.
        @{ v = [string]$id.CertSubject;                      p = '<CERT_SUBJECT>' }
        @{ v = [string]$id.CertOrg;                          p = '<CERT_ORG>' }
        @{ v = [string]$id.Thumbprint;                       p = '<THUMBPRINT>' }
        @{ v = [string]$id.BrandUk;                          p = '<BRAND_UK>' }
        @{ v = [string]$id.BrandEn;                          p = '<BRAND_EN>' }
        @{ v = $app;                                         p = '<APP>' }
    )
    # ORDINAL set: PowerShell's default @{} hashtable is CASE-INSENSITIVE, which collapsed the
    # AppId pair ('ValeraZSU') against the EnvPrefix pair ('VALERAZSU') and silently dropped <APP>,
    # leaving the bare namespace/UserAgent tokens un-normalized. Identity is case-significant, so
    # the dedup must be too.
    $seen = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
    $pairs = @()
    foreach ($e in $raw) {
        $v = $e.v
        if ([string]::IsNullOrEmpty($v)) { continue }
        if ($seen.Contains($v)) { continue }   # same string, one placeholder (deterministic)
        [void]$seen.Add($v)
        $pairs += [pscustomobject]@{ V = $v; P = $e.p }
    }
    return @($pairs | Sort-Object { $_.V.Length } -Descending)
}

# Normalize a text file's identity to placeholders, given the owning project's bind Identity.
function Std-NormalizeText([string]$text, $id) {
    $t = $text
    foreach ($pair in (Std-IdentityPairs $id)) {
        $t = $t.Replace($pair.V, $pair.P)
    }
    return $t
}

# Canonical fingerprint of a frozen file as seen from one project:
#   binary -> raw SHA-256 (identity never appears in a PNG)
#   text   -> SHA-256 of the identity-normalized text
# Line endings are normalized to LF first so a CRLF/LF packaging difference is not read as drift.
function Std-Fingerprint([string]$path, $id) {
    if (Std-IsBinary $path) { return "bin:" + (Std-Sha256Bytes (Std-Bytes $path)) }
    $t = (Std-ReadUtf8 $path) -replace "`r`n", "`n"
    $t = Std-NormalizeText $t $id
    return "txt:" + (Std-Sha256Text $t)
}
