# Pulsar — Centralized NMS

**Pulsar** is the codename for this application; the .NET projects keep the `Nms.*` namespace.

Internal network monitoring: a small **site agent** (Linux VM or Raspberry Pi) probes devices over ICMP / TCP / HTTP and pushes results outbound over HTTPS to a **central service in Azure** (API + worker + Blazor portal). Phase 1 is uptime monitoring and alerting; Phase 2 adds SSH / HTTP access to on-site equipment through the same portal.

```
Site (LAN, behind NAT)                        Azure
┌──────────────────────────┐   HTTPS 443     ┌──────────────────────────────────────┐
│ nms-agent (systemd)      │ ─────────────▶  │ nms-api     ASP.NET Core + SignalR   │
│  ICMP / TCP / HTTP probes│   WebSocket     │ nms-portal  Blazor Server            │
│  VLAN-aware scheduler    │ ◀─────────────  │ nms-worker  alerts, watchdog, retain │
│  SQLite outbox           │                 │ PostgreSQL + TimescaleDB · Key Vault │
└──────────────────────────┘                 └──────────────────────────────────────┘
```

## Repository layout

| Path | What |
|---|---|
| `src/Nms.Shared` | DTOs, hub contracts, the check state machine |
| `src/Nms.Data` | EF Core model, migrations, Timescale setup SQL |
| `src/Nms.Agent` | The site agent (publishes as a single-file `nms-agent` for linux-x64 / linux-arm64) |
| `src/Nms.Api` | Agent enrollment + result ingest, management API, SignalR hubs |
| `src/Nms.Worker` | Notification dispatcher (email + Teams), agent-offline watchdog, retention |
| `src/Nms.Portal` | Blazor Server UI (Entra ID) |
| `tests/` | xUnit tests |
| `infra/` | Bicep for the Azure environment |
| `deploy/agent/` | `install.sh`, systemd unit, VLAN helper |
| `docs/` | Architecture, VLAN setup, agent install |

## Run locally

Prerequisites: .NET 8 SDK, Docker.

```bash
docker compose up -d                      # Postgres 16 + TimescaleDB on localhost:5432
dotnet run --project src/Nms.Api          # https://localhost:5001  (Swagger at /swagger)
dotnet run --project src/Nms.Worker
dotnet run --project src/Nms.Portal       # https://localhost:5003
```

In `Development` both API and portal run with `Auth:Mode = Development`: every request is an admin named `dev`, no Entra ID needed. Migrations and the Timescale hypertable are applied automatically when the API starts.

Run an agent against the local API (any Linux box or WSL):

1. In the portal: Sites → create a site → Add agent → copy the one-time token.
2. On the agent host:
   ```bash
   dotnet publish src/Nms.Agent -c Release -r linux-x64 -o out
   sudo setcap cap_net_raw+ep out/nms-agent
   NMS_ApiBaseUrl=https://<your-pc>:5001 NMS_EnrollmentToken=<token> NMS_StateDir=/tmp/nms \
     NMS_DangerousAcceptAnyServerCertificate=true out/nms-agent
   ```
   Production installs use `deploy/agent/install.sh` with `/etc/nms-agent/agent.json` (see `src/Nms.Agent/agent.example.json`).

## How it works

- **Enrollment**: an admin creates an agent in the portal and gets a 24-hour single-use token. The agent posts it once and receives a per-agent secret (PBKDF2-hashed server side). Tokens and secrets can be re-issued or revoked per agent.
- **Transport**: one outbound SignalR connection for config pushes, commands (run check now, discovery, update) and heartbeats; results are uploaded in batches over plain HTTPS. Only outbound 443 is required.
- **Store and forward**: results are written to a local SQLite outbox and deleted only after the API acknowledges them, so a WAN outage loses nothing (up to 500k results).
- **State machine**: `Up → Degraded → Down` after `FailureThreshold` consecutive failures, `Up` again on the first success. Incidents open on Down and close on recovery. Replayed old results never flap current state.
- **Agent offline is its own alert**: the worker marks an agent Offline after 3 missed heartbeats and opens a single "site unreachable" incident. Device incidents raised while the agent is offline, during a maintenance window, or for non-critical devices are recorded but not notified.
- **Notifications**: incidents are batched into one email / Teams card per channel per minute; recoveries are sent too. Rules route by site, tag or globally, with per-rule cooldown.
- **Multi-VLAN**: *Routed* mode probes other VLANs via the gateway. *Trunked* mode gives the agent 802.1Q sub-interfaces (`eth0.10`, …); each probe binds to the interface whose subnet or VLAN id matches the device, and discovery can sweep a VLAN at L2. See `docs/vlan-setup.md`.
- **History**: `check_results` is a Timescale hypertable (1-day chunks, compressed after 7 days, dropped after 90). The portal charts come from a 5-minute continuous aggregate.
- **Self-update**: Settings → Agent releases → *Publish from GitHub release* reads a CI-built release and pushes `UpdateAvailable` to all connected agents (offline ones get it at their next heartbeat); each verifies the SHA-256 and restarts via systemd.

## Entra ID setup (production)

1. App registration **nms-api**: expose scope `access_as_user`; define app roles `Nms.Admin`, `Nms.Operator`, `Nms.Viewer`; assign users/groups. Roles are defined here only: the portal reads them from the API access token, so nothing needs to be assigned on the portal registration.
2. App registration **nms-portal**: web redirect `https://<portal>/signin-oidc`, client secret in Key Vault, API permission to `nms-api/access_as_user`.
3. Configure `AzureAd` sections in `src/Nms.Api/appsettings.json` and `src/Nms.Portal/appsettings.json`, and set `Api:Scope` on the portal to `api://<nms-api-client-id>/access_as_user`.

## Tests

```bash
dotnet test Nms.sln
```

## Phase 2 (planned)

Azure Relay Hybrid Connections per site; agent terminates SSH (SSH.NET) and streams a PTY to an xterm.js page; YARP reverse-proxies device web UIs through the agent, with per-device subdomains for UIs that break under path prefixes. Session audit to Blob.
