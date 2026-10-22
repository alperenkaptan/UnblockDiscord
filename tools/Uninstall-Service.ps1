#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$serviceName = 'UnblockDiscord'
$destination = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'UnblockDiscord'
$expected = '"' + (Join-Path $destination 'UnblockDiscord.Service.exe') + '"'
$service = Get-CimInstance Win32_Service -Filter "Name='UnblockDiscord'"
if (!$service) { Write-Host 'The UnblockDiscord service is not installed.'; return }
if ($service.PathName -ne $expected) { throw 'The registered service does not use our expected installation. Refusing to remove it.' }
Stop-Service $serviceName
(Get-Service $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
& sc.exe delete $serviceName | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not remove service registration.' }
Write-Host 'Automatic service stopped and unregistered. Network settings and the existing packet-filter driver are unchanged.'
Write-Host "Installed files remain at $destination; remove that folder manually if no longer needed."
