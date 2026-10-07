# Security policy

## Reporting a vulnerability

Please do not open a public issue containing credentials, tokens, exploit
details, or personal information. Use GitHub's private vulnerability reporting
feature for this repository when it is available.

Include the affected version, reproduction steps, expected impact, and any
suggested mitigation. Do not include live Twitch or Discord credentials.

## Supported versions

Security fixes are provided for the latest published release. Users should
upgrade to the newest signed installer before reporting an issue.

## Local security model

- The controller binds to `127.0.0.1` only.
- API and WebSocket requests require a random per-install key.
- OAuth and bot credentials are encrypted with current-user Windows DPAPI.
- Secrets are excluded from configuration files, logs, and source control.
