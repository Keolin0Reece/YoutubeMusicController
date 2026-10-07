# Discord bot setup

The Discord bot runs inside the existing `YoutubeMusicController` Windows
Service and uses slash commands. It does not require a public web server,
Interactions Endpoint URL, port forwarding, or the Discord application's
Public Key.

## 1. Create the bot and invite it

In the Discord Developer Portal, open **Youtube Music Controller** and:

1. Open **Bot**, create the bot user if needed, and reset/copy its bot token.
2. Open **Installation** or **OAuth2 > URL Generator**.
3. Select the `bot` and `applications.commands` scopes.
4. Grant **View Channels** and **Send Messages**.
5. Use the generated URL to install the bot in your Discord server.

Do not commit, paste into chat, or otherwise share the bot token. The
Application ID and Public Key are not secrets, but the bot token is.

## 2. Save the local configuration

After publishing and installing the controller, right-click its tray icon and
choose **Configure Discord**. Enter:

- the numeric Application ID from **General Information**;
- your development server's Guild ID, or leave it blank for global commands;
- the bot token from the **Bot** page.

The Guild ID is recommended initially because guild commands update
immediately. Enable Discord Developer Mode and right-click the server to copy
its ID.

The configuration is encrypted with Windows DPAPI and stored at:

```text
C:\ProgramData\YoutubeMusicController\discord-auth.dat
```

The setup restarts the controller if it is running. Its status is available at
`http://127.0.0.1:5161/api/discord/status`.

## 3. Commands

```text
/song query:<title and optional artist>
/nowplaying
/queue
```

Discord and Twitch share search ranking, serialized queue insertion, and the
same repeat protection. Discord also applies per-user and global request
cooldowns. Users with **Manage Server** bypass the request-rate cooldowns but
not repeat protection.

The Discord username configured as
`Discord:Commands:VideoRequesterUsername` may queue a closely matching video
result. Other Discord users remain limited to results classified as songs.
The comparison is case-insensitive and uses the account username rather than a
server nickname.

Ambiguous requests from the configured video requester can be pinned to a
specific upload with `QueuePolicy:PreferredVideos`.

Set `Discord:AllowedChannelIds` in `appsettings.json` to numeric channel IDs to
restrict commands to selected channels. An empty array allows every channel in
the configured server. When a Guild ID is configured, interactions from every
other server are rejected.
