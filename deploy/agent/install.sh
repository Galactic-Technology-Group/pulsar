#!/usr/bin/env bash
# Pulsar (Centralized NMS) agent installer. Idempotent; run as root.
#
#   sudo ./install.sh --api-url https://nms-api.example.com                   # claim-code enrolment (default)
#   sudo ./install.sh --api-url https://nms-api.example.com --token <token>   # scripted enrolment with a token
#   sudo ./install.sh --api-url ... --base-url https://github.com/Galactic-Technology-Group/pulsar/releases/latest/download
#   sudo ./install.sh --base-url ...               # upgrade in place; config and credentials are kept
#   sudo ./install.sh --token <new token> --re-enrol  # drop stored credentials and enrol again with a token
#   sudo ./install.sh --re-enrol                   # drop stored credentials and show a new claim code
#   sudo ./install.sh --local ./nms-agent          # install a binary you built yourself
#   sudo ./install.sh ... --no-start               # inside an image build (chroot): install and enable, don't start
#
# Without --token the agent shows an ENROLLMENT CODE on the console and in the journal; approve it in the portal
# under Pending agents. Re-running with --token never discards working credentials: the agent only uses the token
# when the API rejects its stored secret. Pass --re-enrol to force it. Env overrides: BASE_URL.
set -euo pipefail

BASE_URL="${BASE_URL:-https://example.invalid/releases/latest}"
API_URL=""
TOKEN=""
REENROL=0
NOSTART=0
LOCAL_BIN=""
AGENT_NAME="$(hostname -s)"
INSTALL_DIR=/opt/nms-agent
CONF_DIR=/etc/nms-agent
STATE_DIR=/var/lib/nms-agent
SVC_USER=nms-agent
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNIT_SRC="$SRC_DIR/nms-agent.service"

usage() { sed -n '2,15p' "$0"; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --api-url)  API_URL="$2"; shift 2 ;;
    --token)    TOKEN="$2"; shift 2 ;;
    --base-url) BASE_URL="$2"; shift 2 ;;
    --local)    LOCAL_BIN="$2"; shift 2 ;;
    --name)     AGENT_NAME="$2"; shift 2 ;;
    --re-enrol|--re-enroll) REENROL=1; shift ;;
    --no-start) NOSTART=1; shift ;;
    -h|--help)  usage ;;
    *) echo "Unknown argument: $1" >&2; usage ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 1; }
command -v systemctl >/dev/null || { echo "systemd is required." >&2; exit 1; }
# Inside a chroot (image build) systemd is not running: enable units but never start them.
[[ -d /run/systemd/system ]] || NOSTART=1

case "$(uname -m)" in
  x86_64)  RID=linux-x64 ;;
  aarch64) RID=linux-arm64 ;;
  armv7l)  echo "32-bit ARM is not supported; install a 64-bit OS on the Pi." >&2; exit 1 ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac
echo "==> Architecture: $(uname -m) -> $RID"

# ---- user and directories ---------------------------------------------------------------------
if ! id -u "$SVC_USER" >/dev/null 2>&1; then
  echo "==> Creating system user $SVC_USER"
  useradd --system --no-create-home --home-dir "$STATE_DIR" --shell /usr/sbin/nologin "$SVC_USER"
fi
install -d -m 0755 -o root -g root "$INSTALL_DIR" "$CONF_DIR"
install -d -m 0750 -o "$SVC_USER" -g "$SVC_USER" "$STATE_DIR"

# ---- binary -----------------------------------------------------------------------------------
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
if [[ -n "$LOCAL_BIN" ]]; then
  echo "==> Using local binary $LOCAL_BIN"
  cp "$LOCAL_BIN" "$TMP/nms-agent"
else
  ARTIFACT="nms-agent-$RID"
  echo "==> Downloading $BASE_URL/$ARTIFACT"
  curl -fsSL --retry 3 -o "$TMP/nms-agent" "$BASE_URL/$ARTIFACT"
  if curl -fsSL --retry 3 -o "$TMP/sum" "$BASE_URL/$ARTIFACT.sha256" 2>/dev/null; then
    EXPECTED="$(awk '{print $1}' "$TMP/sum")"
    ACTUAL="$(sha256sum "$TMP/nms-agent" | awk '{print $1}')"
    [[ "$EXPECTED" == "$ACTUAL" ]] || { echo "Checksum mismatch: expected $EXPECTED got $ACTUAL" >&2; exit 1; }
    echo "==> Checksum OK"
  else
    echo "!!  No checksum file published; skipping verification" >&2
  fi
fi

# Replace atomically so a running agent is not corrupted mid-write.
install -m 0755 -o root -g root "$TMP/nms-agent" "$INSTALL_DIR/nms-agent.new"
mv -f "$INSTALL_DIR/nms-agent.new" "$INSTALL_DIR/nms-agent"
# Self-update needs to replace the binary; give the service user ownership of the directory.
chown "$SVC_USER:$SVC_USER" "$INSTALL_DIR" "$INSTALL_DIR/nms-agent"
if command -v setcap >/dev/null; then
  setcap cap_net_raw+ep "$INSTALL_DIR/nms-agent"
  echo "==> cap_net_raw granted"
else
  echo "!!  setcap not found (apt install libcap2-bin); ICMP will fall back and may fail without root" >&2
fi

# ---- config -----------------------------------------------------------------------------------
# The state directory holds credentials (once enrolled), claim.json and claim-code (while a claim is pending).
clear_state() { rm -f "$STATE_DIR/credentials" "$STATE_DIR/claim.json" "$STATE_DIR/claim-code"; }

write_config() {
  echo "==> Writing $CONF_DIR/agent.json"
  {
    echo "{"
    echo "  \"ApiBaseUrl\": \"$API_URL\","
    [[ -n "$TOKEN" ]] && echo "  \"EnrollmentToken\": \"$TOKEN\","
    echo "  \"Name\": \"$AGENT_NAME\","
    echo "  \"StateDir\": \"$STATE_DIR\","
    echo "  \"Interfaces\": []"
    echo "}"
  } > "$CONF_DIR/agent.json"
  chmod 0640 "$CONF_DIR/agent.json"
  chown root:"$SVC_USER" "$CONF_DIR/agent.json"
}

# Set or remove the token (and optionally the API URL) in an existing config without touching other keys.
update_config() {   # $1 = token ('' removes it), $2 = api url ('' keeps it)
  if command -v python3 >/dev/null; then
    python3 - "$CONF_DIR/agent.json" "$1" "$2" <<'PY'
import json, sys
p, tok, url = sys.argv[1:4]
d = json.load(open(p))
if tok: d["EnrollmentToken"] = tok
else: d.pop("EnrollmentToken", None)
if url: d["ApiBaseUrl"] = url
json.dump(d, open(p, "w"), indent=2)
PY
  elif [[ -n "$1" ]]; then
    sed -i "s|\"EnrollmentToken\": *\"[^\"]*\"|\"EnrollmentToken\": \"$1\"|" "$CONF_DIR/agent.json"
  fi
}

if [[ ! -f "$CONF_DIR/agent.json" ]]; then
  [[ -n "$API_URL" ]] || { echo "First install needs --api-url (and optionally --token)." >&2; exit 1; }
  write_config
elif [[ -n "$TOKEN" ]]; then
  echo "==> Config exists; storing the new enrollment token"
  update_config "$TOKEN" "$API_URL"
  if [[ $REENROL -eq 1 ]]; then
    echo "==> --re-enrol: removing stored credentials; the agent will enrol with the new token"
    clear_state
  elif [[ -f "$STATE_DIR/credentials" ]]; then
    echo "==> Stored credentials kept; the token is only used if the API rejects them (pass --re-enrol to force)"
  fi
elif [[ $REENROL -eq 1 ]]; then
  echo "==> --re-enrol: removing stored credentials and any token; the agent will show a new claim code"
  update_config "" "$API_URL"
  clear_state
else
  echo "==> Keeping existing $CONF_DIR/agent.json and credentials"
fi

# ---- self-update rollback guard (systemd ExecStartPre) ----------------------------------------------------
if [[ -f "$SRC_DIR/nms-agent-guard.sh" ]]; then
  install -m 0755 -o root -g root "$SRC_DIR/nms-agent-guard.sh" "$INSTALL_DIR/nms-agent-guard.sh"
elif curl -fsSL -o "$TMP/nms-agent-guard.sh" "$BASE_URL/nms-agent-guard.sh" 2>/dev/null; then
  install -m 0755 -o root -g root "$TMP/nms-agent-guard.sh" "$INSTALL_DIR/nms-agent-guard.sh"
else
  echo "!!  nms-agent-guard.sh not found; crash-loop rollback is limited to the agent's own start-up check" >&2
fi

# ---- service ----------------------------------------------------------------------------------
if [[ -f "$UNIT_SRC" ]]; then
  install -m 0644 "$UNIT_SRC" /etc/systemd/system/nms-agent.service
else
  echo "!!  nms-agent.service not found next to install.sh; fetching from $BASE_URL" >&2
  curl -fsSL -o /etc/systemd/system/nms-agent.service "$BASE_URL/nms-agent.service"
fi
# Console banner (enrollment code on tty1 / login prompt). Optional: skipped when the files are not shipped.
CONSOLE=0
if [[ -f "$SRC_DIR/nms-agent-console.sh" && -f "$SRC_DIR/nms-agent-console.service" ]]; then
  install -m 0755 -o root -g root "$SRC_DIR/nms-agent-console.sh" "$INSTALL_DIR/nms-agent-console.sh"
  install -m 0644 "$SRC_DIR/nms-agent-console.service" /etc/systemd/system/nms-agent-console.service
  CONSOLE=1
fi

enable_unit() {   # works with or without a running systemd (chroot): fall back to the wants symlink
  systemctl enable "$1" >/dev/null 2>&1 || {
    mkdir -p /etc/systemd/system/multi-user.target.wants
    ln -sf "/etc/systemd/system/$1.service" "/etc/systemd/system/multi-user.target.wants/$1.service"
  }
}

if [[ $NOSTART -eq 1 ]]; then
  enable_unit nms-agent
  [[ $CONSOLE -eq 1 ]] && enable_unit nms-agent-console
  echo "Installed and enabled (not started: --no-start / chroot). The agent starts at next boot."
  exit 0
fi

systemctl daemon-reload
enable_unit nms-agent
systemctl restart nms-agent
if [[ $CONSOLE -eq 1 ]]; then enable_unit nms-agent-console; systemctl restart nms-agent-console; fi
sleep 2
systemctl --no-pager --lines=5 status nms-agent || true
echo
if [[ -z "$TOKEN" && ! -f "$STATE_DIR/credentials" ]]; then
  echo "No token given: the agent shows an ENROLLMENT CODE within a few seconds."
  echo "  journalctl -u nms-agent -f | grep -i 'enrollment code'     (also shown on the console)"
  echo "Approve it in the portal under Pending agents."
fi
echo "Done. Follow logs with: journalctl -u nms-agent -f"
