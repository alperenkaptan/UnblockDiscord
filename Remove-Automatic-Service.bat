@echo off
setlocal
title Unblock Discord - Remove Automatic Service
set "UD_LAUNCHER=%~dp0tools\Remove-Service-Launcher.ps1"
set "UD_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "UD_POWERSHELL=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
cd /d "%SystemRoot%"
"%UD_POWERSHELL%" -NoProfile -ExecutionPolicy Bypass -File "%UD_LAUNCHER%"
exit /b %errorlevel%
