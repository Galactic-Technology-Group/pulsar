# End-to-end alert test

Proves the chain agent probe → API incident → worker digest → email/Teams, in any environment.
Run it once per environment before go-live and after any change to the worker or the notification senders.

## Prerequisites

- An agent that shows **Up** on the Agents page.
- You are signed in to the portal with the **Admin** role (channels and rules are admin-only).
- Email: the ACS sender domain is verified (`az communication email domain list`). Teams: an incoming-webhook URL.

## Steps

1. **Settings → Notification channels**: add a channel.
   Email: name `ops-email`, type Email, value = your address (comma-separate several).
   Teams: type Teams webhook, value = the webhook URL, or `secret:<key-vault-secret-name>` to keep it out of the database.
2. **Settings → Alert rules**: add a rule `all-sites`, scope **All sites**, channel `ops-email`, critical only **on**, cooldown `300`.
3. **Site → Add device**: name `alert-test`, address = an IP on the site LAN that nothing answers (for example the top of the subnet, `.250`), **critical** ticked. Add an **ICMP** check with the defaults (60 s interval, threshold 3).
4. Wait. Timeline with defaults:

   | Elapsed | What happens |
   |---|---|
   | 0–3 min | Three consecutive ICMP failures; the API opens a `CheckDown` incident on the third |
   | +60 s | Digest window closes; the worker sends **DOWN: alert-test …** |
   | +10 s | Message is queued at ACS / posted to Teams |

5. **Recovery**: edit the device and change the address to something that answers (the site gateway). The next successful probe closes the incident; within about 70 s you receive **RECOVERED: …**.
6. **Agent offline** (optional, tests the site-unreachable path): on the agent host run `sudo systemctl stop nms-agent`. After 3 minutes (6 missed 30 s heartbeats) an `AgentOffline` incident opens and one message is sent; device checks stay quiet. `systemctl start nms-agent` closes it and sends a recovery that ends with the cross-check verdict, here `gateway stayed reachable from Azure (...)` because only the agent was stopped. Pulling the site's WAN instead opens a *Gateway unreachable* incident after about 90 s, the agent-offline summary reads `gateway ... is also unreachable from Azure: site WAN is down`, and the recovery says `gateway was unreachable from Azure hh:mm-hh:mm UTC`. Both need the site's public address set on the Gateway card of the site page.
7. Delete the `alert-test` device afterwards. Keep the channel and rule.

## What to check if nothing arrives

- Incidents page shows the incident: the probe side works, look at the worker.
- Worker logs (`ContainerAppConsoleLogs_CL`, app `ca-nms-dev-worker`):
  `Email '...' queued to N recipients` means ACS accepted it, check spam and the ACS sender domain.
  `Send to <channel> failed` is a sender error, the exception follows.
  `No sender for channel type` means a sender is not registered.
  Nothing at all: the rule did not match. Device not critical with "critical only" on, wrong site scope, or the channel is disabled.
- The incident was opened and closed inside the 60 s digest window: that is a flap and is suppressed by design.
