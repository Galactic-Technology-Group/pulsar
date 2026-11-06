# Appliance ISO boot test

Proves the x64 appliance end to end: unattended install, enrollment code on the console, approval in the portal,
agent online, self-update and the device checks working from the new box. Run it once per ISO build before handing
the image to a site, and again after an Ubuntu point-release bump.

What CI already proves (job `iso`): the Ubuntu image downloads and matches Canonical's checksum, the GRUB patch
applies, and `xorriso` writes a bootable ISO. What only a boot proves: the autoinstall itself, the first-boot
services, and enrollment against the API.

## Prerequisites

1. **API with claim-code support deployed.** The appliance has no token; it asks `POST /api/agents/claims` for a
   code. Against an older API the console stays on *Pulsar agent starting... (waiting for network and the API)*
   and the agent log repeats `Enrollment failed; retrying`. Merge PR 1, build the images and deploy them first.
2. **The ISO built for that API.** CI bakes in the `api_url` input (or the `PULSAR_API_URL` variable). Download the
   artifact of the run you want:
   ```bash
   gh run download <run id> --repo Galactic-Technology-Group/pulsar -n pulsar-agent-amd64-iso
   sha256sum -c pulsar-agent-amd64.iso.sha256
   ```
   Artifacts expire after 14 days; rebuild with a manual dispatch of the CI workflow (`build_iso` on) if needed.
3. **A portal login with the Admin or Operator role** (approving a claim is a write operation).
4. **A VM host** with the ISO reachable: 1 vCPU, 1 GB RAM, 16 GB disk, one NIC with DHCP on the management VLAN,
   outbound 443 to the API. UEFI or BIOS; Secure Boot may stay on.
   Hyper-V: Generation 2, disable *Secure Boot* only if the install fails to start; VMware: *Ubuntu Linux (64-bit)*;
   Proxmox: OVMF or SeaBIOS, VirtIO disk and NIC are fine (the image carries the guest agents for all three).

## Test

| Step | Do | Expect | Time |
|---|---|---|---|
| 1 | Boot the VM from the ISO | GRUB shows *Try or Install Ubuntu Server* and continues by itself after 5 s | 10 s |
| 2 | Wait, do not touch the console | Subiquity runs with no prompts, wipes the disk, installs, reboots once | 3 to 6 min |
| 3 | Watch tty1 after the reboot | Banner `PULSAR AGENT - NOT YET CLAIMED` with a code like `KD7P-4QRS`, the hostname `pulsar-<6 hex>`, the DHCP address and the API URL | within 30 s of login prompt |
| 4 | Portal: **Pending agents** | The code is listed with hostname, address and interfaces | immediate |
| 5 | Approve it, choosing the site | Banner switches to `Pulsar agent enrolled` | under 5 s |
| 6 | Portal: the site page | The agent is **Online**, version shown, interfaces listed; the Gateway card unchanged | under 30 s |
| 7 | Add a device on the site's LAN with an ICMP check | Check goes **Up** from the appliance (proves `cap_net_raw` on the binary) | about 1 min |
| 8 | Log in on the console as `pulsar` with the build password | Forced password change, then a shell | |
| 9 | `systemctl status nms-agent nms-agent-console unattended-upgrades` | all three `active` | |
| 10 | Settings → Agent releases, publish the current release | Appliance restarts the agent and reports the new version within a minute | |

Optional:
- Reboot the VM: the banner comes back as `enrolled`, the agent reconnects without a new code.
- Revoke the agent in the portal: the console shows a fresh code within a minute (re-claim after revoke).
- Unplug the VM's NIC for 4 minutes: one agent-offline alert after 3 minutes, its recovery, no device flood.

## Pass criteria

Steps 1 to 10 as expected, nothing typed on the box except the step 8 login. Record the ISO's SHA-256, the Ubuntu
point release, the agent version and the date in `docs/production-readiness.md`.

## If it fails

- Install never starts (drops to the GRUB prompt or a shell): the ISO lost its boot records; rebuild, and check the
  `xorriso ... -boot_image any replay` step ran against a verified Ubuntu image.
- Install prompts for input: the autoinstall was not picked up. On the GRUB line press `e` and confirm
  `autoinstall ds=nocloud\;s=/cdrom/nocloud/` is present; if it is, the `nocloud/user-data` failed validation,
  which the installer logs on tty2 (`Ctrl+Alt+F2`, `/var/log/installer/`).
- Install finishes but no banner: `journalctl -u nms-agent-console`; the unit is in `/opt/pulsar-install`.
- Banner shows *starting...* for more than a minute: the API is unreachable or predates claim codes.
  `journalctl -u nms-agent -f` on the box, and `curl -s <api>/healthz` from it.
- Code shown but never listed in the portal: the appliance reached a different API than the portal uses; compare the
  URL on the banner with `Api__BaseUrl` of the portal container app.
- Approved but stays *NOT YET CLAIMED*: the claim key on disk is stale (box was cloned from another appliance);
  `sudo rm /var/lib/nms-agent/claim.json && sudo systemctl restart nms-agent` for a fresh code.

## Clean-up

Delete the test agent in the portal (or keep it as the site's agent), then delete the VM. Nothing is left on the
API side except the agent record.
