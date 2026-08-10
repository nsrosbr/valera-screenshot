# publish_tree.ps1 - разова публікація/синхронізація ДЖЕРЕЛЬНОГО дерева в GitHub через
# Contents API. Брат publish.ps1 (той возить релізні асети, цей - код і документи).
# Токен читається з _ghtoken.txt, як і в publish.ps1; у вивід він не потрапляє ніколи.
#
# СПИСОК ЯВНИЙ, не «все підряд»: правило STD-SEC-02 (ніякого git add -A) тут відтворено
# буквально - кожен шлях названо рукою, ignore-правила .gitignore звірено очима перед
# написанням цього файла (2026-08-10). Секрети (_ghtoken.txt, _codesign.pfx), приватні
# Screenshots\, робочі кадри пруф-гейта, бекапи старого піна і бінарники сюди НЕ входять.
#
# gate.yml іде ОСТАННІМ: push воркфлоу вмикає CI, і якби він ліг першим, кожен наступний
# коміт запускав би окремий прогін - а так спрацює один, на фінальному стані дерева.

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path -Parent $PSScriptRoot
$repo = 'nsrosbr/valera-screenshot'
$tok = (Get-Content (Join-Path $root '_ghtoken.txt') -Raw).Trim()
$hdr = @{ Authorization = "Bearer $tok"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'valera-studio' }

function PutFile([string]$rel) {
    $local = Join-Path $root ($rel -replace '/', '\')
    if (-not (Test-Path $local)) { Write-Host "MISS $rel"; return }
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($local))
    $uri = "https://api.github.com/repos/$repo/contents/$rel"
    $sha = $null
    try { $sha = (Invoke-RestMethod -Uri $uri -Headers $hdr -Method Get).sha } catch {}
    $body = @{ message = "Source tree (2.5.1): $rel"; content = $b64 }
    if ($sha) { $body.sha = $sha }
    try {
        Invoke-RestMethod -Uri $uri -Headers $hdr -Method Put -Body (ConvertTo-Json $body) -ContentType 'application/json' | Out-Null
        Write-Host "OK  $rel"
    } catch { Write-Host "ERR $rel :: $($_.Exception.Message)" }
}

$list = @(
    '.editorconfig', '.gitattributes', '.gitignore', 'CLAUDE.md', 'STRUCTURE.md', 'SIGNING.md',
    'CODE_OF_CONDUCT.md', 'CONTRIBUTING.md', 'app.manifest', 'app.ico', 'standard.bind.json', 'portable.txt',
    'build.ps1', 'build_setup.ps1', 'conform.ps1', 'test.ps1', 'verify.ps1', 'sign.ps1', 'package.ps1',
    'publish.ps1', 'release.ps1', 'install.ps1', 'uninstall.ps1', 'INSTALL.bat', 'UNINSTALL.bat',
    'README.md', 'README_EN.md',
    '.claude/settings.json', '.claude/commands/conform.md', '.claude/commands/triage.md', '.claude/commands/release.md',
    '.githooks/pre-commit',
    'setup/Setup.cs', 'setup/Uninstall.cs', 'setup/setup.manifest', 'setup/ValeraScreenshotCodeSign.cer',
    'tests/TestMain.cs', 'tests/corpus.tsv',
    'tools/Drive.cs', 'tools/FieldProbe.cs', 'tools/MakeIcon.cs', 'tools/ProofGate.cs', 'tools/mutate.ps1',
    'tools/newcert.ps1', 'tools/studio_percert.ps1', 'tools/FINISH_REBRAND.cmd', 'tools/publish_tree.ps1',
    'data/author.png', 'data/author_dark.png',
    'deploy/portable.txt', 'deploy/trust_cert.cmd',
    'docs/HANDOFF.md', 'docs/_MAINTAIN.md', 'docs/_MAINTENANCE_LOG.md', 'docs/STUDIO_REGISTRATION.md',
    'dist/VERSIONS.md'
)
Get-ChildItem (Join-Path $root 'src') -Filter '*.cs' -File | ForEach-Object { $list += ('src/' + $_.Name) }
Get-ChildItem (Join-Path $root 'tools\mutants') -File | ForEach-Object { $list += ('tools/mutants/' + $_.Name) }
Get-ChildItem (Join-Path $root '.standard') -Recurse -File | ForEach-Object { $list += ($_.FullName.Substring($root.Length + 1) -replace '\\', '/') }
$list += '.github/ISSUE_TEMPLATE/bug.yml', '.github/ISSUE_TEMPLATE/feature.yml',
         '.github/pull_request_template.md', '.github/workflows/gate.yml'

Write-Host "TOTAL: $($list.Count) files"
foreach ($f in $list) { PutFile $f }
Write-Host 'DONE. Source tree published.'
