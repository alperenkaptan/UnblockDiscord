#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true)]
param()
$ErrorActionPreference = 'Stop'
$serviceName = 'UnblockDiscord'
$programFiles = [IO.Path]::GetFullPath([Environment]::GetFolderPath('ProgramFiles')).TrimEnd('\')
$destination = [IO.Path]::GetFullPath((Join-Path $programFiles 'UnblockDiscord'))
$directory = [IO.DirectoryInfo]::new($destination)
if ($directory.Parent.FullName -ne $programFiles -or $directory.Name -ne 'UnblockDiscord') {
    throw 'The removal target is not the expected Program Files\UnblockDiscord directory.'
}
$expected = '"' + (Join-Path $destination 'UnblockDiscord.Service.exe') + '"'
$service = Get-CimInstance Win32_Service -Filter "Name='UnblockDiscord'"
if ($service -and $service.PathName -ne $expected) {
    throw 'A different service uses this name/path. Refusing to remove it.'
}
$hasFiles = Test-Path -LiteralPath $destination
if ($hasFiles) {
    for ($ancestor = $directory.Parent; $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A parent of the install directory is a reparse point. Refusing deletion.' }
    }
    # Inspect each directory before descending, so junctions are never followed.
    $pending = [Collections.Generic.Stack[IO.FileSystemInfo]]::new()
    $pending.Push((Get-Item -LiteralPath $destination -Force))
    $trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    while ($pending.Count) {
        $item = $pending.Pop()
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to follow a reparse point: $($item.FullName)" }
        $acl = Get-Acl -LiteralPath $item.FullName
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw "Unexpected owner: $($item.FullName)" }
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $trusted -and
                -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
                ([long]$rule.FileSystemRights -band 0x500D0156)) { throw "Untrusted write permissions: $($item.FullName)" }
        }
        if ($item -is [IO.DirectoryInfo]) {
            foreach ($child in (Get-ChildItem -LiteralPath $item.FullName -Force)) { $pending.Push($child) }
        }
    }
    $markerPath = Join-Path $destination '.unblockdiscord-install.json'
    $ownsFiles = $false
    if (Test-Path -LiteralPath $markerPath) {
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        $ownsFiles = $marker.Product -eq 'UnblockDiscord.AutomaticService' -and $marker.InstallDirectory -eq $destination
    }
    # Older installations had no marker. Their verified SCM path proves ownership.
    if (!$ownsFiles -and !$service) { throw 'Cannot verify that the leftover folder belongs to our installer. No files were removed.' }
}
$firewallRules=@()
foreach ($protocol in @('TCP','UDP')) {
    $ruleName='UnblockDiscord.Automatic.ProxiFyre.'+$protocol
    $rule=Get-NetFirewallRule -Name $ruleName -PolicyStore PersistentStore -ErrorAction SilentlyContinue
    if (!$rule) { continue }
    $application=$rule | Get-NetFirewallApplicationFilter
    if ($rule.Group -ne 'Unblock Discord Automatic' -or $application.Program -ne (Join-Path $destination 'engines\proxifyre\ProxiFyre.exe')) {
        throw "The firewall rule does not belong to our installed engine and was preserved: $ruleName"
    }
    $firewallRules += $rule
}
if (!$PSCmdlet.ShouldProcess($destination, 'Stop and unregister UnblockDiscord, remove its event source, owned firewall rules and installed files')) { return }

if ($service) {
    $controller = Get-Service -Name $serviceName
    try {
        if ($controller.Status -ne 'Stopped') { $controller.Stop(); $controller.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    } finally { $controller.Dispose() }
    if ($service.ProcessId -ne 0) {
        $old = Get-Process -Id $service.ProcessId -ErrorAction SilentlyContinue
        if ($old) {
            try { if (!$old.WaitForExit(30000)) { throw 'The service process did not exit. Installed files were not deleted.' } }
            finally { $old.Dispose() }
        }
    }
    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not remove service registration.' }
}
$firewallRules | Remove-NetFirewallRule
if ($hasFiles) {
    # Close only our executables inside the verified installed directory.
    foreach ($process in (Get-CimInstance Win32_Process)) {
        if ($process.Name -notin @('UnblockDiscord.exe', 'UnblockDiscord.Service.exe', 'ProxiFyre.exe', 'ciadpi.exe')) { continue }
        if (!$process.ExecutablePath) { continue }
        $image = [IO.Path]::GetFullPath($process.ExecutablePath)
        if (!$image.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $owned = Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
        if ($owned) {
            try { $owned.Kill(); if (!$owned.WaitForExit(10000)) { throw "An installed process is still running: $image" } }
            finally { $owned.Dispose() }
        }
    }
    # Absolute target and complete non-reparse ownership tree were checked above.
    Remove-Item -LiteralPath $destination -Recurse -Force
    if (Test-Path -LiteralPath $destination) { throw 'Some installed files could not be removed.' }
}
if ([Diagnostics.EventLog]::SourceExists($serviceName)) {
    if ([Diagnostics.EventLog]::LogNameFromSourceName($serviceName, '.') -ne 'Application') {
        throw 'Unexpected event-log source registration. It was left untouched.'
    }
    Remove-EventLog -Source $serviceName
}
if (Get-CimInstance Win32_Service -Filter "Name='UnblockDiscord'") {
    throw 'Windows has marked the service for deletion. Close Services/Event Viewer and restart Windows if it remains listed.'
}
Write-Host 'Removed: UnblockDiscord service, automatic startup/recovery settings, installed configuration, engine files and app files.'
Write-Host 'Removed: UnblockDiscord Application event-log source. Historical log entries were preserved.'
Write-Host 'Preserved: Discord, extracted portable downloads, other services, Windows Packet Filter and Visual C++ runtime.'
Write-Host 'Removed our two engine-specific firewall rules. System DNS, proxy and hosts settings were never changed.'
