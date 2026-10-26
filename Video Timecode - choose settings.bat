@echo off
powershell -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0VideoTimecode.ps1" %*
pause
