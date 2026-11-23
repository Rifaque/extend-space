@echo off
rem Installs ExtendSpace for the current user. Double-click this file after extracting the ZIP.
rem The PowerShell script it runs is beside it; read it first if you like.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-ExtendSpace.ps1" %*
if errorlevel 1 (
  echo.
  echo Installation did not finish. See the message above.
)
pause
