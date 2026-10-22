[CmdletBinding()]
param([switch]$Elevated)
$ErrorActionPreference = 'Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try { $admin=([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
if (!$admin) {
    try {
        if ($Elevated) { throw 'Administrator approval was not granted.' }
        $command="& '" + $PSCommandPath.Replace("'", "''") + "' -Elevated"
        $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $child=Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -WindowStyle Normal `
            -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -Wait -PassThru
        exit $child.ExitCode
    } catch { Write-Host $_.Exception.Message -ForegroundColor Yellow; Read-Host 'Press Enter to close'; exit 1 }
}
try {
    $folder=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'UnblockDiscord'
    $service=Get-CimInstance Win32_Service -Filter "Name='UnblockDiscord'"
    $allProcesses=@(Get-CimInstance Win32_Process)
    $installed=@($allProcesses | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($folder+'\',[StringComparison]::OrdinalIgnoreCase) })
    $tcpRule=Get-NetFirewallRule -Name 'UnblockDiscord.Automatic.ProxiFyre.TCP' -ErrorAction SilentlyContinue
    $udpRule=Get-NetFirewallRule -Name 'UnblockDiscord.Automatic.ProxiFyre.UDP' -ErrorAction SilentlyContinue
    Write-Host 'UNBLOCK DISCORD - AUTOMATIC SYSTEM CHECK' -ForegroundColor Cyan
    [pscustomobject]@{
        ServicePresent=[bool]$service
        ServiceState=$(if($service){$service.State}else{'Absent'})
        Startup=$(if($service){$service.StartMode}else{'Absent'})
        InstalledFolder=(Test-Path -LiteralPath $folder)
        EventSource=(Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\UnblockDiscord')
        OwnedTcpRulePresent=[bool]$tcpRule
        OwnedUdpRulePresent=[bool]$udpRule
        OwnedTcpRuleEnabled=$(if($tcpRule){[string]$tcpRule.Enabled}else{'Absent'})
        OwnedUdpRuleEnabled=$(if($udpRule){[string]$udpRule.Enabled}else{'Absent'})
        InstalledProcesses=$installed.Count
        ProxiFyreProcesses=@($installed | Where-Object {$_.Name -eq 'ProxiFyre.exe'}).Count
        ByeDpiProcesses=@($installed | Where-Object {$_.Name -eq 'ciadpi.exe'}).Count
    } | Format-List
    if ($installed.Count) { $installed | Select-Object Name,ProcessId,SessionId | Format-Table -AutoSize }
    $configPath=Join-Path $folder 'service.json'
    if (Test-Path -LiteralPath $configPath) {
        $config=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $discord=([IO.Path]::GetFullPath($config.DiscordFolder)).TrimEnd('\')+'\'
        Write-Host "Watching: $discord"
        Write-Host ('Matching Discord processes: ' + @($allProcesses | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($discord,[StringComparison]::OrdinalIgnoreCase)}).Count)
    }
    if ($service -and $service.State -eq 'Running') {
        Write-Host 'Running means the watcher is up; confirm engine counts and recent events below.' -ForegroundColor Yellow
        if (!$tcpRule -or !$udpRule) { Write-Host 'The installed engine firewall rules are missing. Run the updated install BAT to repair them.' -ForegroundColor Yellow }
    }
    Write-Host
    Write-Host 'Recent service events:'
    $events=@(Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName='UnblockDiscord'} -MaxEvents 5 -ErrorAction SilentlyContinue)
    if ($events.Count) { $events | Select-Object TimeCreated,LevelDisplayName,Message | Format-List }
    else { Write-Host 'No service events found.' }
    Write-Host 'After removal: service/folder/source/owned rules should be absent (False), process counts 0.'
    Write-Host 'Historical event entries and shared prerequisites can remain after removal.'
} catch { Write-Host $_.Exception.Message -ForegroundColor Red }
Read-Host 'Press Enter to close this report'
