# Windows Service and tray controller

The controller is installed as the manually started Windows Service
`YoutubeMusicController`. A separate WinForms tray application runs in the
signed-in user's desktop session and controls the service.

## Tray actions

- **Start controller** starts the Windows Service.
- **Stop controller** requests a graceful shutdown.
- **Kill controller** forcibly terminates a stuck service after confirmation.
- **Connect Twitch** opens the one-time Twitch authorization page.
- **Exit tray** closes only the tray application.
- **Stop controller and exit** gracefully stops the service before closing the
  tray application.

Start, stop, and kill require an Administrator confirmation from Windows. Kill
should only be used if the normal stop action does not work.

The tray icon starts automatically when the current user signs in. The service
uses the **Manual** startup type, so it does not run until requested.

If you close the tray application, start it again with:

```powershell
& 'C:\Program Files\YoutubeMusicController\Tray\YoutubeMusicController.Tray.exe'
```

This only restores the tray icon. It does not automatically start or stop the
controller service; use the tray menu for that.

## Publish and install an update

From the repository root:

```powershell
.\scripts\Publish-Windows.ps1
.\scripts\Install-Windows.ps1
```

The installer requests Administrator permission, preserves whether the service
was running, installs both applications below `C:\Program Files`, and refreshes
the current user's Startup shortcut.

To explicitly start the service after installation:

```powershell
.\scripts\Install-Windows.ps1 -StartService
```

Published files under `artifacts` are generated output and are not committed.

## Manual service commands

Run these commands from an Administrator PowerShell window:

```powershell
Start-Service YoutubeMusicController
Stop-Service YoutubeMusicController
Get-Service YoutubeMusicController
```

The service listens only on `http://127.0.0.1:5161`. Twitch credentials and
authorization remain encrypted under
`C:\ProgramData\YoutubeMusicController\twitch-auth.dat`.
