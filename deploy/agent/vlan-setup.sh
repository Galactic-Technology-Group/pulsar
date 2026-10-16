#!/usr/bin/env bash
# Generates (and optionally applies) 802.1Q sub-interfaces for Trunked mode.
#
#   ./vlan-setup.sh --parent eth0 --vlan 10=dhcp --vlan 20=10.20.0.5/24 --vlan 30=dhcp --dry-run
#   sudo ./vlan-setup.sh --parent eth0 --vlan 10=dhcp --vlan 20=10.20.0.5/24 --apply
#
# Backend is chosen automatically: netplan (Ubuntu, Raspberry Pi OS Bookworm with netplan) or
# NetworkManager via nmcli. Nothing is changed without --apply.
set -euo pipefail

PARENT=""
declare -a VLANS=()
MODE="dry-run"
BACKEND=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --parent)  PARENT="$2"; shift 2 ;;
    --vlan)    VLANS+=("$2"); shift 2 ;;
    --backend) BACKEND="$2"; shift 2 ;;
    --apply)   MODE="apply"; shift ;;
    --dry-run) MODE="dry-run"; shift ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

[[ -n "$PARENT" && ${#VLANS[@]} -gt 0 ]] || { echo "Need --parent <iface> and at least one --vlan <id>=dhcp|<cidr>" >&2; exit 1; }
if [[ "$MODE" == "apply" && $EUID -ne 0 ]]; then echo "--apply needs root." >&2; exit 1; fi

if [[ -z "$BACKEND" ]]; then
  if command -v netplan >/dev/null && [[ -d /etc/netplan ]]; then BACKEND=netplan
  elif command -v nmcli >/dev/null; then BACKEND=nm
  else echo "Neither netplan nor nmcli found; write /etc/network/interfaces or systemd-networkd by hand." >&2; exit 1; fi
fi

parse() { # "10=dhcp" -> id addr
  local spec="$1"
  [[ "$spec" =~ ^([0-9]{1,4})=(dhcp|[0-9]{1,3}(\.[0-9]{1,3}){3}/[0-9]{1,2})$ ]] || { echo "Bad --vlan spec: $spec (use 10=dhcp or 20=10.20.0.5/24)" >&2; exit 1; }
  VID="${BASH_REMATCH[1]}"; ADDR="${BASH_REMATCH[2]}"
  (( VID >= 1 && VID <= 4094 )) || { echo "VLAN id out of range: $VID" >&2; exit 1; }
}

echo "# backend: $BACKEND  parent: $PARENT  mode: $MODE" >&2

if [[ "$BACKEND" == "netplan" ]]; then
  FILE=/etc/netplan/60-nms-vlans.yaml
  {
    echo "network:"
    echo "  version: 2"
    echo "  vlans:"
    for spec in "${VLANS[@]}"; do
      parse "$spec"
      echo "    $PARENT.$VID:"
      echo "      id: $VID"
      echo "      link: $PARENT"
      if [[ "$ADDR" == "dhcp" ]]; then
        echo "      dhcp4: true"
        echo "      dhcp4-overrides: { use-routes: false }   # keep the default route on $PARENT"
      else
        echo "      addresses: [$ADDR]"
      fi
    done
  } > "${TMPFILE:=$(mktemp)}"
  if [[ "$MODE" == "dry-run" ]]; then
    echo "# would write $FILE:"; cat "$TMPFILE"; rm -f "$TMPFILE"
  else
    install -m 0600 "$TMPFILE" "$FILE"; rm -f "$TMPFILE"
    netplan generate && netplan apply
    echo "Applied $FILE"
  fi
else
  for spec in "${VLANS[@]}"; do
    parse "$spec"
    CON="nms-vlan$VID"
    CMD=(nmcli connection add type vlan con-name "$CON" ifname "$PARENT.$VID" dev "$PARENT" id "$VID")
    if [[ "$ADDR" == "dhcp" ]]; then CMD+=(ipv4.method auto ipv4.never-default yes ipv6.method ignore)
    else CMD+=(ipv4.method manual ipv4.addresses "$ADDR" ipv6.method ignore); fi
    if [[ "$MODE" == "dry-run" ]]; then
      printf '%q ' "${CMD[@]}"; echo
    else
      nmcli connection delete "$CON" >/dev/null 2>&1 || true
      "${CMD[@]}"
      nmcli connection up "$CON"
    fi
  done
fi

cat >&2 <<'EOF'

Verify:
  ip -d link show | grep -A1 'vlan'      # sub-interfaces with their ids
  ip -4 addr                              # addresses on eth0.<id>
  ping -I eth0.<id> <gateway on that VLAN>
  sudo tcpdump -ni eth0 vlan              # tagged frames on the trunk
The agent picks these up automatically on its next heartbeat (restart it to hurry: systemctl restart nms-agent).
EOF
