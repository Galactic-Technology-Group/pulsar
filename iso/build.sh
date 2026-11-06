#!/usr/bin/env bash
# Builds the Pulsar agent appliance ISO: stock Ubuntu Server 24.04 (amd64) + embedded autoinstall + agent payload.
#
#   ./iso/build.sh --api-url https://nms-api.example.com --agent-dir ./out/linux-x64 [--out pulsar-agent.iso]
#                  [--base-url <release download base>] [--password <console password, default "pulsar">]
#                  [--ubuntu-iso <path to an already downloaded ubuntu-24.04.x-live-server-amd64.iso>]
#
# Needs: xorriso, curl, openssl, sha256sum (apt install xorriso). Runs on Linux (CI) or WSL. Output ~3 GB.
# Boot it in any x64 VM (UEFI or BIOS): it installs hands-off and the agent shows an enrollment code.
set -euo pipefail

UBUNTU_VERSION="${UBUNTU_VERSION:-24.04.3}"
UBUNTU_URL="${UBUNTU_URL:-https://releases.ubuntu.com/${UBUNTU_VERSION%.*}/ubuntu-${UBUNTU_VERSION}-live-server-amd64.iso}"
UBUNTU_SUMS="${UBUNTU_SUMS:-https://releases.ubuntu.com/${UBUNTU_VERSION%.*}/SHA256SUMS}"
API_URL=""
BASE_URL="https://github.com/Galactic-Technology-Group/pulsar/releases/latest/download"
AGENT_DIR=""
OUT="pulsar-agent-amd64.iso"
PASSWORD="pulsar"
UBUNTU_ISO=""
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --api-url)    API_URL="$2"; shift 2 ;;
    --base-url)   BASE_URL="$2"; shift 2 ;;
    --agent-dir)  AGENT_DIR="$2"; shift 2 ;;
    --out)        OUT="$2"; shift 2 ;;
    --password)   PASSWORD="$2"; shift 2 ;;
    --ubuntu-iso) UBUNTU_ISO="$2"; shift 2 ;;
    -h|--help)    sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done
[[ -n "$API_URL" ]] || { echo "--api-url is required (the API the appliance will enrol with)." >&2; exit 1; }
[[ -n "$AGENT_DIR" && -f "$AGENT_DIR/nms-agent-linux-x64" || -f "$AGENT_DIR/nms-agent" ]] || { echo "--agent-dir must contain nms-agent-linux-x64 (or nms-agent)." >&2; exit 1; }
for t in xorriso curl openssl sha256sum; do command -v "$t" >/dev/null || { echo "$t is required." >&2; exit 1; }; done

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ---- 1. Base ISO (download once, verify against Ubuntu's SHA256SUMS) -----------------------------
if [[ -z "$UBUNTU_ISO" ]]; then
  CACHE="${ISO_CACHE:-$HOME/.cache/pulsar-iso}"
  mkdir -p "$CACHE"
  UBUNTU_ISO="$CACHE/$(basename "$UBUNTU_URL")"
  if [[ ! -f "$UBUNTU_ISO" ]]; then
    echo "==> Downloading $UBUNTU_URL"
    curl -fL --retry 3 -o "$UBUNTU_ISO.part" "$UBUNTU_URL" && mv "$UBUNTU_ISO.part" "$UBUNTU_ISO"
  fi
  echo "==> Verifying checksum"
  curl -fsSL "$UBUNTU_SUMS" | grep " \*$(basename "$UBUNTU_ISO")\$" | sed "s|\*.*|$UBUNTU_ISO|" | sha256sum -c -
fi

# ---- 2. Payload: autoinstall + agent ---------------------------------------------------------------
HASH="$(openssl passwd -6 "$PASSWORD")"
mkdir -p "$WORK/nocloud" "$WORK/pulsar"
sed -e "s|__API_URL__|$API_URL|g" -e "s|__BASE_URL__|$BASE_URL|g" -e "s|__PASSWORD_HASH__|$HASH|g" \
  "$HERE/autoinstall/user-data" > "$WORK/nocloud/user-data"
: > "$WORK/nocloud/meta-data"

if [[ -f "$AGENT_DIR/nms-agent-linux-x64" ]]; then cp "$AGENT_DIR/nms-agent-linux-x64" "$WORK/pulsar/nms-agent-linux-x64"
else cp "$AGENT_DIR/nms-agent" "$WORK/pulsar/nms-agent-linux-x64"; fi
cp "$REPO/deploy/agent/install.sh" "$REPO/deploy/agent/uninstall.sh" "$REPO/deploy/agent/vlan-setup.sh" \
   "$REPO/deploy/agent/nms-agent.service" "$REPO/deploy/agent/nms-agent-console.sh" "$REPO/deploy/agent/nms-agent-console.service" \
   "$WORK/pulsar/"
chmod 0755 "$WORK/pulsar/"*.sh "$WORK/pulsar/nms-agent-linux-x64"
( cd "$WORK/pulsar" && sha256sum nms-agent-linux-x64 > nms-agent-linux-x64.sha256 )

# ---- 3. GRUB: boot straight into the autoinstall ---------------------------------------------------
xorriso -osirrox on -indev "$UBUNTU_ISO" -extract /boot/grub/grub.cfg "$WORK/grub.cfg" >/dev/null 2>&1
chmod u+w "$WORK/grub.cfg"
# Every "linux /casper/vmlinuz ..." line gets the autoinstall datasource; the menu times out after 5 s.
sed -i -e 's|linux\s\+/casper/vmlinuz|& autoinstall ds=nocloud\\;s=/cdrom/nocloud/|' \
       -e 's|^set timeout=.*|set timeout=5|' "$WORK/grub.cfg"
grep -q 'autoinstall ds=nocloud' "$WORK/grub.cfg" || { echo "grub.cfg patch failed" >&2; exit 1; }

# ---- 4. Rebuild the ISO, keeping Ubuntu's BIOS+UEFI boot records -----------------------------------
echo "==> Writing $OUT"
rm -f "$OUT"
xorriso -indev "$UBUNTU_ISO" -outdev "$OUT" \
  -volid "PULSAR-AGENT" \
  -boot_image any replay \
  -map "$WORK/nocloud" /nocloud \
  -map "$WORK/pulsar" /pulsar \
  -map "$WORK/grub.cfg" /boot/grub/grub.cfg \
  -end >/dev/null 2>&1
sha256sum "$OUT" > "$OUT.sha256"
ls -lh "$OUT"
echo "Done. Console user: pulsar / <password you set> (must be changed at first login). API: $API_URL"
