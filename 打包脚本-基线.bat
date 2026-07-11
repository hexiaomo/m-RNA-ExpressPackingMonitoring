@echo off
setlocal

chcp 65001 >nul
cd /d "%~dp0"

echo Before packaging, complete RELEASE_CHECKLIST.md.
choice /C YN /N /M "Confirm all manual core business and recovery checks are complete? [Y/N]: "
if errorlevel 2 exit /b 1

pwsh -NoProfile -ExecutionPolicy Bypass -File "Tools\Publish-CleanPackage.ps1" -DisablePatch -ConfirmManualCoreChecks
exit /b %ERRORLEVEL%
