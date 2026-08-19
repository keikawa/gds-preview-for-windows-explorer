@echo off
setlocal
set "INSTALL_SCRIPT=%~dp0install.ps1"
if not exist "%INSTALL_SCRIPT%" set "INSTALL_SCRIPT=%~dp0scripts\install.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%INSTALL_SCRIPT%"
if errorlevel 1 (
  echo.
  echo Installation failed. See the message above.
  pause
  exit /b 1
)
echo.
echo Installation completed. Close all Explorer windows and reopen Explorer.
pause
