# Architecture

## Components

| Component | Runs | Responsibilities |
|---|---|---|
| `nms-agent` | Linux VM / Raspberry Pi at each site, systemd | Enrol once; keep one outbound SignalR connection; run ICMP/TCP/HTTP checks on a jittered schedule with per-VLAN source binding; queue results in SQLite; upload in batches; on-demand discovery; self-update |
| `nms-api` | Azure Container Apps (external) | Agent enrollment and authentication; config building and push; result ingest with the check state machine and incident open/close; management REST API for the portal; `AgentHub` (agents) and `PortalHub` (browsers) |
| `nms-worker` | Azure Container Apps (internal, 1 replica) | Notification dispatch with digest/cooldown (email via ACS, Teams webhook); agent-offline watchdog; retention |
| `nms-portal` | Azure Container Apps (external, sticky) | Blazor Server UI over the API; Entra ID sign-in; live updates via `PortalHub` |
| PostgreSQL + TimescaleDB | Azure Database for PostgreSQL Flexible | Inventory, incidents, settings; `check_results` hypertable with compression, retention and a 5-minute continuous aggregate |

## Data flow

```
agent ──POST /api/agents/enroll (token)──▶ api ──▶ per-agent secret
agent ◀──SignalR /hubs/agent (ConfigUpdated, RunCheckNow, StartDiscovery, UpdateAvailable)── api
agent ──SignalR Heartbeat (interfaces, queue depth)──▶ api ──▶ agents.last_seen_utc
agent ──POST /api/results (batch, ordered)──▶ api ──▶ check_results + checks.state + incidents
                                                   └──▶ PortalHub.CheckStateChanged ──▶ browsers
worker ──poll incidents (Notified=false)──▶ email / Teams
worker ──poll agents.last_seen_utc──▶ Offline + AgentOffline incident
```

## Key decisions

- **Agent-initiated everything.** Sites sit behind NAT; only outbound 443 is required. Commands ride the same SignalR connection the agent opened.
- **State lives in the API ingest path, not in the worker.** Each result batch advances the check's state machine inside the same transaction that stores the rows, so incidents are consistent with history and there is no second scheduler to race. The worker only reacts to rows the API wrote.
- **Agent offline is a distinct incident kind.** One WAN outage produces one "site unreachable" alert; device incidents opened while the agent is offline are stored but pre-marked as notified.
- **Replayed history never flaps state.** Only a result newer than the check's last result drives the state machine, so an outbox replay after a long outage records history without re-opening closed incidents.
- **Timescale, not a custom partitioner.** 50 sites × 300 checks × 1/min is ~20 M rows/day; hypertable chunks, native compression and policies handle that on a B2s. The API degrades to a plain table when the extension is missing (local dev).
- **Portal is a client of the API.** One authorization model, one place that pushes config to agents.
- **Secrets never reach agents.** Agents hold a per-agent secret (PBKDF2-hashed server side) that can be revoked individually. Azure credentials, webhook URLs and the DB connection stay in Key Vault.

## Scaling

- API is stateless except `AgentRegistry` (connection → agent map per replica). Config pushes go through SignalR groups, so with more than one API replica add the Azure SignalR Service or a Redis backplane; one replica is fine for Phase 1.
- Worker runs single-replica by design (cooldown memory and timers). It is idle most of the time.
- Agent: 500 ICMP + 100 TCP checks per minute uses a few percent of a Pi 4 core; concurrency caps are configurable.

## Phase 2 sketch

- One Azure Relay Hybrid Connection per site; the agent is the listener, the API the sender.
- SSH: the API opens a stream over Relay, the agent connects to the device with SSH.NET on the right VLAN interface and pipes the PTY; the portal renders it with xterm.js over SignalR. Credentials from Key Vault references per device or entered per session; transcripts to Blob when audit is required.
- HTTP: YARP in the API proxies `/sites/{site}/devices/{device}/web/` over Relay to the agent, which forwards to the device. Per-device subdomains with a wildcard certificate for UIs that assume they are at `/`.
- Authorization per session (role + device ACL), short-lived session tokens, idle timeout, full audit log. The agent only connects to devices in its inventory.
