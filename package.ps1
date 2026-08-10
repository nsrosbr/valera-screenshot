# Full release pipeline: build + sign ValeraScreenshot.exe and the installer, normalize docs,
# and assemble a versioned distribution set under release\. ASCII-only (PS 5.1 reads .ps1 as ANSI).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# GitHub Releases account/repo that hosts updates. The "latest" alias always points to the newest
# release, so this URL never changes across versions. Set once when the repo exists.
$GitHubRepo = "nsrosbr/valera-screenshot"

# THE LIVE LAYER MUST HAVE RUN - checked FIRST, before a single second is spent building or
# signing. release.ps1 is a FROZEN studio file, byte-identical across every app in the studio and
# not ours to edit (conform G4 is the canary, and it caught the attempt). It runs test.ps1, then
# this script, judging the gate by its exit code alone. Drive.exe has been opt-in since
# 2026-07-29 - the right default for daily work, because it seizes the owner's desktop - so a
# green exit code no longer implies the UI was exercised at all. Without this refusal a release
# would ship with every UI promise unverified, announced by one line in a long log.
# Fails CLOSED: missing, unreadable, stale, or drive=0 all stop the release.
$layersFile = Join-Path $root "_gate_layers.txt"
if (-not (Test-Path $layersFile)) {
    throw "no gate record at $layersFile - run .\test.ps1 first (release.ps1 does this for you)."
}
$layers = @{}
foreach ($ln in (Get-Content $layersFile)) {
    $kv = $ln.Split('=', 2)
    if ($kv.Count -eq 2) { $layers[$kv[0].Trim()] = $kv[1].Trim() }
}
$stamp = [datetime]::MinValue
if (-not [datetime]::TryParse($layers['utc'], [ref]$stamp)) { throw "gate record has no readable timestamp - refusing to package." }
$ageMin = ([datetime]::UtcNow - $stamp.ToUniversalTime()).TotalMinutes
if ($ageMin -gt 45) {
    throw ("gate record is {0:N0} minutes old - that is not this build. Re-run .\test.ps1." -f $ageMin)
}
if ($layers['drive'] -ne '1') {
    throw ("REFUSING TO PACKAGE: the gate ran WITHOUT the live UI layer, so nothing here proves the " +
           "product still works when a person uses it. Re-run with the screen free:" + [Environment]::NewLine +
           '    $env:VALERASCREENSHOT_RUN_DRIVE = "1"; .\test.ps1' + [Environment]::NewLine +
           "  then run .\package.ps1 again (or .\release.ps1 with that variable set).")
}
Write-Host ("gate record OK: drive={0} proof={1}, {2:N0} min old" -f $layers['drive'], $layers['proof'], $ageMin)


# ONLY instances started from this folder - never the copy the owner has installed and is using.
# See the note in test.ps1: matching by process name alone had the release pipeline reaching across
# and killing the installed product on the developer's own machine.
foreach ($p in @(Get-Process ValeraScreenshot -ErrorAction SilentlyContinue)) {
    $path = $null
    try { $path = $p.Path } catch { }
    if ($path -and $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $p.Id -Force }
}
Start-Sleep -Milliseconds 700

# 1) app
& (Join-Path $root "build.ps1")
& (Join-Path $root "sign.ps1") | Select-Object -Last 1
# 2) installer (embeds the just-signed exe), then sign it
& (Join-Path $root "build_setup.ps1")
& (Join-Path $root "sign.ps1") -Exe (Join-Path $root "ValeraScreenshotSetup.exe") | Select-Object -Last 1

$exe = Join-Path $root "ValeraScreenshot.exe"
$setup = Join-Path $root "ValeraScreenshotSetup.exe"
$ver = (Get-Item $exe).VersionInfo.ProductVersion
if ([string]::IsNullOrEmpty($ver)) { $ver = ($exe | Get-Item).VersionInfo.FileVersion }

# STD-PIPE-03. $ver is read from the BUILT exe, so a build that failed open would hand us the
# PREVIOUS version here and the whole release would go out under a stale tag. Cross-check the
# artifact against the declared single source of truth (src\Ver.cs) before anything is assembled.
$verCs = Join-Path $root "src\Ver.cs"
$declared = ([regex]::Match((Get-Content $verCs -Raw), 'Number\s*=\s*"([^"]+)"')).Groups[1].Value
if ([string]::IsNullOrEmpty($declared)) { throw "cannot read Ver.Number from $verCs" }
if ($ver -ne $declared) {
    throw "VERSION MISMATCH: built exe reports '$ver' but src\Ver.cs declares '$declared'. Stale or failed build - refusing to package."
}

# 3) doc staging: fresh exe + the shipping .txt docs, normalized to UTF-8 BOM + CRLF for Notepad.
#
# * THIS STEP WAS BROKEN AND WOULD HAVE SHIPPED SILENTLY. It read from dist\ValeraScreenshot - the
#   PORTABLE package folder, which stopped existing when the portable build was discontinued. With
#   the folder gone, `Copy-Item $exe $dist` does not fail: PowerShell treats a non-existent
#   destination as a FILE NAME, so it created a stray extension-less copy of the exe called
#   "dist\ValeraScreenshot", and every `Get-ChildItem $dist -Filter *.txt` after it returned
#   nothing at all - no error, no warning. The release would have gone out with NO manual and NO
#   readme, and the all-in-one archive below would have contained an exe and nothing else.
#   Nobody caught it because package.ps1 is called only by a release, i.e. never during
#   development - the same reason build_setup.ps1 sat un-compilable for weeks.
#
#   The docs are now taken from the REPOSITORY, which is where they are authored. This list must
#   stay in step with $payloadDocs in build_setup.ps1 (what the installer places); L24 guards that
#   one against the uninstaller, and the assertion below guards this one against build_setup.
$dist = Join-Path $root "dist\ValeraScreenshot"
if (Test-Path $dist -PathType Leaf) { Remove-Item $dist -Force }   # the stray file, if one was made
New-Item -ItemType Directory -Force $dist | Out-Null

$bsText = Get-Content (Join-Path $root "build_setup.ps1") -Raw
$m = [regex]::Match($bsText, '\$payloadDocs\s*=\s*@\(([^)]*)\)')
if (-not $m.Success) { throw "cannot read `$payloadDocs from build_setup.ps1 - the doc list has moved; fix this before releasing" }
$docs = @([regex]::Matches($m.Groups[1].Value, '"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
if ($docs.Count -eq 0) { throw "`$payloadDocs parsed as EMPTY - refusing to package a release with no documentation" }

Copy-Item $exe (Join-Path $dist (Split-Path $exe -Leaf)) -Force
foreach ($d in $docs) {
    $s = Join-Path $root $d
    if (-not (Test-Path $s)) { throw "shipping document missing: $s" }
    Copy-Item $s $dist -Force
}
foreach ($t in Get-ChildItem $dist -Filter *.txt) {
    $s = [System.IO.File]::ReadAllText($t.FullName, [System.Text.Encoding]::UTF8)
    $s = ($s -replace "`r`n","`n") -replace "`n","`r`n"
    [System.IO.File]::WriteAllText($t.FullName, $s, (New-Object System.Text.UTF8Encoding($true)))
}
Write-Host ("staged {0} doc(s): {1}" -f $docs.Count, ($docs -join ", "))

# 4) assemble release\ with versioned names
$rel = Join-Path $root "release"
New-Item -ItemType Directory -Path $rel -Force | Out-Null
Get-ChildItem $rel -File -ErrorAction SilentlyContinue | ForEach-Object { [System.IO.File]::Delete($_.FullName) }
# also clear any leftover staging folders from previous runs (ValeraScreenshot_vX.Y)
Get-ChildItem $rel -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'ValeraScreenshot_v*' } | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

# NO PORTABLE ZIP. Owner's decision 2026-07-29: the portable build is discontinued.
# The dist\ folder is still assembled - it is what the installer payload and the docs come
# from - but it is no longer shipped as an archive of its own.
Copy-Item $setup (Join-Path $rel ("ValeraScreenshotSetup_v{0}.exe" -f $ver)) -Force
# copy the plain-text docs (manual/readme) loosely into release too (names may be Cyrillic -> use wildcard)
foreach ($d in $docs) { Copy-Item (Join-Path $dist $d) $rel -Force }   # ALL shipping docs, not only *.txt
Copy-Item (Join-Path $root "ValeraScreenshotCodeSign.cer") $rel -Force -ErrorAction SilentlyContinue

# 5) ONE all-in-one archive (installer + portable exe + all docs + cert) under a clean top-level
#    folder - the single file to forward. Wrapping the .exe in a .zip also gets it past messengers
#    / mail filters that block raw executables.
$stage = Join-Path $rel ("ValeraScreenshot_v{0}" -f $ver)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item $setup (Join-Path $stage ("ValeraScreenshotSetup_v{0}.exe" -f $ver)) -Force
Copy-Item $exe   (Join-Path $stage "ValeraScreenshot.exe") -Force
Get-ChildItem $dist -Filter *.txt | ForEach-Object { Copy-Item $_.FullName $stage -Force }
Copy-Item (Join-Path $root "ValeraScreenshotCodeSign.cer") $stage -Force -ErrorAction SilentlyContinue
$allZip = Join-Path $rel ("ValeraScreenshot_v{0}.zip" -f $ver)
if (Test-Path $allZip) { Remove-Item $allZip -Force }
Compress-Archive -Path $stage -DestinationPath $allZip -Force
Remove-Item $stage -Recurse -Force

# 6) update manifest (latest.txt): version + sha256 + release notes for THIS version.
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
$exeUrl = "https://github.com/$GitHubRepo/releases/latest/download/ValeraScreenshot.exe"
# pull the notes block for $ver from RELEASE_NOTES.txt (newlines -> literal \n; the updater decodes them)
$notesFile = Join-Path $root "RELEASE_NOTES.txt"
$noteLines = @()
if (Test-Path $notesFile) {
  $inBlock = $false
  foreach ($ln in (Get-Content $notesFile -Encoding UTF8)) {
    if ($ln.Trim() -eq $ver) { $inBlock = $true; continue }
    if ($inBlock) {
      if ($ln.Trim() -match '^\d+\.\d+(\.\d+)?$') { break }   # next version header ends the block
      if ($ln.TrimStart().StartsWith('#')) { continue }
      $noteLines += $ln
    }
  }
}
while ($noteLines.Count -gt 0 -and $noteLines[0].Trim()  -eq '') { $noteLines = $noteLines[1..($noteLines.Count-1)] }
while ($noteLines.Count -gt 0 -and $noteLines[-1].Trim() -eq '') { $noteLines = $noteLines[0..($noteLines.Count-2)] }
$notes = if ($noteLines.Count -gt 0) { ($noteLines -join '\n') } else { "VALERASCREENSHOT $ver" }
$manifest = @(
  "version=$ver",
  "url=$exeUrl",
  "sha256=$sha",
  "notes=$notes"
) -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $rel "latest.txt"), $manifest + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
# the bare signed exe to attach to the GitHub release (its sha256 matches latest.txt above)
Copy-Item $exe (Join-Path $rel "ValeraScreenshot.exe") -Force

Write-Host ""
Write-Host ("=== RELEASE v{0} -> {1} ===" -f $ver, $rel)
Get-ChildItem $rel | Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB,1)}} | Format-Table -AutoSize
