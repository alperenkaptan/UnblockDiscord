@echo off
setlocal
title Unblock Discord - Check Automatic System
set "UD_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "UD_POWERSHELL=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%UD_POWERSHELL%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Check-Automatic-System.ps1"
exit /b %errorlevel%
