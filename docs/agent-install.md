# Agent installation

The agent is a single static binary (`nms-agent`, .NET 8 self-contained) for `linux-x64` and `linux-arm64`. It runs as an unprivileged systemd service, needs only outbound HTTPS (443) to the API, and keeps a local SQLite outbox so nothing is lost when the WAN drops.

## Supported hosts

- Ubuntu 22.04 / 24.04 (VM or bare metal), Debian 12
- Raspberry Pi 4 or 5 with **64-bit** Raspberry Pi OS (Bookworm) or Ubuntu Server. 32-bit OS images are not supported.
- 1 vCPU / 512 MB is plenty for 500 checks a minute.

## Install

1. In the portal open the site, **Add agent**, copy the enrollment token (valid 24 h, single use).
2. On the host, with the release assets from CI (`install.sh`, `nms-agent.service`, binaries + `.sha256`):

```bash
curl -fsSLO https://github.com/Galactic-Technology-Group/pulsar/releases/latest/download/install.sh
curl -fsSLO https://github.com/Galactic-Technology-Group/pulsar/releases/latest/download/nms-agent.service
chmod +x install.sh
sudo ./install.sh --api-url https://nms-api.example.com --token <enrollment token> \
  --base-url https://github.com/Galactic-Technology-Group/pulsar/releases/latest/download
```

The installer detects the architecture, verifies the SHA-256, creates the `nms-agent` user, installs to `/opt/nms-agent`, grants `cap_net_raw` (ICMP without root), writes `/etc/nms-agent/agent.json`, and enables the service. Re-running it (without `--token`) upgrades in place and keeps the config and credentials. Passing `--token` again stores the new token but keeps working credentials; the agent only enrols with it if the API rejects its stored secret. Add `--re-enrol` to drop the credentials and enrol as a fresh agent.

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
Expected within a few seconds: `Enrolled as agent …`, `Connected to https://…/hubs/agent`, `Applied config vN: K active checks`. The portal shows the agent Online with its interfaces.

## Configuration

`/etc/nms-agent/agent.json` (any key can also be set as `NMS_<Key>` in `/etc/nms-agent/environment`):

| Key | Meaning |
|---|---|
| `ApiBaseUrl` | API origin |
| `EnrollmentToken` | Used when `/var/lib/nms-agent/credentials` is missing or the API rejects it (401); otherwise ignored |
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
| `API rejected the stored credentials and no EnrollmentToken is configured` | The agent's secret was revoked or re-issued server-side; re-issue a token and re-run install with `--token` |
| `Raw ICMP socket denied` | `getcap /opt/nms-agent/nms-agent` should show `cap_net_raw+ep`; run `sudo setcap cap_net_raw+ep /opt/nms-agent/nms-agent` |
| Agent Online but checks Unknown | No results uploaded yet; wait one interval, then `journalctl -u nms-agent | grep -i upload` |
| Checks on another VLAN all Down in Trunked mode | `ip -4 addr` shows no address on that sub-interface, or the switch port is not trunking that VLAN |
| Portal shows Offline while host is up | Outbound 443 blocked or TLS inspection in the path; `curl -v https://<api>/healthz` from the host |
