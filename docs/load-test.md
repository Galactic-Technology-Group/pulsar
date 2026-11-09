# Load test

`tools/Nms.LoadTest` simulates site agents against a real API. Each simulated agent speaks the agent protocol: a
SignalR hub connection authenticated with its secret, `GetConfig`, a heartbeat every 30 s, and result batches of up to
500 posted every flush interval (10 s). It replaces the readiness item "simulated 50 agents × 300 checks against dev".

## What a run does

1. Removes leftovers of an interrupted run (anything named `loadtest-*`).
2. Seeds N sites `loadtest-NN`, each with one agent and M devices with one check (80 % ICMP, 20 % TCP, 60 s interval,
   down after 3 failures). Seeding writes the database directly, so the tool needs `ConnectionStrings__Nms`.
3. Runs every agent for the given minutes. Connects are staggered over the first minute.
   - 2 % of checks flap (down 3 minutes in every 15), so incidents open and close all run long.
   - The first R agents simulate a WAN cut at minute *replay-at*: they drop the hub, stop uploading for *replay-gap*
     minutes while still producing results, then drain the backlog back to back in 500-result batches, as the agent
     outbox does.
4. Prints one line per minute (posts, accepted results/s, latency percentiles of successful posts, non-2xx, 429,
   network errors, hub connections, drops, heartbeats) and a table per phase at the end, plus a `RESULT {json}` line.
5. Deletes everything it created. It also cleans up on SIGTERM; `clean` removes leftovers by hand.

**Nothing reaches the real notification channels.** Every seeded site gets a maintenance window for the run, so
check-down incidents are recorded but marked notified. Seeded agents have a 1-day heartbeat interval in the database,
so the worker's agent-offline watchdog never flags them, even if the run dies before cleaning up. The simulator still
heartbeats every 30 s.

## Running it against dev

The tool runs as a manual Container Apps job in the dev environment (`job-nms-loadtest`), with the apps' managed
identity for the registry pull and the `pg-connection` Key Vault secret, so no database credential leaves Azure.

```bash
az acr build -r acrnmsdev2ytr5jov7iqpk -t pulsar-loadtest:<tag> -f tools/Nms.LoadTest/Dockerfile .
```

```bash
az containerapp job update -g rg-pulsar-dev -n job-nms-loadtest --image acrnmsdev2ytr5jov7iqpk.azurecr.io/pulsar-loadtest:<tag>
```

```bash
az containerapp job start -g rg-pulsar-dev -n job-nms-loadtest
```

```bash
az containerapp job logs show -g rg-pulsar-dev -n job-nms-loadtest --execution <execution name> --container loadtest --tail 120 --format text
```

Arguments are set in the job template (`run --api <url> --agents 50 --checks 300 --minutes 30 --replay-agents 10
--replay-at 10 --replay-gap 10`). Add `--keep` to leave the data for inspection, then run the job with `clean`.
While it runs, the `loadtest-*` sites show on the portal dashboard.

## Results

See the latest run below; append new runs rather than overwriting.

### 2026-11-09: 50 agents × 300 checks, 30 minutes, dev (15ec1ea)

Setup: API 0.5 vCPU / 1 GiB (1 to 3 replicas), worker 0.5 vCPU, Postgres Flexible Burstable B2s. The apps run in
East US and the database in Central US. The load job ran inside the same Container Apps environment and reached the
API through its public ingress, like an agent would. 15,000 checks at 60 s is 250 results/s.

| Phase | Posts | Results | Rows/s | p50 ms | p95 ms | p99 ms | Max ms | Errors |
|---|---|---|---|---|---|---|---|---|
| Warm-up (first 2 min, staggered connects) | 425 | 21,286 | 177 | 253 | 280 | 492 | 520 | 0 |
| Steady | 2,400 | 120,001 | 250 | 244 | 274 | 329 | 556 | 0 |
| 10 agents cut off for 10 min (others steady) | 2,401 | 120,055 | 200 | 256 | 282 | 332 | 940 | 0 |
| Replay (3 min after they reconnect) | 959 | 74,944 | 416 | 235 | 323 | 485 | 697 | 0 |
| After replay | 2,100 | 105,001 | 250 | 246 | 273 | 322 | 722 | 0 |
| Whole run | 8,285 | 441,287 | 245 | 247 | 280 | 336 | 940 | 0 |

Hub: 60 connects (50 + 10 after the outage), 0 connect errors, 0 drops, 2,305 heartbeats with 0 errors, `GetConfig`
p95 298 ms. No 429s. The 10 replaying agents each drained about 3,000 queued results within the first minute
(peak 750 rows/s for that minute). 500 flap incidents were opened and closed, all suppressed by the maintenance window.

Server side over the run window (Azure Monitor, 1-minute grain):

| Resource | Average | Peak |
|---|---|---|
| API CPU | 0.07 cores | 0.16 cores |
| API memory | 429 MiB | 501 MiB |
| API replicas | 1 | 1 (never scaled out) |
| Worker CPU | < 0.01 cores | 0.01 cores |
| Postgres CPU | 9.7 % | 18.8 % |
| Postgres memory | 43 % | 44 % |
| Postgres IOPS consumed | 4 % | 5 % |
| Postgres connections | 14 | 18 |
| Postgres storage | 10.5 % | 10.7 % |

Conclusions:
- **The target scale fits the dev sizing with large headroom.** The API used a third of its CPU allotment at peak and
  Postgres stayed under a fifth of its CPU, so 50 sites at 300 checks do not need the GeneralPurpose database for
  capacity. Prod keeps GeneralPurpose for its SLA, sustained (non-burst) CPU and geo-redundant backups, not for load.
- **Ingest latency is network, not compute.** A post costs about 245 ms at the median whether it carries 1 result or
  500, with the API nearly idle. Each batch makes several sequential queries, and every one crosses from East US to
  the database in Central US. Agents upload in the background, so this is harmless today, but it is the first thing to
  fix if a portal page or a heartbeat ever feels slow: put the apps and the database in the same region for prod.
  `infra/main.prod.bicepparam` currently repeats the split.
- **No scale-out happened**, which matters because the API has no SignalR backplane: with two replicas, a config push or
  discovery request only reaches agents connected to the replica that handled the portal call. The API is now capped
  at one replica in `infra/main.bicep`; add Azure SignalR Service before raising it.
- Seeding 15,000 checks took 10 s and cleaning 441,000 results took 3 s.
