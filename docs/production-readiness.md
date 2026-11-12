# Production readiness (v1)

This is the v1 go-live gate of `docs/roadmap.md`; everything after go-live is planned there.

What has to be true before the first production site is monitored by Pulsar, split into what the code and
infrastructure already do, what is done per environment by hand, and what is still open. Tick items off here.

## Already in place

| Area | State |
|---|---|
| Agent transport | Outbound HTTPS/WebSocket only; per-agent secret, single-use 24 h enrolment token; re-enrols on rejected credentials when a token is configured |
| Agent resilience | SQLite outbox replays results after an outage; hub reconnect with backoff; systemd `Restart=always` |
| Agent updates | Portal → App settings → Agent releases → *Publish from GitHub release*; SHA-256 verified; failed version retried after 1 h; automatic rollback on crash loop or no hub connection (0.1.7+), shown in the portal |
| Alerting | Check state machine with failure threshold; agent-offline is a separate incident and suppresses device alerts; maintenance windows; email (ACS) and Teams channels; digest window; per-rule cooldown; flaps inside the digest window are never sent |
| Portal auth | Entra ID sign-in, roles from the API; Data Protection key ring persisted to Blob storage and wrapped by a Key Vault key, so sign-in survives restarts and replicas |
| Secrets | Key Vault only, read by the apps' managed identity; Bicep refuses to deploy without the four secret parameters |
| Data | Postgres Flexible 16 with Timescale hypertable; worker purges raw results after `RetentionDays` (90) and incidents after 365 days |
| Infra | Bicep for everything; `infra/main.prod.bicepparam` sizes Postgres GeneralPurpose with 35-day geo-redundant backups |

## Per-environment setup (manual, once)

- [ ] Resource group `rg-pulsar-prod` and a `pulsar-prod.env` kept in a password manager (see `docs/credential-rotation.md`).
- [ ] Two Entra app registrations for prod (API and portal), redirect URI on the portal's final hostname, API exposing `access_as_user`, portal pre-authorised. Follow README → *Entra ID setup*.
- [ ] Deploy: `set -a; . pulsar-prod.env; set +a; az deployment group create -g rg-pulsar-prod -f infra/main.bicep -p infra/main.prod.bicepparam`, then build images with `az acr build` and redeploy with the image parameters.
- [ ] Custom domain and managed certificate on the portal and API container apps; update the portal app registration's redirect URI and the API's `Cors__Origins__0`.
- [ ] ACS Email: verify a company sending domain (the default `*.azurecomm.net` sender lands in spam for many tenants) and set `Email__From` accordingly.
- [ ] Create notification channels (email list, Teams incoming webhook) and at least one Global alert rule; send a test by taking a known device down.
- [ ] Azure Monitor alerts on: API replica restarts, worker not running, Postgres storage > 80 %, Postgres CPU > 80 % for 15 min. Point them at the same Teams channel.
- [ ] Budget alert on the subscription for the prod resource group.

## Per-site rollout

- [ ] Host: Linux VM or Raspberry Pi 4/5 (64-bit OS, SSD or high-endurance SD), NTP correct, outbound 443 to the API allowed by the site firewall.
- [ ] Create the site and agent in the portal, copy the token, run `install.sh --api <url> --token <token>` (see `docs/agent-install.md`). Confirm the agent shows Online and the right version.
- [ ] Add devices and checks; mark the ones that should page as *critical*. Give the site a maintenance window for its patch night.
- [ ] Routed or trunked VLAN mode decided and documented for the site (`docs/vlan-setup.md`).
- [ ] Pull the site's WAN for 5 minutes: expect one "agent offline" alert after 3 minutes, a *Gateway unreachable* incident, its recovery, and no device-down flood. Needs the site's public address and a TCP port on the Gateway card, with that port answering (open or refused, not dropped) from the internet on the gateway's WAN interface.

## Open before go-live

| # | Item | Why it matters | Owner |
|---|---|---|---|
| 1 | End-to-end alert test on the dev VM (down → DOWN message → recovery message) | Notifications have not yet been exercised against real traffic | user + Claude |
| 2 | Rotate the two secrets in `docs/credential-rotation.md` | Both were pasted into chat | user |
| 3 | Soak: dev VM agent running 3+ days; look for false agent-offline incidents and outbox growth | Confirms the 3-minute offline threshold and reconnect behaviour | user |
| 4 | Pi validation of the same build (arm64 binary, `setcap`, SD wear) | Production sites will mostly run Pis | user |
| 4a | Appliance ISO boot test (`docs/iso-boot-test.md`) against the deployed claim-code API | CI proves the image builds (26.04.1, run 42256859494); only a boot proves the autoinstall and enrollment | user + Claude |
| 5 | ~~Self-update rollback~~ done in agent 0.1.7 (`docs/agent-install.md` → Automatic rollback). Remaining: re-run `install.sh` on hosts installed before 0.1.7 so systemd runs the start guard | Covers the update *from* 0.1.7 onward | user |
| 6 | ~~Load test~~ done 2026-11-09: 50 agents × 300 checks for 30 min, 0 errors, p95 280 ms, Postgres CPU peak 19 % (`docs/load-test.md`). Follow-up: same region for apps and database in prod (API is now capped at one replica) | Sizing confirmed | Claude |

## Known limits of v1

- Timescale on Azure ships the Apache edition: no compression, no retention policies, no continuous aggregates. The worker's retention job and raw-table queries cover the Phase 1 scale; revisit at ~500 rows/s.
- The portal's MSAL token cache is in memory. After a portal restart the first API call triggers a silent Entra round-trip rather than an error; a distributed cache (Redis) is only needed if that round-trip becomes visible at scale.
- One agent per site. HA agents are a Phase 2+ item.
