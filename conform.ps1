# conform.ps1 - shim. The checker itself lives in .standard\ (vendored, read-only by convention:
# edit it in D:\Soft\_studio and re-sync). One implementation, ever. See STANDARD.md sec.21.
# ASCII-only (STD-ENC-01).
& (Join-Path $PSScriptRoot ".standard\conform.ps1") -Root $PSScriptRoot @args
exit $LASTEXITCODE
