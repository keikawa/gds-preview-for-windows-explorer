@echo off
setlocal
set "UNINSTALL_SCRIPT=%~dp0uninstall.ps1"
if not exist "%UNINSTALL_SCRIPT%" set "UNINSTALL_SCRIPT=%~dp0scripts\uninstall.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%UNINSTALL_SCRIPT%"
if errorlevel 1 (
  echo.
  echo Uninstallation failed. See the message above.
  pause
  exit /b 1
)
echo.
echo Uninstallation completed.
pause
