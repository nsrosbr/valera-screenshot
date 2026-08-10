# publish.ps1 - upload the built+signed release to GitHub Releases via the REST API.
# ASCII-only (STD-ENC-01). No gh CLI, no git push, no winget - pure PowerShell.
#
# WHY THE REST API AND NOT gh/git. The update channel is fed by the RELEASES api alone, so shipping
# an update never requires pushing source. Those are two independent decisions, and keeping them
# independent means a broken push can never hold the channel hostage - and a published channel can
# never force source out before it is ready. The studio reference (Valera) works the same way; a
# second, different publishing story in one studio would be exactly the drift conform exists to catch.
#
# ONE-TIME SETUP: create a fine-grained GitHub token (Contents: Read and write on the repo below)
# and save it to _ghtoken.txt in this folder (local only, .gitignore'd). Then a release is one
# command: .\release.ps1  (gate -> package -> publish).
#
# THE ASSET PAIR IS NOT DECORATION. latest.txt names the binary the updater downloads, so BOTH must
# be attached or every installed copy gets a 404 on its very first update. That trap was real here:
# the invariant used to say "exactly two artifacts", which omitted the bare exe the manifest points at.
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = "nsrosbr/valera-screenshot"

# --- token (env GH_TOKEN, or _ghtoken.txt) ---
$token = $env:GH_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) {
    $tf = Join-Path $root "_ghtoken.txt"
    if (Test-Path $tf) { $token = (Get-Content $tf -Raw).Trim() }
}
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "No GitHub token. Create _ghtoken.txt with a fine-grained token (Contents: write on $repo), or set env GH_TOKEN."
}

# --- files + version (read from release\latest.txt, which package.ps1 wrote) ---
$rel = Join-Path $root "release"
$exe = Join-Path $rel "ValeraScreenshot.exe"
$manifest = Join-Path $rel "latest.txt"
if (-not (Test-Path $exe) -or -not (Test-Path $manifest)) {
    throw "release\ValeraScreenshot.exe or latest.txt missing - run package.ps1 first."
}
$ver = ((Get-Content $manifest | Where-Object { $_ -like 'version=*' }) -replace 'version=','').Trim()
if ([string]::IsNullOrWhiteSpace($ver)) { throw "Cannot read version= from latest.txt" }
$tag = "v$ver"

# The signature IS the update channel's whole promise: the app refuses any binary not signed by our
# pinned certificate. Publishing an unsigned exe would therefore ship an update nobody can install -
# silently, and discovered only by users. Refuse here instead. UnknownError is accepted because a
# self-signed certificate reads that way until it is in the machine's trusted roots; absence of a
# signer certificate is not accepted at all.
$sig = Get-AuthenticodeSignature $exe
if (-not $sig.SignerCertificate) { throw "release\ValeraScreenshot.exe carries no signature - refusing to publish." }
if ($sig.Status -ne "Valid" -and $sig.Status -ne "UnknownError") {
    throw "release\ValeraScreenshot.exe signature status is '$($sig.Status)' - refusing to publish."
}

$headers = @{
    Authorization = "Bearer $token"
    Accept = "application/vnd.github+json"
    "User-Agent" = "ValeraScreenshot-Publisher"
    "X-GitHub-Api-Version" = "2022-11-28"
}
$api = "https://api.github.com/repos/$repo"

Write-Host "Publishing $tag to $repo ..."

# --- 1) ensure the repo has an initial commit (a brand-new empty repo cannot be tagged) ---
try { Invoke-RestMethod -Uri "$api/contents/README.md" -Headers $headers -Method Get | Out-Null }
catch {
    $readme = "# VALERA Screenshot`n`nAutomatic update channel. See the Releases page."
    $content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($readme))
    $b = @{ message = "init update channel"; content = $content } | ConvertTo-Json
    Invoke-RestMethod -Uri "$api/contents/README.md" -Headers $headers -Method Put -Body $b -ContentType "application/json" | Out-Null
    Write-Host "  initialized repository (README)."
}

# --- 2) get or create the release for this tag ---
$release = $null
try { $release = Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $headers -Method Get } catch { }
if ($release) {
    Write-Host "  release $tag already exists (id=$($release.id)) - refreshing assets."
} else {
    $body = @{
        tag_name = $tag
        name = "VALERA Screenshot $ver"
        body = "VALERA Screenshot $ver - local screen captures. No network, no telemetry."
        draft = $false
        prerelease = $false
        make_latest = "true"
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Uri "$api/releases" -Headers $headers -Method Post -Body $body -ContentType "application/json"
    Write-Host "  created release $tag (id=$($release.id))."
}

# --- 3) upload assets (delete a same-named asset first: GitHub rejects duplicate names) ---
function Publish-Asset($path, $name) {
    foreach ($a in @($release.assets | Where-Object { $_.name -eq $name })) {
        try { Invoke-RestMethod -Uri "$api/releases/assets/$($a.id)" -Headers $headers -Method Delete | Out-Null } catch { }
    }
    $up = "https://uploads.github.com/repos/$repo/releases/$($release.id)/assets?name=$name"
    $bytes = [System.IO.File]::ReadAllBytes($path)
    Invoke-RestMethod -Uri $up -Headers $headers -Method Post -Body $bytes -ContentType "application/octet-stream" | Out-Null
    Write-Host ("  uploaded: {0}  ({1:N0} KB)" -f $name, ((Get-Item $path).Length / 1KB))
}

# The binary lands BEFORE the manifest that points at it: a poller must never see a manifest whose
# download does not exist yet.
Publish-Asset $exe "ValeraScreenshot.exe"

# The installer is for people, not for the updater. There is no portable package any more
# (owner's decision 2026-07-29), so this is the only human-facing artifact.
$setup = Get-ChildItem $rel -Filter "ValeraScreenshotSetup_v*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($setup) { Publish-Asset $setup.FullName $setup.Name }
else { Write-Host "  WARNING: no installer in release\ - publishing the update channel only." }

Publish-Asset $manifest "latest.txt"

Write-Host ""
Write-Host "DONE. $tag published: https://github.com/$repo/releases/latest"
Write-Host "Installed users get it via the tray -> Check for updates..."
