# Pulsar roadmap

Single roadmap for Pulsar, Galactic Technology Group's network monitoring system. It merges the competitive research
and phased plan drafted on 2026-11-08 (v0.3) with what the repository has already shipped and with the v1 go-live
list in `docs/production-readiness.md`. When the two disagreed, this file says what is built, what changes, and why.

Status key used throughout: **done** (in main and deployed to dev), **partial** (some of it exists, the rest is listed),
**new** (not started), **changed** (the draft's intent is kept but the approach differs from what is built).

---

## 1. Positioning

Pulsar is the monitoring layer for a security/AV integrator's service contracts. It borrows Domotz's device control,
Auvik's topology, PRTG's alert rigor and LibreNMS's cost structure, and adds two things none of them have together:

1. **Physical-security depth**: cameras, VMS/recorders, access control, intrusion, fire alarm supervision.
2. **Native Dynamics 365 Field Service**: every alert becomes a work order tied to the asset, site and SLA tier.

### Design rules (apply to every phase)

| Rule | State in v1 |
|---|---|
| One collector per site, one tenant per customer, unlimited devices; flat per-site pricing | **partial**. One agent per site is built. There is no tenant level yet: sites hang directly off one deployment. Tenant → Site arrives with the customer role (Phase 2). |
| Agentless first (SNMP, ARP, LLDP, ONVIF, SSH/WinRM); the Pulsar agent is the only agent | **done** in spirit. Nothing is installed on monitored devices. The site agent is the collector; the draft's "single Docker image" is **changed** to the appliance ISO plus `install.sh` for VM and Pi, which already exist. A container image can be added if a NAS deployment needs it. |
| Every action (tunnel, power cycle, snapshot, config push) is logged | **new**. Needed from the first write action (Phase 1 remote access). |
| Alerts become work orders; nothing lives only in a dashboard | **new** (Phase 3 Field Service). Incidents and notifications exist; the work-order link does not. |
| All clients consume the same public API; no client-only backend paths | **done**. The Blazor portal already calls the REST API with the user's Entra token; iOS and Windows clients use the same endpoints. |

---

## 2. Competitor feature benchmark

Research sources: vendor pages via Exa (domotz.com, auvik.com, paessler.com, docs.librenms.org), checked 2026-11-08.

| Capability | Domotz | Auvik | PRTG | LibreNMS |
|---|---|---|---|---|
| Discovery | Agentless collector; Layer-2 MAC-based identity (survives DHCP churn); LLDP/RFC4188, ARP, ONVIF; multi-subnet/VLAN | Free collectors; LLDP/CDP/forwarding tables + SNMP/SSH/vendor APIs; 700+ vendors | Ping sweep → SNMP/WMI fingerprint → device templates; scheduled re-discovery | CDP/LLDP/FDP, OSPF, BGP, ARP, SNMP scan; every 6 h by default |
| Topology | Auto L2 map; switch-port-to-device mapping | Live L1/L2/L3 self-drawing map; path analysis (strongest) | Custom drag-and-drop maps/dashboards (not auto-topology) | Basic link maps |
| Monitoring | SNMP templates, TCP services, RTT/loss, interface traffic + PoE draw, OS via SSH/WinRM, custom scripts, MIB browser | Device health, syslog, NetFlow/sFlow/J-Flow (TrafficInsights app classification), SaaS visibility | Sensor model (one value per sensor): SNMP, WMI, flow, packet sniff, HTTP, SSH, MQTT, OPC UA, cloud | SNMP graphs, syslog, NetFlow via NfSen, Nagios-plugin service checks, env sensors |
| Alerting | Shared alert profiles → PSA/ticketing/chat; new-device alerts | 64+ preconfigured alerts; AI prioritization and likely-cause | State/speed/volume/threshold/change triggers; dependencies; schedules; 14 notification methods | Rule engine; many transports |
| Remote access | Browser RDP/SSH/HTTP/Telnet/generic TCP tunnel; VPN-on-demand; 1-hour auto-expiring tunnels; no port forwarding | Encrypted remote terminal (SSH/Telnet/HTTP) | Device tools only | None native |
| Power control | PoE per-port cycle; PDU/UPS/smart-plug control with outlet-to-device linking; auto Wake-on-LAN | — | — | — |
| Config backup | Backup + one-click restore + drift diff | Versioned backups, side-by-side diff, admin-only change restriction | — | Via Oxidized/RANCID |
| Security | WAN TCP open-port scan + LAN UPnP scan; mark-safe workflow; rogue-device alerts; BitLocker status; SSO/SAML, 2FA, RBAC; SOC 2 / ISO 27001 | RBAC, audit history | MFA (Entra/Okta), audit log, AD/SSO | 2FA, LDAP/RADIUS/AD |
| MSP / integrations | ConnectWise, Autotask, HaloPSA, Syncro, Q360; public API; MCP server for AI clients | ConnectWise, Autotask, Halo, ServiceNow, IT Glue, Teams, Slack, PagerDuty; Inventory/Alert/Usage/Tenants APIs | Multi-probe; unlimited users; API; MSP edition | Full API; multi-tenant via poller groups; bandwidth billing (95th pct) |
| Deployment | Cloud + collector on Pi/NAS/Docker/VM/appliance (16 options) | Cloud + Windows/Linux collector | Windows core on-prem or Hosted; remote probes incl. Linux | Self-hosted LAMP/Docker; distributed pollers (~1,000 devices each) |
| Pricing | $35/collector-site/mo, unlimited devices; down to $17.50 at volume (Sept 2026) | Quote-based per billable device (~$27 network / $6 server / $1.50 workstation on Core; APs, UPS, printers free) | Per sensor: $200/mo (500) to $1,642/mo (10,000); 5–10 sensors/device | Free, GPLv3 |

### Where each wins

- **Domotz**: integrator's tool. ONVIF cameras (snapshot/stream/reboot), PDUs, PoE cycling, AV control systems, remote tunnels, flat per-site pricing. Weak on flow analysis and deep topology.
- **Auvik**: self-drawing topology, flow analytics, config change control, alert polish. Billable-device pricing is expensive on switch-heavy sites.
- **PRTG**: breadth (IT/OT/IoT); the model for dependency-aware alerting and remote probes. Windows-bound; sensor-count licensing is a chore.
- **LibreNMS**: proves the plumbing can be free. SNMP discovery, distributed polling, API, billing. No remote access, no native config backup, self-maintained stack.

---

## 3. Where v1 stands against the draft's Phase 0

The draft's Phase 0 (Nov 2026 to Jan 2027) is largely what the repository already contains, so it collapses into the
v1 go-live gate below.

| Draft Phase 0 item | State | Notes |
|---|---|---|
| Control plane in Azure; Entra auth; Postgres + time-series; event bus | **done** except the bus | Container Apps (API, worker, portal), Entra ID sign-in with roles, Postgres Flexible 16 + Timescale hypertable (`docs/architecture.md`). No event bus: SignalR hubs carry live events; a bus is only needed when a second consumer (Field Service, flow analytics) appears. |
| Collector as a single Docker image for Pi/NUC/VM/NAS; outbound-only channel; auto-update; heartbeat | **changed** | Built as a .NET single-file binary for x64 and arm64 with `install.sh`, plus the unattended x64 appliance ISO (`iso/`) with claim-code enrollment. Outbound 443 only, self-update from GitHub releases, 30 s heartbeat. A container image is a packaging task, not a redesign; add it when a NAS site asks. |
| Data model Tenant → Site → Device → Interface/Sensor; identity keyed on MAC | **partial** | Site → Device → Check exists, plus agent interfaces. No tenant. Devices are keyed by address; discovery already records MAC, so MAC identity is a migration, scheduled with SNMP discovery in Phase 1. |
| Poller groups | **new** | Deferred: results already carry `agent_id`, so a second agent per site is a data-model no-op; leader election and work splitting are the Phase 2 HA item. |
| RBAC skeleton admin / tech / read-only / customer | **partial** | Admin, Operator, Viewer exist as Entra app roles. Customer is new and needs the tenant level. |
| Site location from the Field Service account address | **partial** | `sites.address`, latitude and longitude exist (entered on the site page); sourcing them from Field Service is Phase 3. |
| Exit: collector at HQ and one customer site; inventory within 15 min of install | **partial** | Dev VM agent at HQ is enrolled and monitored. First customer site is the v1 go-live. |

---

## 4. Phases

### v1 go-live (now to Dec 2026)

The gate before the first contracted site. Tracked line by line in `docs/production-readiness.md`; summary:

- End-to-end alert test (`docs/alert-test.md`), credential rotation, 3-day soak on the dev VM, Pi validation.
- Appliance ISO boot test (`docs/iso-boot-test.md`) against the claim-code API.
- Self-update rollback: **done** (agent 0.1.7). Load test: **done** (`docs/load-test.md`).
- Prod environment: `rg-pulsar-prod`, Entra apps, custom domains, verified sending domain, Azure Monitor alerts.

**Exit criteria:** HQ and one customer site monitored; a WAN pull at the customer site produces one agent-offline alert
with the gateway verdict, its recovery, and no device flood.

### Phase 1: See and Reach (Jan to Apr 2027)

*Borrowed from Domotz discovery, remote access and power; PRTG trigger types.*

**Discovery and inventory**

| Item | State |
|---|---|
| ICMP sweep + ARP table per VLAN; hostnames by reverse DNS, mDNS and NetBIOS | **done** (agent 0.1.6) |
| LLDP/CDP neighbour read, SNMP v2c/v3 system and interface tables, ONVIF WS-Discovery | **new** |
| Vendor/model/type/serial classification (OUI table + SNMP sysObjectID + ONVIF device info) | **new** |
| Device identity keyed on MAC (survives DHCP churn); address becomes an attribute | **partial**: `devices.mac_address` (unique per site) is filled by discovery and the device form; a sweep that finds a known MAC at a new address offers *Update address*. Automatic re-addressing waits for scheduled re-discovery |
| Switch-port-to-device mapping with PoE draw per port (SNMP BRIDGE-MIB / POWER-ETHERNET-MIB) | **new** |
| Additional-subnet and VLAN scanning across routed networks | **done** (routed and trunked modes, `docs/vlan-setup.md`) |
| New-device alert, default on per site | **new**: scheduled re-discovery (LibreNMS does every 6 h) comparing against inventory |

**Monitoring**

| Item | State |
|---|---|
| ICMP RTT/loss, TCP service checks, HTTP/HTTPS checks | **done** |
| SNMP templates: CPU, memory, temperature, fan, PSU, PoE budget, interface traffic/errors/discards | **partial** (agent 0.1.8, `docs/snmp.md`): SNMPv2c/v3 poller in the agent, `Snmp` check type, site or global credentials, readings in the `metric_samples` hypertable with history charts. Templates so far: uptime, interfaces (status, traffic, errors), CPU. Memory, fans, PSU and PoE budget are vendor templates still to write. Templates are JSON compiled into the API and portal, not a database entity, until custom templates are needed. |
| 30-day raw history, 13-month rollups | **partial**: raw retention is 90 days; rollups need a worker aggregate job because Azure's Timescale has no continuous aggregates |
| Template library: Cisco, Meraki (API), Ubiquiti, Ruckus, Araknis, Fortinet, APC, CyberPower, Middle Atlantic, WattBox, Axis, Hanwha, Avigilon | **partial**: APC and CyberPower are in (`src/Nms.Shared/Snmp/templates.json`); the rest follow the first sites' equipment |
| UPS status: battery charge, runtime remaining, on-battery/on-mains, load %, input/output voltage, battery self-test result and replace-battery flag | **done** except the self-test result (agent 0.1.8): `ups` (UPS-MIB, RFC 1628), `apc-ups` (PowerNet-MIB) and `cyberpower-ups` (CPS-MIB) templates with default limits for on battery, replace battery, low charge, low runtime, high load and temperature. |
| Environmental sensors: room temperature, humidity, water/leak and dry-contact inputs | **partial** (agent 0.1.8): APC NMC temperature/humidity probes (`apc-environment`) with per-probe limits, shown on the device page. NetBotz, CyberPower probes, AKCP, Middle Atlantic/WattBox, leak and dry-contact inputs, and latest values on the site card, are still to do. Wireless (MQTT/LoRaWAN) sensors stay with Phase 4 OT/IoT reach. |

**Alerting**

| Item | State |
|---|---|
| Triggers: state, threshold, volume, change | **partial**: state (failure threshold, flap handling) and threshold (per SNMP reading and table row, `docs/snmp.md`) are done; volume and change are new |
| Parent-device dependency suppression (switch down → suppress downstream) | **partial**: agent-offline suppresses the whole site and the Azure gateway probe says whether the WAN is down; per-device parent links are new and need the switch-port mapping |
| Maintenance windows and site-level schedules | **done** (windows per site or device); recurring schedules are new |
| Channels: Teams, email, webhook; shared alert profiles per customer | **partial**: Teams and ACS email with digest and cooldown are done; generic webhook and per-customer profiles are new |

**Remote access and power** (supersedes the earlier "Phase 2 sketch" in `docs/architecture.md`, which stays as the design)

| Item | State |
|---|---|
| Brokered tunnels (SSH, RDP, HTTPS, Telnet, generic TCP) through the agent; 60-minute expiry; per-device scope; full session log | **new**: Azure Relay Hybrid Connection per site, agent as listener, SSH.NET + xterm.js for SSH, YARP for HTTP, as sketched. RDP and generic TCP ride the same stream. |
| PoE port power cycle (SNMP write; Meraki and UniFi APIs) | **new**: the audit log it needs is **done** (every portal/API write, `docs/architecture.md`) |
| PDU/UPS outlet control with outlet-to-device linking; Wake-on-LAN | **new** |

**Web app: geographic site map**

- Map view as a landing-page option beside the site tiles; one pin per site coloured by worst open incident; clustering at state/metro zoom.
- Pin → site card (open incidents, device count, agent heartbeat, gateway state, last incident) → site detail.
- Filter by customer, SLA tier, alert state; search by address or site name.
- Provider: Azure Maps (already in the Azure footprint) or MapKit JS. Decide in Phase 1 with tile cost per view in hand. Site address and coordinates are in place.

**Pulsar for iOS: status companion (read-only)**

- Native SwiftUI, iOS 17+; Entra sign-in against the existing API app registration; Face ID unlock.
- Site list with red/amber/green roll-up → device list → device detail (status, last seen, RTT, key SNMP metrics, 24 h sparkline). The dashboard and device endpoints already return this.
- Push via APNs honouring alert profiles and maintenance windows; acknowledge and snooze from the notification. Needs a notification channel type `Push` in the worker and a device-token registry in the API.
- Alert feed filtered by site, severity, customer; global search by name, IP, MAC, serial.
- Lock-screen and Home-screen widgets: open-critical count, per-site tile.
- Offline-tolerant: last known state cached and timestamped when stale.
- Read-only in Phase 1, safe to hand to customers.

**Exit criteria:** three service-contract sites monitored; a tech resolves one real outage via tunnel or power cycle
without a truck roll.

### Phase 2: Trust and Prove (May to Aug 2027)

*Borrowed from Auvik config management and topology, Domotz security scans, PRTG reports.*

- Config backup and one-click restore for switches, firewalls, APs; diff against last known good; drift alert.
- Auto Layer-2 topology map with live status overlay; path view between two devices (built on the LLDP and port mapping from Phase 1).
- Security: scheduled WAN open-port scan from Azure (the worker already probes the gateway from outside; extend it), LAN UPnP forwarding scan from the agent, mark-safe / auto-remove-when-closed workflow, rogue-device alert, OS checks via SSH/WinRM (patch level, BitLocker status).
- Reports: per-site uptime/SLA, inventory export, security posture, scheduled monthly client PDF.
- Tenant level, customer role and customer read-only portal; SSO/SAML for customers who are not in our Entra tenant; audit log export.
- HA: second agent per site with leader election (the data model already keys results by agent).

**iOS: actions on the phone**

- Power cycle (PoE port, PDU outlet) with a confirm step; Wake-on-LAN.
- Camera snapshot and 60-second test stream (logged, not stored).
- Launch a brokered SSH/HTTPS tunnel and hand off to Termius or Safari.
- Customer mode: same app, customer role, read-only, limited to their sites.

**iPad**

- Universal app. Map in the left pane, site/device detail in the right; multitasking.
- Same pins and filters as web; long-press a pin to open that site's topology map.
- Offline: cached pin states with timestamps.

**Pulsar for Windows (native)**

- WinUI 3 / .NET 8; distributed through Intune to technician laptops and the NOC display; Entra sign-in; Windows Hello unlock.
- Full operator surface: site map, device list/detail, alert queue with acknowledge/assign, topology map, config diffs, reports.
- Native advantages: one-click tunnel launch into local RDP/SSH/PuTTY; Windows toast notifications; kiosk/wall-board mode (rotating site map + alert feed) for the NOC screen.
- Always-on tray agent showing the critical count.

**Exit criteria:** a monthly report goes to a client as a service-contract deliverable; one config rollback performed in production.

### Phase 3: The Differentiators (Sep to Dec 2027)

**Physical-security depth**

- ONVIF camera health: online state, stream check, on-demand snapshot (not stored), video-profile config, PTZ status, reboot.
- VMS/recorder health: Genetec Security Center, Milestone, Avigilon. Server/archiver services, disk capacity and retention days, stream-loss and recording-gap detection.
- Access control and intrusion: panel heartbeat, door-controller comms, dual-path communicator check, central-station signal path.
- Fire alarm: supervisory/trouble conditions via SNMP or dry-contact bridge where permitted. Monitor only, never control.

**Dynamics 365 Field Service integration**

- Alert → case → work order with asset record, site and SLA tier from the service contract.
- Auto-close when the condition clears; dispatch via Field Service; Pulsar device ↔ Field Service customer asset (two-way sync).
- Uptime and incident history feed quotes and renewal proposals.
- This is where the event bus from the draft's Phase 0 becomes necessary: incidents are published once and consumed by notifications, Field Service and reports.

**Automation**

- Script library (Python/PowerShell) per device or template; conditional actions (camera offline > 5 min → PoE cycle once → escalate).
- Flow analytics (NetFlow/sFlow/IPFIX) for top talkers on recorder VLANs.

**Native apps**

- iOS/iPadOS: Field Service work-order pane (see the work order the alert created, mark on-site, close with notes); Live Activity for active incidents.
- Windows: Field Service work-order pane; config push and script execution with approval prompts; saved multi-monitor layouts per user.

**Map layers**

- Incident heat layer over time (which sites and areas generate the most alerts).
- Storm-track overlay for Florida hurricane season to pre-stage power and connectivity checks before landfall.

**Exit criteria:** every alert at a contracted site produces a Field Service work order; one Genetec site shows archiver retention on its dashboard.

### Phase 4: Scale and Intelligence (2028)

- MCP server exposing inventory, alerts and actions to AI clients with OAuth-scoped consent.
- Anomaly detection and alert prioritisation (per-interface/device baselines, likely-cause ranking).
- Multi-tenant white-label for subcontract/partner arrangements.
- Bandwidth/usage billing exports per site; public API with the same scopes as the UI.
- OT/IoT reach: Modbus, BACnet, MQTT for building systems and EV charging gear; wireless temperature, humidity and door sensors (MQTT, LoRaWAN gateways) through a new agent collector.

### Later / parked

- Android build (same API surface, no new backend work) once iOS is stable.
- Container image of the agent for NAS hosts (packaging only).

### Removed

- Apple Watch complication (2026-11-08).

### Explicitly not building (yet)

- Endpoint RMM (patching, software deployment). Pulsar stays a network/device layer.
- Packet capture and deep application classification. Buy or integrate if needed.
- Per-device pricing or feature tiers. Would break the flat-per-site story.

---

## 5. Client surface summary

| Client | Phase 1 | Phase 2 | Phase 3 |
|---|---|---|---|
| Web app (Blazor, exists) | Full operator surface + geographic site map | Topology, config diffs, security scans, reports, customer portal | Field Service pane, map heat/storm layers |
| iOS (iPhone) | Read-only status, push, widgets | Power cycle, snapshot/stream, tunnel launch, customer mode | Work-order pane, Live Activities |
| iPadOS | — | Universal app; map + detail split view; topology on long-press | Work-order pane |
| Windows (WinUI 3) | — | Full operator surface, kiosk/NOC mode, tray agent, Intune distribution | Work-order pane, config push, script execution |

---

## 6. Measures that matter

- Install-to-inventory time < 15 min (the appliance ISO already targets this: boot to enrolled in about 6 min).
- Alert-to-work-order < 1 min (Phase 3).
- Truck rolls avoided per month per site.
- Mean time to acknowledge and resolve by SLA tier, reported to the client.
- Collector uptime; false-positive alert rate (dependency suppression working).

---

## 7. Open questions

Answered since the draft:

- **Time-series store**: TimescaleDB on Azure Postgres Flexible, already in production shape. It is the Apache edition (hypertables only), so rollups are a worker job. Revisit only if ingest passes ~500 rows/s.
- **Identity provider**: Entra ID for staff, done. Customer SSO/SAML is a Phase 2 question.
- **Collector form factor**: x64 appliance ISO for VMs and NUCs, `install.sh` for Pi 4/5 (arm64) and existing Linux hosts. Pi validation is on the v1 list; a Pi 5 image is the natural next packaging step.

Still open:

- Mapping provider: Azure Maps vs MapKit JS (cost model, iOS parity). Decide at the start of Phase 1.
- Which VMS API to build first (Genetec Security Center SDK/Web SDK given channel-partner status).
- Fire alarm supervision: confirm what is permissible to monitor under Florida code and AHJ expectations before scoping.
- Customer pricing: the flat per-site number and how it maps onto existing service-contract tiers.
- SNMP write for PoE cycling vs vendor APIs only: writes need SNMPv3 auth/priv on every switch; decide the minimum we will support.
- Which UPS and environmental-sensor models are deployed at contracted sites, so the first templates match real gear.

## 8. Source pages (checked 2026-11-08)

- Domotz: domotz.com/features.php, /pricing.php, /features/management.php, /features/remote-power-management.php, /features/network-security.php, /features/security-camera-monitoring.php, /security-professionals.php, /integrators.php
- Auvik: auvik.com/network-management-software/, auvik.com/franklyit/blog/auvik-pricing-details/ (Sept 2025), auvik.com/auvik-integrations/
- Paessler PRTG: paessler.com/prtg/prtg-network-monitor, paessler.com/manuals/prtg/auto-discovery, paessler.com/manuals/prtg/differences_between_prtg_network_monitor_and_prtg_hosted_monitor
- LibreNMS: librenms.org, docs.librenms.org/Support/Features/, docs.librenms.org/Extensions/Auto-Discovery/, docs.librenms.org/Extensions/Distributed-Poller/
