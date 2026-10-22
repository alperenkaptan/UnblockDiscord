@echo off
setlocal
title Unblock Discord - Automatic Service Setup
set "UD_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "UD_POWERSHELL=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%UD_POWERSHELL%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Install-Service-Launcher.ps1"
exit /b %errorlevel%
