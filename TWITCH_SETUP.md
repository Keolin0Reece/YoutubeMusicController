# Twitch chat connection setup

The controller receives Twitch chat through an EventSub WebSocket. It does not
need a public URL, webhook, cloud service, or port forwarding.

## 1. Register a Twitch application

Open the Twitch Developer Console and create an application with this OAuth
redirect URL:

```text
http://localhost:5161/api/twitch/callback
```

Choose **Chat Bot** as the application category.

## 2. Store the credentials locally

From the repository root, run these commands with the values from Twitch:

```powershell
dotnet user-secrets set "Twitch:ClientId" "YOUR_CLIENT_ID" --project .\YoutubeMusicController.Api
dotnet user-secrets set "Twitch:ClientSecret" "YOUR_CLIENT_SECRET" --project .\YoutubeMusicController.Api
```

User-secrets are stored outside the repository. Never add the client secret or
OAuth tokens to `appsettings.json`, a committed `.env` file, or source code.

## 3. Run and connect

Run the API and frontend normally, then select **Connect Twitch** in the local
controller. Twitch will request permission to read and write chat using the
signed-in broadcaster account.

Once connected, the controller recognizes these Twitch chat commands:

```text
!song <title or artist>
!sr <title or artist>
!nowplaying
!np
!queue
```

Song requests are searched and added to the end of the queue. A requested song
cannot be queued again while it is waiting or playing. Its 10-minute repeat
timer starts when that queued copy finishes or is skipped. The timer is held in
memory and resets whenever the API stops. It applies to tracks added through
Twitch or the controller dashboard; tracks added directly inside YTMD are not
tracked.

The repeat window can be changed under `QueuePolicy` in `appsettings.json`.
Viewer cooldowns and maximum query length can be changed under
`Twitch:Commands`.
