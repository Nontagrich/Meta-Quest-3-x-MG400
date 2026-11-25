@echo off
powershell.exe -NoLogo -NoProfile -File "%~dp0Start-MG400Bridge.ps1" %*
if errorlevel 1 pause
