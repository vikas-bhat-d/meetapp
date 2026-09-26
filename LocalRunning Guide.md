# Local Running Guide

This guide runs the laptop-only local stack:

1. LiveKit server
2. ASP.NET Core application
3. Caddy HTTPS reverse proxy

Run the commands below in **PowerShell** from the repository root, the folder that contains `livekitmeet.sln`.

## 1. Install prerequisites

Install these tools once:

```powershell
winget install Microsoft.DotNet.SDK.8
winget install CaddyServer.Caddy
```

Install the LiveKit server separately and make sure `livekit-server.exe` is available in `PATH`. Verify all commands:

```powershell
dotnet --version
caddy version
livekit-server --help
```

Restore the .NET dependencies after cloning:

```powershell
dotnet restore .\livekitmeet.sln
```

## 2. Configure the laptop IP address

Find the laptop's LAN IPv4 address:

```powershell
ipconfig
```

Use the address of the Wi-Fi or Ethernet adapter, for example `192.168.1.6`.

### Required replacement in `infra/local/Caddyfile`

Open `infra/local/Caddyfile` and replace **only** the public address on the first line:

```text
https://192.168.1.6:8443 {
```

For example, if the laptop address is `192.168.1.25`, use:

```text
https://192.168.1.25:8443 {
```

The value passed as a complete endpoint is therefore:

```text
192.168.1.25:8443
```

Do **not** replace these local proxy targets in the Caddyfile:

```text
127.0.0.1:5189
127.0.0.1:7880
```

They mean the ASP.NET application and LiveKit server running on this same laptop.

### Required application URL check

The application tells the browser which LiveKit WebSocket URL to use. In `livekitmeet/appsettings.json`, make sure this value uses the same public address as Caddy:

```json
"Url": "wss://192.168.1.25:8443/livekit"
```

This is a configuration value, not a Caddy proxy target. If the laptop IP changes, update this URL as well or LiveKit calls will use the old address.

Keep these values unchanged for the laptop-only setup:

```json
"ApiKey": "devkey",
"ApiSecret": "secret"
```

They must match the credentials used by the LiveKit development server.

## 3. Start LiveKit

Open **PowerShell window 1** at the repository root and run:

```powershell
livekit-server --dev --bind 0.0.0.0
```

Leave this window running. The development server listens on port `7880`.

## 4. Start the ASP.NET application

Open **PowerShell window 2** at the repository root and run:

```powershell
dotnet run --project .\livekitmeet\livekitmeet.csproj --launch-profile http
```

Leave this window running. The application listens on `0.0.0.0:5189`.

## 5. Start Caddy

Open **PowerShell window 3** at the repository root and run:

```powershell
caddy run --config .\infra\local\Caddyfile
```

Caddy listens on `https://<laptop-ip>:8443` and routes:

| Public path | Local destination |
| --- | --- |
| `/` | ASP.NET Core at `127.0.0.1:5189` |
| `/livekit` | LiveKit at `127.0.0.1:7880` |

## 6. Open the application

Open this URL on the laptop:

```text
https://<laptop-ip>:8443
```

For the example address above:

```text
https://192.168.1.25:8443
```

Caddy uses a local development certificate. The browser may show a certificate warning the first time. Continue only for this local development setup.

## Troubleshooting

- If `livekit-server` is not recognized, add the directory containing `livekit-server.exe` to `PATH`, then open a new PowerShell window.
- If Caddy reports that a port is already in use, stop the process using port `8443`, `5189`, or `7880`.
- If the page opens but a call cannot connect, verify that `LiveKit:Url` in `livekitmeet/appsettings.json` matches the Caddy URL exactly and ends with `/livekit`.
- If the browser cannot reach the laptop from another device, allow TCP port `8443` through Windows Firewall. The laptop-only browser does not need ports `5189` or `7880` exposed.
- Stop each service with `Ctrl+C` in its own PowerShell window.