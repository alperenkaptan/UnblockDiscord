[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

function Close-Report {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $previous = -1
    while ($timer.Elapsed.TotalSeconds -lt 10) {
        $remaining = 10 - [int][Math]::Floor($timer.Elapsed.TotalSeconds)
        if ($remaining -ne $previous) {
            Write-Host -NoNewline "`rClosing in $remaining seconds. Press any key (including Esc) to close now.  "
            $previous = $remaining
        }
        if (![Console]::IsInputRedirected -and [Console]::KeyAvailable) { [Console]::ReadKey($true) | Out-Null; break }
        Start-Sleep -Milliseconds 100
    }
    Write-Host
}

try {
    $uninstaller = Join-Path $PSScriptRoot 'Uninstall-Service.ps1'
    if (!(Test-Path -LiteralPath $uninstaller)) { throw 'tools\Uninstall-Service.ps1 is missing. Extract the full ZIP first.' }
    # Run the removal code from memory in a new elevated console. The original
    # BAT/PowerShell can exit, allowing the installed folder to remove itself.
    $body = 'function Close-Report {' + ${function:Close-Report}.ToString() + "}`n"
    $body += @'
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $env:SystemRoot
Wait-Process -Id __CALLER_PID__ -Timeout 10 -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200
$result = 0
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $admin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
    if (!$admin) { throw 'Administrator approval is required to remove the service.' }
    Write-Host 'Removing Unblock Discord Automatic...' -ForegroundColor Cyan
    & {
__UNINSTALL_SCRIPT__
    }
    Write-Host
    Write-Host 'REMOVAL COMPLETE' -ForegroundColor Green
}
catch {
    $result = 1
    Write-Host
    Write-Host 'REMOVAL INCOMPLETE' -ForegroundColor Red
    Write-Host $_.Exception.Message
    Write-Host 'Close any installed Unblock Discord windows and retry. Details above describe anything left behind.'
}
Write-Host
Close-Report
exit $result
'@
    $body = $body.Replace('__CALLER_PID__', [string]$PID).Replace('__UNINSTALL_SCRIPT__', [IO.File]::ReadAllText($uninstaller))
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
    $powershell = Join-Path $PSHOME 'powershell.exe'
    Write-Host 'Please accept the administrator prompt. Removal will continue in its own report window.'
    Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Normal -WorkingDirectory $env:SystemRoot `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) | Out-Null
    exit 0
}
catch {
    Write-Host 'Removal was not started.' -ForegroundColor Yellow
    Write-Host $_.Exception.Message
    Close-Report
    exit 1
}
