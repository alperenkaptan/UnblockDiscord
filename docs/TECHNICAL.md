# Unblock Discord

A small .NET 10 x64 WinForms Start/Stop app for the default Windows Discord installation.

## Use

Build `UnblockDiscord.slnx`, run `UnblockDiscord.exe`, accept Windows' administrator prompt, then click **Start**. Start checks public Discord endpoints before enabling interception. The form hides to the tray; click the tray icon to reopen it. Fully quit and reopen Discord after starting if it was already running, so existing connections are recreated. Click **Stop**, or close the form, to stop the bypass. Closing the window exits the app.

Keep the `engines` folder beside the executable. The Windows Packet Filter driver and the x64 Visual C++ runtime must be installed. They were already present on the development machine. On a different machine, use the [official ProxiFyre setup](https://github.com/wiresock/proxifyre/releases/tag/v2.6.4) for prerequisites; do not run its service or another bypass alongside this app. This app runs its own engine in console mode and does not register a Windows service.

## Scope and technique

- ProxiFyre matches the full `%LOCALAPPDATA%\Discord\` folder, including `Update.exe`, Squirrel helpers, `app-*\Discord.exe`, and other executables inside that folder. Folder matching survives version updates and does not match unrelated `Update.exe` processes. Browsers, games and other installation folders are not selected.
- A loopback SOCKS relay corrects poisoned DNS *inside selected HTTPS connections*: it reads the unencrypted TLS ClientHello's SNI, obtains an IPv4 answer from Cloudflare over HTTPS, and connects through ByeDPI. It forwards the original TLS handshake; Discord still validates the real server's certificate. Domain suffixes are in `DiscordTraffic.cs` and include updater, gateway, CDN and regional voice namespaces.
- ByeDPI applies TCP splitting/reordering and TLS record splitting, plus initial UDP fake packets. Start tries three bounded strategies against public gateway, updater and CDN endpoints. The default first strategy passed the tests on the user's Turk Telekom connection.
- UDP associations are relayed directly to ByeDPI, including voice transport. TCP/WebSocket signaling and updater traffic use the same folder rule. No authentication automation, account tokens, client patching, DLL injection, or certificate installation is used.
- Traffic still leaves through your own internet connection and public IP. There is no remote VPN/proxy server. Cloudflare receives DNS queries for the corrected Discord hostnames.

Adapter DNS, the Windows DNS cache, hosts file, system proxy, firewall rules and Discord files are never edited. Runtime configuration is written only beside our bundled ProxiFyre executable. Stop closes the engines and active relay connections; Discord reconnects using the original connection afterward. A kill-on-close Windows Job Object also terminates engines when the UI process is force-killed. The existing packet-filter driver remains installed in its previous state.

## Limits

This is a practical bypass, not a guarantee against every ISP filter. The SNI correction handles the poisoned address observed here (`195.175.254.2`); it cannot repair an NXDOMAIN response that prevents a connection from being attempted, or a hostname hidden by ECH. UDP endpoints supplied as IPs work without DNS correction. Direct IP blocking may require a tunnel, which this app does not provide. Only the default stable Discord folder is selected; an external browser used for OAuth, a separately downloaded installer, Canary/PTB, or helpers outside that folder remain direct.

There is some local proxy/driver overhead; it cannot be zero. There is no background scanning, scheduled task, startup registration, telemetry, or periodic connectivity polling. DNS results are cached for their TTL (up to five minutes). Full voice/video/screen-sharing behavior and account risk cannot be proved with unauthenticated endpoint tests. No application can promise Discord will never flag an account. ProxiFyre documents an IPv6 fragmentation limitation: fragmented IPv6 datagrams may pass directly; this is not an anonymity or leak-prevention tool.

## Verification

```
dotnet build UnblockDiscord.slnx -c Release
dotnet build tests/UnblockDiscord.Tests.csproj -c Release
tests/bin/Release/net10.0-windows/UnblockDiscord.Tests.exe
```

The dependency-free test runner checks domain boundaries, ClientHello parsing/truncation, 10,000 malformed inputs, folder-only rules, and job cleanup after parent termination.

Opt-in network tests (do not run alongside another bypass):

```
tests/bin/Release/net10.0-windows/UnblockDiscord.Tests.exe --network --driver
```

These route **only the test executable**, never Discord. They check public HTTPS endpoints, valid TLS after deliberately passing the poisoned destination IP, SOCKS UDP round trips, process isolation against a separate `curl.exe`, and restoration after shutdown. The driver test uses ProxiFyre's documented limited non-administrator mode; production uses the administrator manifest for reliable attribution. The separate-curl and restored-block assertions are specific to this blocked Turk Telekom test environment. No login or Discord voice channel is used.

## Pinned upstream dependencies

| Component | Source / license | Download archive SHA-256 |
| --- | --- | --- |
| ByeDPI 0.17.3 x64 | [hufrea/byedpi](https://github.com/hufrea/byedpi/tree/v0.17.3), MIT | `70D2C94147193CB915F9C6EB5144B8D404DACBCFA90BDA2383B6B211AFAFA456` |
| ProxiFyre 2.6.4 x64 | [wiresock/proxifyre](https://github.com/wiresock/proxifyre/tree/v2.6.4), AGPL-3.0 | `C7DA1DC5FDF9AAA7828FBF5C2076C4F612CBA7889DCAC2E3D06DC2E1DEE4C3DF` |

The ProxiFyre archive hash was checked against its official release sidecar. License copies are in each engine folder. The included ProxiFyre payload also contains its upstream dependencies (Newtonsoft.Json, NLog, Topshelf, socksify); retain their license obligations when redistributing. This project's application code has no license grant selected yet. Review the upstream distribution/source obligations before distributing a packaged app.

Research references: [WinDivert process attribution](https://github.com/basil00/WinDivert/blob/master/doc/windivert.html), [Squirrel install/update layout](https://github.com/Squirrel/Squirrel.Windows/blob/develop/docs/using/install-process.md), [Discord voice transport](https://docs.discord.com/developers/topics/voice-connections), and [SplitWire's use of ByeDPI with ProxiFyre](https://github.com/cagritaskn/SplitWire-Turkey).
