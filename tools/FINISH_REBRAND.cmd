@echo off
REM ============================================================================
REM  FINISH_REBRAND.cmd - ONE CLICK, owner-run. Finishes the rebrand:
REM    step 1: studio_percert.ps1 -Apply  (studio normalizer + rebuild, proves
REM            no file leaves the agreed core, auto-rollback pointer on failure)
REM    step 2: newcert.ps1 -Apply         (mints CN=Pavlo Isaiev, moves the pin,
REM            exports the .cer; refuses to run if step 1 did not land)
REM  No admin rights needed. Safe to re-run: both steps are idempotent.
REM ============================================================================
setlocal
echo === STEP 1/2: studio normalizer (studio_percert.ps1 -Apply) ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0studio_percert.ps1" -Apply
if errorlevel 1 (
  echo.
  echo STEP 1 FAILED - stopping. See the messages above; nothing else was touched.
  pause
  exit /b 1
)
echo.
echo === STEP 2/2: mint own certificate (newcert.ps1 -Apply) ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0newcert.ps1" -Apply
if errorlevel 1 (
  echo.
  echo STEP 2 FAILED - see the messages above; a backup path was printed there.
  pause
  exit /b 1
)
echo.
echo ============================================================
echo  ALL DONE. Tell the agent to finish its part:
echo  build -Dist with the new signature, gates, drop LS-DEV-05/06.
echo ============================================================
pause
