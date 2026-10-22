[CmdletBinding()]
param(
    [switch]$Elevated,
    [string]$DiscordFolder = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Discord')
)
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
        if (![Console]::IsInputRedirected -and [Console]::KeyAvailable) {
            [Console]::ReadKey($true) | Out-Null
            break
        }
        Start-Sleep -Milliseconds 100
    }
    Write-Host
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $isAdmin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }

if (!$isAdmin) {
    try {
        if ($Elevated) { throw 'Administrator access was not granted.' }
        # Keep the original user's Discord folder even if UAC uses another account.
        # EncodedCommand and PowerShell single-quote escaping preserve spaces,
        # apostrophes, Unicode and shell metacharacters in both paths.
        $launcher = $PSCommandPath.Replace("'", "''")
        $target = $DiscordFolder.Replace("'", "''")
        $command = "& '$launcher' -Elevated -DiscordFolder '$target'"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $powershell = Join-Path $PSHOME 'powershell.exe'
        Write-Host 'Please accept the Windows administrator prompt to install the automatic service.'
        $child = Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Normal `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -Wait -PassThru
        exit $child.ExitCode
    }
    catch {
        Write-Host
        Write-Host 'Setup was not started. Administrator approval is required.' -ForegroundColor Yellow
        Write-Host $_.Exception.Message
        Close-Report
        exit 1
    }
}

$result = 0
try {
    $installer = Join-Path $PSScriptRoot 'Install-Service.ps1'
    if (!(Test-Path -LiteralPath $installer)) { throw 'tools\Install-Service.ps1 is missing. Extract the complete published ZIP before running this BAT.' }
    Write-Host 'Installing and starting Unblock Discord Automatic...' -ForegroundColor Cyan
    & $installer -DiscordFolder $DiscordFolder
    $service = Get-Service -Name UnblockDiscord
    $details = Get-CimInstance Win32_Service -Filter "Name='UnblockDiscord'"
    if ($service.Status -ne 'Running') { throw "The service did not stay running. Current state: $($service.Status). Check Event Viewer for details." }
    Write-Host
    Write-Host 'SETUP COMPLETE' -ForegroundColor Green
    Write-Host "Service:       $($service.DisplayName)"
    Write-Host "Status:        $($service.Status)"
    Write-Host "Startup:       $($details.StartMode)"
    Write-Host "Discord folder: $DiscordFolder"
    Write-Host 'Windows has started the service. It waits for Discord and activates the bypass when needed.'
    Write-Host 'Logs: Event Viewer > Windows Logs > Application > UnblockDiscord'
}
catch {
    $result = 1
    Write-Host
    Write-Host 'SETUP FAILED' -ForegroundColor Red
    Write-Host $_.Exception.Message
    Write-Host 'Install Windows Packet Filter and the x64 Visual C++ runtime if the report says they are missing.'
}
Write-Host
Close-Report
exit $result
