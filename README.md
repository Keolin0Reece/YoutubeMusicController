# YouTube Music Controller

YouTube Music Controller is a Windows tray application that connects YouTube
Music Desktop to Twitch chat, optional Discord slash commands, and a local
playback dashboard.

Viewers can request songs without having direct access to the computer running
YouTube Music. Only an explicit `!song` Twitch command starts a search;
ordinary chat messages and `!sr` are ignored.

## What you need

- Windows 10 or Windows 11 (64-bit)
- YouTube Music Desktop with its remote-control API available at
  `http://127.0.0.1:26538`
- A Twitch public-client application ID if you want Twitch integration
- Optionally, a Discord application and bot token

The release installer is self-contained. You do not need Visual Studio, Node.js,
PowerShell, or the .NET SDK.

## Install the application

1. Download `YoutubeMusicController-Setup-<version>.exe` and
   `SHA256SUMS.txt` from the latest GitHub release.
2. Because the installer is currently unsigned, Windows may display
   **Unknown publisher**. Only continue if you downloaded it from this
   repository's official Releases page and its checksum matches.
3. Run the installer.
4. Leave **Start YouTube Music Controller when I sign in** selected if you want
   the tray application to start automatically.
5. Optionally create a desktop shortcut, then finish the installation.

The application installs for the current Windows user and normally does not
need administrator permission.

### Verify the download

Open PowerShell in the folder containing the installer and run:

```powershell
Get-FileHash `
  .\YoutubeMusicController-Setup-1.0.0.exe `
  -Algorithm SHA256
```

Compare the displayed hash with the matching line in `SHA256SUMS.txt`. Do not
run the installer if the values differ.

## Prepare YouTube Music Desktop

1. Install and open YouTube Music Desktop.
2. Enable its remote-control/API feature if it is not already enabled.
3. Make sure the API is listening on `127.0.0.1` port `26538`.
4. Keep YouTube Music Desktop running while using the controller.

When everything is connected, the tray menu displays:

```text
Status: Running — YouTube Music connected
```

If it displays **waiting for YouTube Music**, start YouTube Music Desktop and
check its remote-control settings.

## Use the tray application

After installation, find the YouTube Music Controller icon in the Windows
notification area. It may be inside the hidden-icons menu.

- Double-click the icon to open the local playback dashboard.
- Right-click it to view status, start, stop, or restart the controller.
- Select **Settings…** to change Twitch or Discord configuration.
- Select **Connect Twitch** to authorize the Twitch account.
- Select **Exit** to stop both the controller and tray application.

The Settings window opens automatically the first time the application starts.
You can configure Twitch, Discord, both, or neither.

## Set up Twitch

Each installation should use a Twitch application owned by the person running
the controller. The Client ID is public; do not provide or store a Twitch
Client Secret.

### Create the Twitch application

1. Enable two-factor authentication on your Twitch account.
2. Open the [Twitch Developer Console](https://dev.twitch.tv/console/apps).
3. Select **Register Your Application**.
4. Enter a unique application name.
5. If a redirect URL is required, enter:

   ```text
   http://localhost:5161
   ```

6. Choose an appropriate category, such as a chatbot or application
   integration.
7. Configure the application as a **public client** if Twitch presents that
   option.
8. Create the application and copy its **Client ID**.

The controller uses Twitch's public Device Code Grant flow and requests only
the chat read/write permissions it needs. Twitch documents this flow at
[Getting OAuth Access Tokens](https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/).

### Connect the Twitch account

1. Right-click the tray icon and select **Settings…**.
2. Paste the Client ID into **Twitch Client ID** and select **Save**.
3. Right-click the tray icon and select **Connect Twitch**.
4. Your browser will open Twitch's activation page. Sign in and approve the
   request.
5. Return to the tray application and allow it a few seconds to connect.

If Twitch authorization expires or the application has not been used for an
extended period, select **Connect Twitch** again.

### Twitch commands

```text
!song <title or artist>
!nowplaying
!np
!queue
```

Examples:

```text
!song Africa Toto
!nowplaying
!queue
```

Only `!song` starts a song search. An ordinary message such as `Africa Toto`,
and alternative commands such as `!sr`, are ignored.

## Set up Discord (optional)

Discord support is disabled until you configure it. Each installation uses its
own bot token; this project does not ship a shared bot or token.

### Create and install the Discord bot

1. Open the [Discord Developer Portal](https://discord.com/developers/applications).
2. Select **New Application**, give it a name, and create it.
3. On **General Information**, copy the **Application ID**.
4. Open **Bot**, create the bot if requested, then generate or reset its token.
   Copy the token somewhere temporary and secure.
5. Open **Installation** or **OAuth2**, and create an installation URL with
   these scopes:
   - `bot`
   - `applications.commands`
6. Give the bot permission to **View Channels** and **Send Messages**.
7. Open the generated URL and add the bot to your Discord server.
8. In Discord, enable **Developer Mode** under Advanced settings.
9. Right-click your server and select **Copy Server ID**.

Discord's application-command documentation is available at
[Discord Application Commands](https://docs.discord.com/developers/docs/interactions/slash-commands).

### Connect Discord to the controller

1. Right-click the tray icon and select **Settings…**.
2. Select **Enable Discord**.
3. Enter the Application ID, Server ID in **Discord Guild ID**, and bot token.
4. Leave **Video requester username** blank for normal use. If set, that one
   Discord username is allowed more flexibility when matching close video
   search results.
5. Select **Save**. The controller restarts and registers the slash commands.

Providing a Guild ID is recommended because guild commands appear immediately
and remain limited to that server. If the Guild ID is omitted, commands are
registered globally and may take longer to appear.

### Discord commands

```text
/song query:<title and optional artist>
/nowplaying
/queue
```

For example, enter `/song`, then put `Africa Toto` in its `query` field.

## Dashboard

Double-click the tray icon to open the dashboard. It shows connection status,
the current track, upcoming queue entries, and recent Twitch messages. The
dashboard is local to the computer and is served from `127.0.0.1:5161`.

The tray application opens the dashboard with a generated access key. The key
is removed from the browser address after the page loads and is not intended to
be shared.

## Update or uninstall

To update, download and run the newer installer. It closes the running tray and
controller processes, replaces the application files, and preserves your local
settings and encrypted credentials.

To uninstall, use **Installed apps** in Windows Settings. The uninstaller asks
whether it should also remove settings, logs, and encrypted Twitch/Discord
credentials. Choose **No** if you plan to reinstall and want to keep them.

## Troubleshooting

### The tray icon is missing

Check the hidden-icons menu in the Windows notification area. You can also
start **YouTube Music Controller** from the Start menu.

### The controller is waiting for YouTube Music

- Start YouTube Music Desktop.
- Confirm its remote-control API is enabled.
- Confirm another program is not using port `26538`.
- Right-click the tray icon and select **Restart controller**.

### Twitch does not connect

- Confirm the Client ID was copied correctly.
- Confirm the Twitch application is configured as a public client.
- Select **Connect Twitch** again and finish the browser authorization.
- Confirm the authorizing Twitch account has access to the chat being used.

### Discord commands do not appear

- Confirm the bot was installed with the `bot` and `applications.commands`
  scopes.
- Confirm the Application ID, Guild ID, and token belong to the same bot.
- Confirm **Enable Discord** is selected.
- Restart the controller from the tray menu.
- Prefer a Guild ID while testing because global commands can take longer to
  appear.

### Find the logs

Settings, encrypted credentials, API keys, and logs are stored under:

```text
%LocalAppData%\YoutubeMusicController
```

Include relevant log messages when reporting a problem, but review them before
posting publicly. Never publish bot tokens, access tokens, API keys, or local
settings files.

## Privacy and security

- Twitch and Discord tokens are encrypted with Windows DPAPI for the signed-in
  Windows user.
- The Discord bot token is not written to `settings.json`.
- The dashboard API listens only on `127.0.0.1` and requires a random
  per-installation key.
- Logs use bounded rotation to prevent unlimited disk growth.
- No Twitch Client Secret is required or distributed.

See [SECURITY.md](SECURITY.md) for reporting vulnerabilities and
[PRIVACY.md](PRIVACY.md) for local data handling.

## Build from source

Maintainers and developers can build and test the project with:

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

Generated binaries are placed under `artifacts` and are not committed.

## License

Licensed under the MIT License. See [LICENSE](LICENSE).
