# release.ps1 - ONE command to ship an update: build + sign (app + installer), assemble the
# release folder and manifest, then publish it to GitHub Releases automatically.
# Usage:  bump Ver.cs, then run  .\release.ps1
# Requires _ghtoken.txt (a fine-grained GitHub token) once - see ONOVLENNYA.txt.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# THE GATE. STD-GATE-01. CLAUDE.md invariant #5 calls test.ps1 mandatory before any release,
# but nothing ever called it: the invariant was agent discipline only, and every release since
# the line began could have sailed straight past it. A procedural invariant a script bypasses
# is a hope, not a gate. This is the wiring.
& (Join-Path $root "test.ps1")
if ($LASTEXITCODE -ne 0) { throw "GATE FAILED (test.ps1 exit $LASTEXITCODE) - no release." }

& (Join-Path $root "package.ps1")
& (Join-Path $root "publish.ps1")
