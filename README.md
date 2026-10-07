# YouTube Music Controller

A Windows tray application that connects YouTube Music Desktop to Twitch chat,
Discord slash commands, and a local playback dashboard.

## Requirements

- Windows 10 or Windows 11 (x64)
- YouTube Music Desktop with its remote-control API available at
  `http://127.0.0.1:26538`
- A Twitch public-client application ID for Twitch chat integration
- Optionally, a Discord application and bot token

The release installer is self-contained; end users do not need the .NET SDK,
Node.js, PowerShell, or administrator permission.

## Install

Download `YoutubeMusicController-Setup-<version>.exe` from the GitHub Releases
page and run it. Setup installs the application for the current Windows user,
adds a Start Menu shortcut, and can start the tray automatically at sign-in.

On first launch, the Settings window collects the public Twitch Client ID and
optional Discord bot configuration. Twitch authorization uses Twitch's public
device-code flow, so no Twitch Client Secret is stored or distributed.

Runtime settings, encrypted credentials, API keys, and logs are stored below:

```text
%LocalAppData%\YoutubeMusicController
```

Tokens are encrypted with Windows DPAPI for the signed-in user. The local API
listens only on `127.0.0.1` and requires a randomly generated per-install key.

## Commands

Twitch:

```text
!song <title or artist>
!nowplaying
!np
!queue
```

Only `!song` starts a Twitch song search. Ordinary chat messages and `!sr` are
ignored.

Discord:

```text
/song query:<title and optional artist>
/nowplaying
/queue
```

Discord is optional and disabled until it is configured in the tray Settings
window. Each installation uses the bot token supplied by that user; no Discord
token is included in this repository.

## Build and test

```powershell
dotnet test .\YoutubeMusicController.slnx -c Release
Push-Location .\YoutubeMusicController.Web
npm ci
npm run build
npm run lint
Pop-Location
.\scripts\Publish-Windows.ps1
```

To build the installer, install Inno Setup 6 and run:

```powershell
.\scripts\Build-Installer.ps1 -Version 1.0.0
```

Published binaries are generated under `artifacts` and are not committed.

## Security

Do not commit Twitch tokens, Discord bot tokens, certificates, private keys, or
local settings. See [SECURITY.md](SECURITY.md) for reporting vulnerabilities
and [PRIVACY.md](PRIVACY.md) for local data handling.

## License

Licensed under the MIT License. See [LICENSE](LICENSE).
