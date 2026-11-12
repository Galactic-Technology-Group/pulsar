# SNMP monitoring

Agents from 0.1.8 poll devices over SNMPv2c or SNMPv3 and report readings: UPS battery and power source, room temperature
and humidity, interface status and traffic, CPU load. A reading that crosses a limit opens a **Threshold** incident that goes
through the same alert rules, digest, cooldown and maintenance windows as a check that is down.

## Setting it up

1. **Enable SNMP on the device.** Read-only is enough; Pulsar never writes. Prefer SNMPv3 with SHA-256 (or SHA-1 on older
   cards) and AES. Allow the agent's address in the device's access list.
2. **Add a credential** (admins): on the site's page → Site settings for that site only, or App settings for every site. Communities and
   passwords are write-only: the API never returns them, and they travel only to the agents of sites whose checks use them.
   Leave a secret blank when editing to keep the stored one.
3. **Add an SNMP check** on the device (device page → SNMP, operators). Pick a template and a credential. The default
   interval is 5 minutes; port 161 unless set.
4. The device page shows the latest readings once the agent has polled. Click a reading for its 6 h / 24 h / 7 d history.
   Traffic and error rates need two polls.

The check itself is Up while the device answers and goes Down after the failure threshold of unanswered polls, like any other
check. Agents older than 0.1.8 are never sent SNMP checks; they pick them up after they update.

## Templates

Templates ship in `src/Nms.Shared/Snmp/templates.json` and are compiled into the API and portal. Each one lists the readings
(OIDs, units, scale, enum names) and the default limits copied onto a new check.

| Template | For | Readings | Default limits |
|---|---|---|---|
| `system` | Any SNMP device | Uptime | none |
| `interfaces` | Switches, routers, firewalls | Per-port status, in/out bit/s, in/out errors/s (64-bit counters, 32-bit fallback) | none (unused ports would alert) |
| `host-cpu` | Servers, NAS, many firewalls | CPU load per processor | above 90 % |
| `ups` | Most UPS cards (standard UPS-MIB, RFC 1628), including CyberPower and Eaton | Power source, battery status, charge, runtime, time on battery, battery temperature, input voltage, load, alarms | on battery; battery low; charge below 50 %; runtime below 10 min; load above 80 %; battery above 40 °C |
| `apc-ups` | APC Smart-UPS with a Network Management Card (PowerNet-MIB) | Output status, charge, runtime, internal temperature, replace-battery flag, load, input voltage | on battery; replace battery; charge below 50 %; runtime below 10 min; load above 80 %; above 40 °C |
| `cyberpower-ups` | CyberPower RMCARD (CPS-MIB) | As `apc-ups` | on battery; replace battery; charge, runtime, load |
| `apc-environment` | Temperature/humidity probes on an APC NMC (AP9335T/TH) | Temperature and humidity per probe | above 30 °C; above 60 % RH |

Vendor OIDs were checked against the PowerNet-MIB and CPS-MIB files on 2026-11-10. A template whose objects a device does
not have still answers (the check stays Up) with the detail `NoSuchObject`: pick another template.

To add a template, append it to `templates.json` (scalar OIDs end in `.0`, table columns do not; `labelOid` names the rows,
`fallbackOid` is walked when the main column is empty), then release the API and portal. The template-library test checks the
shape of every entry.

## Limits

Each SNMP check has its own limits, editable on the device page: reading, condition (`>`, `<`, `=`, `≠`), value, alert text,
and optionally one table row (an interface or probe name); without a row the limit applies to every row. Enum readings
compare against their numbers, which the form lists (UPS-MIB power source 5 = battery).

- A breach opens one incident per limit and row, and closes it when a later poll is back inside the limit.
- A failed poll leaves threshold incidents as they are; the unanswered device alerts through the check going Down.
- Editing or deleting a limit closes its open incident without a recovery message. Changing a check's template replaces its
  limits with the new template's defaults.
- Suppression matches check-down incidents: recorded but not paged while the site's agent is offline, during maintenance, or
  for non-critical devices.

## Storage and volume

Readings go to the `nms.metric_samples` hypertable (compressed after 7 days where the Timescale edition allows, kept 90 days;
the worker's retention job prunes it otherwise). The newest readings per check are also kept on the check for the device page.

Volume is driven by table templates: `interfaces` on a 48-port switch is about 250 readings per poll. At the 5-minute default,
50 sites with ten such switches each is roughly 400 rows/s at the top of each poll cycle, which is within what the load test
showed for results but close to the ~500 rows/s point where `docs/roadmap.md` says to revisit the store. Poll access switches
every 5 to 15 minutes, and use `system` instead of `interfaces` where per-port data is not needed.

## Troubleshooting

The check's recent results show why a poll failed:

| Detail | Meaning |
|---|---|
| `TimedOut` | No answer: wrong community, device ACL, SNMP disabled, or a firewall between VLANs |
| `PortUnreachable` | The device refused UDP 161 (nothing listening) |
| `AuthFailure`, `UnknownUser`, `UnsupportedSecurityLevel` | SNMPv3 user, authentication protocol or password does not match the device |
| `DecryptionFailed` | SNMPv3 privacy protocol or password does not match |
| `NotInTimeWindow`, `UnknownEngineId` | SNMPv3 engine clock or ID problem; usually clears on the next poll |
| `NoSuchObject` | The device answered but has none of the template's objects |
| `PrivacyNeedsAuth` | The credential sets encryption without authentication |
