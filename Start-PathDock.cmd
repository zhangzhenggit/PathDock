@echo off
cd /d "%~dp0"
if exist "dist\PathDock.exe" (
  start "" "dist\PathDock.exe"
) else (
  pwsh.exe -NoLogo -NoProfile -File "%~dp0build.ps1" -Run
)
