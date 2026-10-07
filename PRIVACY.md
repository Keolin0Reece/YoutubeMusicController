# Privacy

YouTube Music Controller runs locally on the user's Windows computer. It sends
requests only to services the user explicitly configures: YouTube Music
Desktop, Twitch, and optionally Discord.

The application stores local settings, encrypted authorization credentials,
and rotating diagnostic logs under `%LocalAppData%\YoutubeMusicController`.
It does not include analytics, advertising, telemetry, or a hosted account
service.

Twitch and Discord process data according to their own terms and privacy
policies. Chat messages needed to process commands may be held briefly in
memory and displayed in the local dashboard. Credentials are not intentionally
written to application logs.

Uninstalling the application leaves local settings and encrypted credentials
in place so an upgrade does not disconnect integrations. Users can delete the
`%LocalAppData%\YoutubeMusicController` directory after uninstalling to remove
that local data.
