# Sign ValeraScreenshot.exe (Authenticode). Run AFTER build.ps1 - rebuild strips the signature.
# Uses built-in Windows cmdlets (New-SelfSignedCertificate / Set-AuthenticodeSignature).
# Windows SDK / signtool NOT required. ASCII-only on purpose (PS 5.1 reads .ps1 as ANSI).
param(
    [string]$Exe = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "ValeraScreenshot.exe"),
    [string]$Subject  = "CN=Pavlo Isaiev, O=Pavlo Isaiev",
    [string]$Friendly = "ValeraScreenshot Code Signing (Pavlo Isaiev)",
    [string]$TimeStampUrl = "http://timestamp.digicert.com",
    # Mint a NEW publisher identity on purpose. Off by default: see the fail-closed block below.
    # package.ps1 never passes this, so a release can never take that path by accident.
    [switch]$AllowSelfSigned
)

$ErrorActionPreference = "Stop"

# 1) find the PINNED code-signing cert BY THUMBPRINT ONLY. If it is not in this machine's
#    store, import it from a local _codesign.pfx so signing keeps working on a MIGRATED
#    device (the pinned thumbprint is preserved -> installed clients still update).
#
#    STD-SIGN-01. This used to also match on FriendlyName, which was a silent channel-killer:
#      - The REAL cert's friendly name is "PuntoFree Code Signing (Pavlo Isaiev)" (a pre-rename
#        identity). No sign.ps1 ever looked for that string, so the name branch could NEVER
#        select the real cert.
#      - It COULD select a self-signed cert minted by the fallback below, which used the same
#        -FriendlyName it searched for.
#      - Net effect: run 1 warned and signed with a wrong cert; run 2 matched the fake by name
#        and signed SILENTLY. package.ps1 -> publish.ps1 then shipped it, and every installed
#        client rejected the update at Updater.VerifySignedByUs() - with no error anyone sees.
#    Thumbprint is the only identity that means anything here. Match nothing else.
$Pinned = "A30B626AF77DD5FC2FD04C11DE1D5ADAA56E8FBE"   # must match src\Updater.cs
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$cert = Get-ChildItem "Cert:\CurrentUser\My" |
        Where-Object { $_.Thumbprint -eq $Pinned -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1
if (-not $cert) {
    $pfx = Join-Path $scriptDir "_codesign.pfx"
    if (Test-Path $pfx) {
        $pwFile = Join-Path $scriptDir "_codesign_pwd.txt"
        $pw = if (Test-Path $pwFile) { (Get-Content $pwFile -Raw).Trim() } else { $env:CODESIGN_PWD }
        if ($pw) {
            $sec = ConvertTo-SecureString $pw -AsPlainText -Force
            Import-PfxCertificate -FilePath $pfx -CertStoreLocation "Cert:\CurrentUser\My" -Password $sec | Out-Null
            $cert = Get-ChildItem "Cert:\CurrentUser\My" | Where-Object { $_.Thumbprint -eq $Pinned } | Select-Object -First 1
            if ($cert) { Write-Host "Imported code-signing cert from _codesign.pfx (pinned thumbprint OK)." }
        } else {
            Write-Host "Found _codesign.pfx but no password (_codesign_pwd.txt or env CODESIGN_PWD)."
        }
    }
}
# FAIL CLOSED. Invariant #2 ("changing the cert = breaking updates for everyone") used to be
# enforced by a Write-Host: this block minted a wrong cert and RETURNED SUCCESS, so package.ps1
# and publish.ps1 sailed on and shipped it. Refusing to sign is always cheaper than shipping a
# build the whole installed base rejects. STD-SIGN-02.
if (-not $cert -and -not $AllowSelfSigned) {
    throw @"
PINNED code-signing cert $Pinned not found, and no importable _codesign.pfx.
REFUSING TO SIGN (fail closed).

Signing with any other certificate produces a build that EVERY installed client REJECTS at
the update thumbprint gate (src\Updater.cs) - silently: the user sees no error, just never
gets the update. That is unrecoverable through the channel itself.

Fix: bring _codesign.pfx + _codesign_pwd.txt to this device (CLAUDE.md - portability).
To mint a NEW publisher identity ON PURPOSE - accepting that it breaks the update channel
for every existing install - re-run explicitly:   .\sign.ps1 -AllowSelfSigned
"@
}
if (-not $cert) {
    Write-Host "WARNING: -AllowSelfSigned given - minting a NEW self-signed cert."
    Write-Host "Its thumbprint will NOT match the pin in src\Updater.cs, so builds signed with it"
    Write-Host "are REJECTED by every installed client's auto-update. Never use this for a release."
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
        -FriendlyName $Friendly -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyUsage DigitalSignature -KeyExportPolicy Exportable -NotAfter ((Get-Date).AddYears(5))
}
Write-Host ("Certificate: {0}  (valid until {1:yyyy-MM-dd})" -f $cert.Thumbprint, $cert.NotAfter)

# 2) sign (with timestamp if online, otherwise without)
try {
    $sig = Set-AuthenticodeSignature -FilePath $Exe -Certificate $cert `
        -HashAlgorithm SHA256 -TimestampServer $TimeStampUrl
    if ($null -eq $sig.TimeStamperCertificate) { throw "no timestamp" }
    Write-Host "Signed WITH timestamp."
} catch {
    Write-Host "Timestamp unavailable (offline) - signing without it."
    $sig = Set-AuthenticodeSignature -FilePath $Exe -Certificate $cert -HashAlgorithm SHA256
}
Write-Host ("Signature Status = {0}" -f $sig.Status)

# 3) export the PUBLIC cert (.cer) - this is what you push to Trusted Publisher via GPO
$cer = Join-Path (Split-Path -Parent $Exe) "ValeraScreenshotCodeSign.cer"
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Write-Host ("Public certificate for distribution: {0}" -f $cer)
Write-Host "To make the .exe TRUSTED on machines, see SIGNING.md (Trusted Publisher / GPO)."
