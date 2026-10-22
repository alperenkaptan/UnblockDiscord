# GitHub repository metadata

These values are ready for the repository's **About** panel. GitHub does not read description/topics from the `.csproj`; set them in the repository UI.

Repository: https://github.com/alperenkaptan/UnblockDiscord

**Description**

Discord-only DPI bypass for Windows in Turkey / Türkiye. ByeDPI + ProxiFyre, per-connection DNS correction, Start/Stop tray app. Tested on Türk Telekom.

**Topics**

```text
discord discord-unblock turkey turkiye dpi-bypass anti-censorship byedpi proxifyre windows winforms dotnet csharp system-tray
```

**Website**

```text
https://github.com/alperenkaptan/UnblockDiscord#readme
```

The executable's Product, Description, Author, Version and RepositoryUrl metadata are in `UnblockDiscord.csproj`. The EXE, form and tray use `assets/unblock-discord.ico`. Edit `assets/unblock-discord.svg` and the matching geometry in `tools/New-AppIcon.ps1` to revise the icon; run the script on Windows to regenerate the PNG/ICO.

After pushing, package the whole published folder and create a GitHub Release so users have an actual download. The README links to the Releases list and does not assume a release already exists. Keep the upstream licenses and source references with distributed engines.
