#!/usr/bin/env bash
# Removes the Pulsar agent. Keeps /etc/nms-agent unless --purge is given.
set -euo pipefail
PURGE=0
[[ "${1:-}" == "--purge" ]] && PURGE=1
[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 1; }

systemctl disable --now nms-agent 2>/dev/null || true
rm -f /etc/systemd/system/nms-agent.service
systemctl daemon-reload
rm -rf /opt/nms-agent /var/lib/nms-agent
if [[ $PURGE -eq 1 ]]; then
  rm -rf /etc/nms-agent
  userdel nms-agent 2>/dev/null || true
  echo "Purged."
else
  echo "Removed. Config kept in /etc/nms-agent (use --purge to delete it and the service user)."
fi
