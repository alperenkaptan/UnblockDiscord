# Automatic Windows service

Publishing now produces two executables in the same folder:

- `UnblockDiscord.exe`: the existing manual Start/Stop tray app.
- `UnblockDiscord.Service.exe`: the optional automatic Windows service, with no UI.

Both reuse the same engines and published runtime DLLs. The service does not load WinForms. Use one mode at a time. The service and manual app use a machine-wide ownership semaphore and reject already-running bypass engines.

## Install for automatic boot startup

An actual Windows service needs one administrator installation step. It cannot safely run as LocalSystem from a user-writable portable folder. The installer copies the published folder to `%ProgramFiles%\UnblockDiscord`, protects it so only Administrators/SYSTEM can modify it, records the intended user's Discord folder, and registers automatic startup plus crash recovery. It leaves the portable copy usable separately.

Close manual Unblock Discord and other bypass tools. The easiest installation is to double-click **Install-Automatic-Service.bat** in the extracted published folder and accept the administrator prompt. It runs the installer, which starts the service through Windows, then shows a status report that closes after 10 seconds or any key (including Esc). Keep the complete folder together; the BAT uses the scripts in `tools`. It preserves your original Discord folder if UAC asks for a different administrator account.

For a command-line installation, from **64-bit Windows PowerShell 5.1, Run as Administrator**, inside the published folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-Service.ps1
```

If elevation uses a different administrator account, explicitly supply the intended user's default stable Discord folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-Service.ps1 -DiscordFolder 'C:\Users\YOUR-USER\AppData\Local\Discord'
```

The existing Windows Packet Filter (`ndisrd`) driver and x64 Visual C++ runtime must already be installed. The service runs as LocalSystem because the packet engine requires elevated attribution/driver access. It never launches Discord or executes binaries from the user's Discord folder as System. It only watches that folder's processes and routes their connections through protected engine binaries.

## Behavior and performance

1. Windows starts the service at boot; the service subscribes to `Win32_ProcessStartTrace` / `Win32_ProcessStopTrace` events and takes one initial process snapshot.
2. With Discord absent, only the service waits. ByeDPI, ProxiFyre, the local relay and network probes are off. There is no process enumeration timer.
3. Any process in the configured Discord folder, including `Update.exe`, Squirrel helpers and versioned executables, activates the bypass. Startup still verifies public Discord endpoints.
4. After the last matching process exits, a 10-second handover period keeps updates from repeatedly restarting engines. A new Discord process cancels the pending stop.
5. Startup failures retry with bounded backoff (15, 30, 60, then 120 seconds) only while Discord remains open. An idle service does not send probes. Unexpected engine exit also triggers a retry while Discord is present.
6. On resume, one snapshot resynchronizes the process set. Stopping the service, Windows shutdown or force-killing the service terminates its engine job and closes the relay.

Event-driven watching still consumes some RAM and processes Windows event notifications; no zero-overhead claim is made. There is also a startup race: Discord can issue its first request before event delivery and the engine probes finish. Discord may retry, or you may need to quit and reopen it after activation. A launcher that starts the bypass *before* Discord would avoid that race, but would not cover arbitrary shortcuts.

This installer targets **one selected user's default stable Discord installation**. It is not a multi-user service manager. The GUI's Stop button controls only its own manual instance; use Windows service controls for automatic mode. A running service intentionally remains independent of closing the manual GUI.

## Manage or remove

Run in an Administrator PowerShell:

```powershell
Get-Service UnblockDiscord
Stop-Service UnblockDiscord
Start-Service UnblockDiscord
```

To disable boot startup as well as stop the service:

```powershell
Stop-Service UnblockDiscord
Set-Service UnblockDiscord -StartupType Disabled
```

For full removal, double-click **Remove-Automatic-Service.bat** and accept the administrator prompt. It stops and unregisters our service, removes automatic startup/recovery settings, closes installed copies of our processes, deletes the verified `%ProgramFiles%\UnblockDiscord` directory (including configuration and engines), and unregisters our Application event-log source. Its report closes after 10 seconds or any key, including Esc. The launcher hands off the removal code in memory, so it can also remove its own installed copy.

Or run the uninstaller directly as Administrator:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Uninstall-Service.ps1
```

Uninstall preserves your extracted ZIP/portable downloads, historical Application event-log entries, Discord, other services, and the pre-existing Windows Packet Filter / Visual C++ runtime. It removes only the `UnblockDiscord` service and our verified installed directory; it never uninstalls ProxiFyreService, ndisrd or other shared services. If ownership cannot be established or a junction/symlink is present, it refuses file deletion and reports the reason. Older installs without an ownership marker can be removed while their original service registration still identifies the folder; new installs also support cleanup of marked leftover folders after service registration is already gone. No system proxy, DNS, hosts, firewall or Discord file changes need reversing.

Read service state transitions and errors in **Event Viewer → Windows Logs → Application → Source: UnblockDiscord**. A Windows service's Running state means its watcher is running, not necessarily that a bypass strategy succeeded. For an interactive diagnostic run from an installed/configured folder, stop the Windows service first and run `UnblockDiscord.Service.exe --console` as Administrator.

To update, stop the service and run the installer from a newly extracted publish folder. It verifies the existing registered path/permissions before replacing files.
