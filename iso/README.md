# Pulsar agent appliance ISO (x64)

A stock Ubuntu Server 26.04 LTS installer with an embedded autoinstall and the agent payload. Boot it in a VM, wait a few minutes, and the console shows an enrollment code to approve in the portal under **Pending agents**. No token, no typing on the box.

## What the image does

- Unattended install onto the first disk (it is wiped), DHCP on the first NIC, SSH on.
- User `pulsar` with the password set at build time (default `pulsar`), forced to change at first login.
- Installs `nms-agent` (claim-code mode, pointed at the API URL baked in at build time) and `nms-agent-console`, which paints the enrollment code on tty1 and in the login prompt.
- `unattended-upgrades` with a 03:30 reboot window, hypervisor guest tools (QEMU/KVM, VMware, Hyper-V), a capped journal.
- Hostname `pulsar-<last 6 of the MAC>` so several appliances do not collide.

Everything on the box is plain Ubuntu LTS afterwards: security patches come from Canonical, the agent updates itself from the portal. The ISO only needs rebuilding when the autoinstall changes or Ubuntu publishes a new point release.

## Build

On Linux or WSL with `xorriso`, `curl`, `openssl`:

```bash
dotnet publish src/Nms.Agent -c Release -r linux-x64 -o out/linux-x64
./iso/build.sh --api-url https://nms-api.example.com --agent-dir out/linux-x64 --password 'something-better'
```

The first run downloads the Ubuntu ISO (about 3 GB) into `~/.cache/pulsar-iso` and verifies it against Ubuntu's `SHA256SUMS`. The point release is pinned in `build.sh` (`UBUNTU_VERSION`, currently 26.04.1); set the variable to pick up a newer one. Output: `pulsar-agent-amd64.iso` plus its `.sha256`.

CI builds it on every `v*` tag and on manual dispatch (workflow `CI`, job `iso`), using the repository variable `PULSAR_API_URL`. The ISO is a workflow artifact; it is larger than GitHub's 2 GiB release-asset limit, so it is not attached to the release. If the secret `ISO_UPLOAD_SAS_URL` (an Azure Blob container SAS URL with write permission) is set, CI also uploads it there.

## Use

1. Create a VM: 1 vCPU, 1 GB RAM, 16 GB disk, one NIC on the management VLAN (or a trunk port profile for Trunked sites). UEFI or BIOS both work; Secure Boot can stay on.
2. Attach the ISO and boot. The GRUB menu times out after 5 s into the install. Allow 3 to 6 minutes.
3. The box reboots and shows:
   ```
   PULSAR AGENT - NOT YET CLAIMED
   Enrollment code:  KD7P-4QRS
   ```
4. In the portal open **Pending agents**, find the code (hostname and interfaces are shown next to it), pick the site, approve. Within 5 s the console switches to "Pulsar agent enrolled" and the agent appears Online under the site.

A code expires after 15 minutes and is replaced automatically; nothing needs restarting.

## Trunked VLAN mode on a VM

The hypervisor port must pass tagged frames to the VM (VMware: VLAN ID 4095 on the port group; Hyper-V: `Set-VMNetworkAdapterVlan -Trunk`; Proxmox: VLAN-aware bridge with no tag on the VM NIC). Then run `vlan-setup.sh` on the box as described in `docs/vlan-setup.md`.

## Changing the API URL after install

`sudo /opt/pulsar-install/install.sh --api-url https://new-api --re-enrol` rewrites the config, clears credentials and shows a new code.
