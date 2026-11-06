#!/usr/bin/env bash
# Pulsar agent console banner. Runs as root from nms-agent-console.service.
#
# Shows the enrollment code on the local console (tty1) and in the login prompt (/etc/issue.d) while the agent
# waits to be claimed, then the enrolled state. The agent itself never touches the console: it only writes
# /var/lib/nms-agent/claim-code (while a claim is pending) and /var/lib/nms-agent/credentials (once enrolled).
set -u

STATE_DIR="${STATE_DIR:-/var/lib/nms-agent}"
CONF="${CONF:-/etc/nms-agent/agent.json}"
ISSUE_DIR=/etc/issue.d
ISSUE_FILE="$ISSUE_DIR/50-pulsar.issue"
TTY="${TTY:-/dev/tty1}"
INTERVAL="${INTERVAL:-5}"

mkdir -p "$ISSUE_DIR"
last=""

api_url() { sed -n 's/.*"ApiBaseUrl": *"\([^"]*\)".*/\1/p' "$CONF" 2>/dev/null | head -1; }
primary_ip() { ip -4 -o addr show scope global 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1; }

banner() {
  local host ip
  host="$(hostname)"
  ip="$(primary_ip)"
  if [[ -f "$STATE_DIR/claim-code" ]]; then
    local code
    code="$(tr -d '[:space:]' < "$STATE_DIR/claim-code")"
    cat <<EOF

  ==============================================================
    PULSAR AGENT  -  NOT YET CLAIMED

    Enrollment code:   ${code}

    In the Pulsar portal open  Pending agents  and approve this
    code, choosing the customer site it belongs to.
    The code changes every 15 minutes until it is approved.

    Host: ${host}    IP: ${ip:-none yet}    API: $(api_url)
  ==============================================================

EOF
  elif [[ -f "$STATE_DIR/credentials" ]]; then
    cat <<EOF

  Pulsar agent enrolled.   Host: ${host}   IP: ${ip:-?}
  Status: systemctl status nms-agent      Logs: journalctl -u nms-agent -f

EOF
  else
    cat <<EOF

  Pulsar agent starting...   Host: ${host}   IP: ${ip:-none yet}
  (waiting for network and the API at $(api_url))

EOF
  fi
}

while true; do
  msg="$(banner)"
  if [[ "$msg" != "$last" ]]; then
    printf '%s\n' "$msg" > "$ISSUE_FILE"
    if [[ -w "$TTY" ]]; then
      # Clear and print on the hypervisor / HDMI console without disturbing an active login shell elsewhere.
      printf '\033[2J\033[H%s\n' "$msg" > "$TTY" 2>/dev/null || true
    fi
    last="$msg"
  fi
  sleep "$INTERVAL"
done
