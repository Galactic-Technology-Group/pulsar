# Agent installation

The agent is a single static binary (`nms-agent`, .NET 8 self-contained) for `linux-x64` and `linux-arm64`. It runs as an unprivileged systemd service, needs only outbound HTTPS (443) to the API, and keeps a local SQLite outbox so nothing is lost when the WAN drops.

## Supported hosts

- Ubuntu 22.04 / 24.04 / 26.04 (VM or bare metal), Debian 12
- Raspberry Pi 4 or 5 with **64-bit** Raspberry Pi OS (Bookworm) or Ubuntu Server. 32-bit OS images are not supported.
- 1 vCPU / 512 MB is plenty for 500 checks a minute.

## Three ways to get an agent onto a site

| Method | When | How the box gets its identity |
|---|---|---|
| **Appliance ISO** (x64 VM) | Default for virtual machines | Boot `pulsar-agent-amd64.iso`; see `iso/README.md`. Shows an enrollment code. |
| **install.sh, claim code** | Existing Linux host or Pi | `install.sh --api-url …` with no token. Shows an enrollment code. |
| **install.sh, token** | Scripted / RMM rollouts | `install.sh --api-url … --token …` with a token from the portal. No human step. |

An **enrollment code** (for example `KD7P-4QRS`) is shown on the box's console, in the login prompt and in the journal. In the portal open **Pending agents**, find the code with its hostname and interfaces, pick the site and approve. The box collects its credentials within 5 seconds. Codes expire after 15 minutes and are replaced automatically; only the box that requested a code can collect against it, so seeing a code on a screen gives nobody access.

## Install with install.sh

On the host, with the release assets from CI (`install.sh`, `nms-agent.service`, `nms-agent-console.*`, binaries + `.sha256`):

```bash
base=https://github.com/Galactic-Technology-Group/pulsar/releases/latest/download
for f in install.sh nms-agent.service nms-agent-console.sh nms-agent-console.service; do curl -fsSLO "$base/$f"; done
chmod +x install.sh
sudo ./install.sh --api-url https://nms-api.example.com --base-url "$base"          # then approve the code in the portal
# or, scripted:
sudo ./install.sh --api-url https://nms-api.example.com --base-url "$base" --token <enrollment token>
```

The installer detects the architecture, verifies the SHA-256, creates the `nms-agent` user, installs to `/opt/nms-agent`, grants `cap_net_raw` (ICMP without root), writes `/etc/nms-agent/agent.json`, installs the console banner service, and enables everything. Re-running it (without `--token`) upgrades in place and keeps the config and credentials. Passing `--token` stores the token but keeps working credentials; the agent only enrols with it if the API rejects its stored secret. `--re-enrol` drops the credentials: with `--token` it enrols with the token, without it the box shows a new code. `--no-start` installs and enables without starting (used by image builds; implied inside a chroot).

To install a binary you built yourself:
```bash
dotnet publish src/Nms.Agent -c Release -r linux-arm64 -o out
sudo ./deploy/agent/install.sh --local out/nms-agent --api-url ... --token ...
```

## Verify

```bash
systemctl status nms-agent
journalctl -u nms-agent -f
```
Expected within a few seconds: `ENROLLMENT CODE XXXX-XXXX` (claim mode, until approved) or `Enrolled as agent …`, then `Connected to https://…/hubs/agent`, `Applied config vN: K active checks`. The portal shows the agent Online with its interfaces. The current code is also in `/var/lib/nms-agent/claim-code` while a claim is pending.

## Configuration

`/etc/nms-agent/agent.json` (any key can also be set as `NMS_<Key>` in `/etc/nms-agent/environment`):

| Key | Meaning |
|---|---|
| `ApiBaseUrl` | API origin |
| `EnrollmentToken` | Optional. Used when `/var/lib/nms-agent/credentials` is missing or the API rejects it (401); otherwise ignored. Absent = claim-code mode |
| `Name` | Display name reported to the portal |
| `Interfaces` | Optional allow-list of interface names (empty = all up, non-loopback IPv4) |
| `MaxConcurrentIcmp/Tcp/Http` | Probe concurrency (64 / 32 / 16) |
| `ResultsBatchSize` | Upload when this many results are queued (500) |

Check definitions never live on the agent; they are pushed from the API whenever a device or check changes.

## Upgrade

Tag the repository (`git tag v0.2.0 && git push origin v0.2.0`); CI builds both binaries, their `.sha256` files and the GitHub release. Then in the portal, Settings → Agent releases → **Publish from GitHub release** (leave the tag blank for the latest). The API reads the release, records it as current and pushes `UpdateAvailable` to every connected agent; agents that are offline are told at their next heartbeat. Each agent downloads the binary for its architecture (x64 or arm64, so the same release serves Pis and VMs), verifies the SHA-256, swaps `/opt/nms-agent/nms-agent` and exits so systemd restarts it on the new version. A failed download or checksum is logged and retried after an hour.

Builds hosted elsewhere can be published with explicit URLs and checksums under "Publish a build hosted elsewhere". Or re-run `install.sh --base-url ...` on the host: do **not** pass the original `--token`, it was consumed at first enrolment.

## Uninstall

```bash
sudo ./uninstall.sh          # keeps /etc/nms-agent
sudo ./uninstall.sh --purge  # removes config and the service user too
```
Revoke the agent in the portal as well so its secret is dead.

## Raspberry Pi notes

- Boot from a USB SSD, or use a high-endurance (A2 / "Endurance") microSD card. The outbox and journal write continuously; cheap cards die in months.
- Reduce writes: `sudo apt install log2ram`, and keep `Storage=volatile` or a small `SystemMaxUse=` in `/etc/systemd/journald.conf`.
- Disable Wi-Fi/Bluetooth if unused (`dtoverlay=disable-wifi`, `dtoverlay=disable-bt` in `/boot/firmware/config.txt`) so the trunk port is the only path.
- Set a fixed address on the management VLAN; a Pi that changes IP mid-outage is hard to find.
- For VLAN trunking see `vlan-setup.md`.

## Troubleshooting

| Symptom | Check |
|---|---|
| `Enrollment token rejected by the API (...)` | Token expired, already used, or the agent is revoked; **Re-issue token** on the agent in the portal and re-run install with `--token <new token>` |
| `Stored credentials were rejected by the API; requesting a new claim code` | The agent was revoked or its secret re-issued; it is back on Pending agents with a new code, approve or reject it there |
| Code shown but nothing on Pending agents | The box cannot reach the API: `curl -v https://<api>/healthz`; the code is only issued by the API, so a code on screen means the request did get through, check the portal is pointed at the same API |
| `Claim flow error (...)` repeating | Outbound 443 blocked or wrong `ApiBaseUrl`; fix with `install.sh --api-url <url> --re-enrol` |
| `Raw ICMP socket denied` | `getcap /opt/nms-agent/nms-agent` should show `cap_net_raw+ep`; run `sudo setcap cap_net_raw+ep /opt/nms-agent/nms-agent` |
| Agent Online but checks Unknown | No results uploaded yet; wait one interval, then `journalctl -u nms-agent | grep -i upload` |
| Checks on another VLAN all Down in Trunked mode | `ip -4 addr` shows no address on that sub-interface, or the switch port is not trunking that VLAN |
| Portal shows Offline while host is up | Outbound 443 blocked or TLS inspection in the path; `curl -v https://<api>/healthz` from the host |
