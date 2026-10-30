#!/usr/bin/env bash
# Pulsar (Centralized NMS) agent installer. Idempotent; run as root.
#
#   sudo ./install.sh --api-url https://nms-api.example.com --token <enrollment token>
#   sudo ./install.sh --api-url ... --token ... --base-url https://github.com/Galactic-Technology-Group/pulsar/releases/latest/download
#   sudo ./install.sh --base-url ...               # upgrade in place; config and credentials are kept
#   sudo ./install.sh --token <new token> --re-enrol  # drop stored credentials and enrol again (e.g. after a revoke)
#   sudo ./install.sh --local ./nms-agent          # install a binary you built yourself
#
# Re-running with --token never discards working credentials: the agent only uses the token when the API
# rejects its stored secret. Pass --re-enrol to force it. Env overrides: BASE_URL.
set -euo pipefail

BASE_URL="${BASE_URL:-https://example.invalid/releases/latest}"
API_URL=""
TOKEN=""
REENROL=0
LOCAL_BIN=""
AGENT_NAME="$(hostname -s)"
INSTALL_DIR=/opt/nms-agent
CONF_DIR=/etc/nms-agent
STATE_DIR=/var/lib/nms-agent
SVC_USER=nms-agent
UNIT_SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/nms-agent.service"

usage() { sed -n '2,11p' "$0"; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --api-url)  API_URL="$2"; shift 2 ;;
    --token)    TOKEN="$2"; shift 2 ;;
    --base-url) BASE_URL="$2"; shift 2 ;;
    --local)    LOCAL_BIN="$2"; shift 2 ;;
    --name)     AGENT_NAME="$2"; shift 2 ;;
    --re-enrol|--re-enroll) REENROL=1; shift ;;
    -h|--help)  usage ;;
    *) echo "Unknown argument: $1" >&2; usage ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 1; }
command -v systemctl >/dev/null || { echo "systemd is required." >&2; exit 1; }

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
if [[ ! -f "$CONF_DIR/agent.json" ]]; then
  [[ -n "$API_URL" && -n "$TOKEN" ]] || { echo "First install needs --api-url and --token." >&2; exit 1; }
  echo "==> Writing $CONF_DIR/agent.json"
  cat > "$CONF_DIR/agent.json" <<EOF
{
  "ApiBaseUrl": "$API_URL",
  "EnrollmentToken": "$TOKEN",
  "Name": "$AGENT_NAME",
  "StateDir": "$STATE_DIR",
  "Interfaces": []
}
EOF
  chmod 0640 "$CONF_DIR/agent.json"
  chown root:"$SVC_USER" "$CONF_DIR/agent.json"
elif [[ -n "$TOKEN" ]]; then
  echo "==> Config exists; storing the new enrollment token"
  python3 - "$CONF_DIR/agent.json" "$TOKEN" "${API_URL}" <<'PY' 2>/dev/null || sed -i "s|\"EnrollmentToken\": *\"[^\"]*\"|\"EnrollmentToken\": \"$TOKEN\"|" "$CONF_DIR/agent.json"
import json, sys
p, tok, url = sys.argv[1:4]
d = json.load(open(p)); d["EnrollmentToken"] = tok
if url: d["ApiBaseUrl"] = url
json.dump(d, open(p, "w"), indent=2)
PY
  if [[ $REENROL -eq 1 ]]; then
    echo "==> --re-enrol: removing stored credentials; the agent will enrol with the new token"
    rm -f "$STATE_DIR/credentials"
  elif [[ -f "$STATE_DIR/credentials" ]]; then
    echo "==> Stored credentials kept; the token is only used if the API rejects them (pass --re-enrol to force)"
  fi
else
  [[ $REENROL -eq 0 ]] || { echo "--re-enrol needs --token <new token>." >&2; exit 1; }
  echo "==> Keeping existing $CONF_DIR/agent.json and credentials"
fi

# ---- service ----------------------------------------------------------------------------------
if [[ -f "$UNIT_SRC" ]]; then
  install -m 0644 "$UNIT_SRC" /etc/systemd/system/nms-agent.service
else
  echo "!!  nms-agent.service not found next to install.sh; fetching from $BASE_URL" >&2
  curl -fsSL -o /etc/systemd/system/nms-agent.service "$BASE_URL/nms-agent.service"
fi
systemctl daemon-reload
systemctl enable nms-agent >/dev/null
systemctl restart nms-agent
sleep 2
systemctl --no-pager --lines=5 status nms-agent || true
echo
echo "Done. Follow logs with: journalctl -u nms-agent -f"
