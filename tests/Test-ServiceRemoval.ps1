[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ProjectDirectory)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('UnblockDiscord-removal-test-' + [Guid]::NewGuid().ToString('N'))
$root = Join-Path $fixture 'UnblockDiscord'
[IO.Directory]::CreateDirectory($root) | Out-Null
$source = [IO.File]::ReadAllText((Join-Path $ProjectDirectory 'tools\Uninstall-Service.ps1'))
$source = [regex]::Replace($source, '(?m)^#requires[^\r\n]*', '')
$source = $source.Replace("[Environment]::GetFolderPath('ProgramFiles')", "'" + $fixture.Replace("'", "''") + "'")
$source = $source.Replace('[Diagnostics.EventLog]::SourceExists($serviceName)', '$true')
$source = $source.Replace("[Diagnostics.EventLog]::LogNameFromSourceName(`$serviceName, '.')", "'Application'")
try {
    foreach ($case in @('normal','wrong-service','orphan-unmarked','orphan-marked','whatif','junction','wrong-firewall')) {
        $marker = Join-Path $root '.unblockdiscord-install.json'
        if (Test-Path -LiteralPath $marker) { [IO.File]::Delete($marker) }
        if ($case -eq 'orphan-marked') {
            [IO.File]::WriteAllText($marker, (@{Product='UnblockDiscord.AutomaticService';InstallDirectory=$root} | ConvertTo-Json))
        }
        if ($case -eq 'junction') {
            [IO.Directory]::CreateDirectory((Join-Path $fixture 'outside')) | Out-Null
            New-Item -ItemType Junction -Path (Join-Path $root 'redirect') -Target (Join-Path $fixture 'outside') | Out-Null
        }
        # All mutating Windows operations are mocked inside this isolated module.
        # No service is stopped/deleted and no install directory is actually removed.
        $module = New-Module -AsCustomObject -ArgumentList $root,$source,$case -ScriptBlock {
            param($root,$source,$case)
            $script:root=$root; $script:source=$source; $script:case=$case
            $script:serviceExists=$case -notlike 'orphan-*'
            $script:filesRemoved=$false
            $script:actions=[Collections.Generic.List[string]]::new()
            function Get-CimInstance {
                param($ClassName,$Filter)
                if ($ClassName -eq 'Win32_Process') {
                    return @(
                        [pscustomobject]@{Name='ProxiFyre.exe';ProcessId=111;ExecutablePath=(Join-Path $script:root 'engines\proxifyre\ProxiFyre.exe')},
                        [pscustomobject]@{Name='ProxiFyre.exe';ProcessId=222;ExecutablePath='C:\OtherApp\ProxiFyre.exe'}
                    )
                }
                if ($script:serviceExists) {
                    $path = if ($script:case -eq 'wrong-service') { '"C:\OtherApp\Other.exe"' } else { '"' + (Join-Path $script:root 'UnblockDiscord.Service.exe') + '"' }
                    return [pscustomobject]@{PathName=$path;ProcessId=0}
                }
            }
            function Get-Acl {
                param($LiteralPath)
                $acl=[Security.AccessControl.DirectorySecurity]::new()
                $acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
                return $acl
            }
            function Get-NetFirewallRule {
                param($Name,$PolicyStore,$ErrorAction)
                return [pscustomobject]@{Name=$Name;Group='Unblock Discord Automatic'}
            }
            function Get-NetFirewallApplicationFilter {
                param([Parameter(ValueFromPipeline=$true)]$InputObject)
                process {
                    $path=if($script:case -eq 'wrong-firewall'){'C:\OtherApp\ProxiFyre.exe'}else{Join-Path $script:root 'engines\proxifyre\ProxiFyre.exe'}
                    [pscustomobject]@{Program=$path}
                }
            }
            function Remove-NetFirewallRule {
                param([Parameter(ValueFromPipeline=$true)]$InputObject)
                process {
                    if ($InputObject.Name -notin @('UnblockDiscord.Automatic.ProxiFyre.TCP','UnblockDiscord.Automatic.ProxiFyre.UDP')) { throw 'Unrelated firewall rule selected.' }
                    $script:actions.Add('remove-rule-'+$InputObject.Name)
                }
            }
            function Test-Path {
                param($LiteralPath)
                if ($LiteralPath -eq $script:root -and $script:filesRemoved) { return $false }
                Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath
            }
            function Get-Service {
                param($Name)
                $object=[pscustomobject]@{Status='Running'}
                $object | Add-Member ScriptMethod Stop { $script:actions.Add('stop-service') }
                $object | Add-Member ScriptMethod WaitForStatus { param($status,$timeout) }
                $object | Add-Member ScriptMethod Dispose { }
                return $object
            }
            function sc.exe {
                $script:actions.Add('delete-service')
                $script:serviceExists=$false
                $global:LASTEXITCODE=0
            }
            function Get-Process {
                param($Id,$ErrorAction)
                if ($Id -ne 111) { throw 'An unrelated process was selected for termination.' }
                $object=[pscustomobject]@{}
                $object | Add-Member ScriptMethod Kill { $script:actions.Add('kill-owned-process') }
                $object | Add-Member ScriptMethod WaitForExit { param($timeout) return $true }
                $object | Add-Member ScriptMethod Dispose { }
                return $object
            }
            function Remove-Item {
                param($LiteralPath,[switch]$Recurse,[switch]$Force)
                if ($LiteralPath -ne $script:root) { throw 'Deletion escaped the verified install folder.' }
                $script:actions.Add('remove-install-directory')
                $script:filesRemoved=$true
            }
            function Remove-EventLog { param($Source) if ($Source -ne 'UnblockDiscord') { throw 'Wrong event source.' }; $script:actions.Add('remove-event-source') }
            function Invoke-Case {
                $failed=$false
                try {
                    $operation=[scriptblock]::Create($script:source)
                    if ($script:case -eq 'whatif') { & $operation -WhatIf } else { & $operation }
                } catch { $failed=$true }
                if ($script:case -in @('wrong-service','orphan-unmarked','junction','wrong-firewall')) {
                    if (!$failed -or $script:actions.Count) { throw 'Unowned installation was not rejected before mutation.' }
                } elseif ($script:case -eq 'whatif') {
                    if ($failed -or $script:actions.Count) { throw 'WhatIf performed a mutation.' }
                } else {
                    if ($failed -or !$script:actions.Contains('remove-install-directory')) { throw 'Owned files were not selected for cleanup.' }
                    if (!$script:actions.Contains('kill-owned-process')) { throw 'Installed process cleanup was not exercised.' }
                    if (@($script:actions | Where-Object {$_ -like 'remove-rule-*'}).Count -ne 2) { throw 'Owned firewall rules were not removed.' }
                    if ($script:case -eq 'normal' -and !$script:actions.Contains('delete-service')) { throw 'Owned service was not selected for removal.' }
                    if ($script:case -eq 'orphan-marked' -and $script:actions.Contains('delete-service')) { throw 'Unrelated service was touched during orphan cleanup.' }
                }
            }
            Export-ModuleMember -Function Invoke-Case
        }
        $module.'Invoke-Case'()
        Write-Host "PASS Removal safety case: $case"
    }
} finally {
    $junction=Join-Path $root 'redirect'
    if ([IO.Directory]::Exists($junction)) { [IO.Directory]::Delete($junction) }
    $outside=Join-Path $fixture 'outside'
    if ([IO.Directory]::Exists($outside)) { [IO.Directory]::Delete($outside) }
    $marker=Join-Path $root '.unblockdiscord-install.json'
    if (Test-Path -LiteralPath $marker) { [IO.File]::Delete($marker) }
    [IO.Directory]::Delete($root)
    [IO.Directory]::Delete($fixture)
}
