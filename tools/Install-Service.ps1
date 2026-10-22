#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param([string]$DiscordFolder = (Join-Path $env:LOCALAPPDATA 'Discord'))
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or ![Environment]::Is64BitProcess) {
    throw 'Run this script in 64-bit Windows PowerShell 5.1 as Administrator.'
}
$serviceName = 'UnblockDiscord'
$source = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$destination = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'UnblockDiscord'
$binary = Join-Path $destination 'UnblockDiscord.Service.exe'
$binaryPath = '"' + $binary + '"'
$DiscordFolder = [IO.Path]::GetFullPath($DiscordFolder)
if (!(Test-Path -LiteralPath (Join-Path $source 'UnblockDiscord.Service.exe'))) {
    throw 'Run the copy of this script inside the published portable folder.'
}
if (!(Test-Path -LiteralPath (Join-Path $DiscordFolder 'Update.exe'))) {
    throw 'Discord Update.exe was not found. Pass -DiscordFolder with the intended user''s Discord installation folder.'
}
if (!(Get-Service ndisrd -ErrorAction SilentlyContinue)) { throw 'Install Windows Packet Filter first (ndisrd driver is missing).' }
if (!(Test-Path -LiteralPath (Join-Path $env:WINDIR 'System32\vcruntime140.dll'))) { throw 'Install the x64 Visual C++ runtime first.' }
if ($source -eq $destination) { throw 'For updates, run the installer from a newly extracted portable folder.' }

$existing = Get-CimInstance Win32_Service -Filter "Name='UnblockDiscord'"
if ($existing -and $existing.PathName -ne $binaryPath) { throw 'A different service already uses the UnblockDiscord name.' }
$trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
function Assert-Protected([IO.FileSystemInfo]$Item, [bool]$ParentChain = $false) {
    if ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not allowed: $($Item.FullName)" }
    $acl = Get-Acl -LiteralPath $Item.FullName
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw "Untrusted owner: $($Item.FullName)" }
    $dangerous = if ($ParentChain) { 0x500D0040 } else { 0x500D0156 }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $trusted -and
            -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
            ([long]$rule.FileSystemRights -band $dangerous)) { throw "Untrusted write permissions: $($Item.FullName)" }
    }
}
for ($parent = [IO.DirectoryInfo]::new([Environment]::GetFolderPath('ProgramFiles')); $parent; $parent = $parent.Parent) {
    Assert-Protected $parent $true
}
if (Test-Path -LiteralPath $destination) {
    if (!$existing) { throw 'The destination already exists without our service. Refusing to overwrite it.' }
    Assert-Protected (Get-Item -LiteralPath $destination)
    Get-ChildItem -LiteralPath $destination -Recurse -Force | ForEach-Object { Assert-Protected $_ }
    Stop-Service $serviceName
    (Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    if ($existing.ProcessId -ne 0) {
        $old = Get-Process -Id $existing.ProcessId -ErrorAction SilentlyContinue
        if ($old -and !$old.WaitForExit(30000)) { throw 'The previous service process has not exited.' }
    }
} else {
    # Create with the final protected ACL before copying executable files.
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;RC;;;OW)')
    [IO.Directory]::CreateDirectory($destination, $acl) | Out-Null
}
Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The published payload must not contain reparse points.' }
}
Get-ChildItem -LiteralPath $source -Recurse -Force | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The published payload must not contain reparse points.' }
}
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
@{ DiscordFolder = $DiscordFolder } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'service.json') -Encoding UTF8
& icacls.exe $destination /setowner '*S-1-5-32-544' /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect ownership of service files.' }
Assert-Protected (Get-Item -LiteralPath $destination)
Get-ChildItem -LiteralPath $destination -Recurse -Force | ForEach-Object { Assert-Protected $_ }
if (![Diagnostics.EventLog]::SourceExists($serviceName)) { New-EventLog -LogName Application -Source $serviceName }
if (!$existing) {
    New-Service -Name $serviceName -DisplayName 'Unblock Discord Automatic' -BinaryPathName $binaryPath `
        -StartupType Automatic -DependsOn @('Winmgmt', 'ndisrd') `
        -Description 'Waits for Discord process events; runs the Discord-only bypass while Discord is open.' | Out-Null
} else { Set-Service -Name $serviceName -StartupType Automatic }
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }
Start-Service $serviceName
(Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "Installed automatic service at $destination"
Write-Host "Watching: $DiscordFolder"
Write-Host 'The service is running. It waits without bypass engines until Discord is detected.'
