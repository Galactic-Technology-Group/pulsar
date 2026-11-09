#!/bin/sh
# Pulsar agent start guard (systemd ExecStartPre). Rolls back a self-update whose binary keeps failing to start.
#
# The agent's updater keeps the running binary as nms-agent.prev and writes update.state before it swaps in a new
# version; the new version deletes update.state once it has held its hub connection for two minutes. While the file
# exists this script counts launches, and after MAX launches restores nms-agent.prev and records rollback.state,
# which the agent reports to the API and uses to block that version. The agent counts its own starts too (3); this
# script covers a binary that dies before reaching its own code. Same key=value format as UpdateGuard.cs.
#
# Never fails: the unit runs it with a leading '-', and every path below exits 0.
STATE_DIR="${NMS_STATE_DIR:-/var/lib/nms-agent}"
INSTALL_DIR="${NMS_INSTALL_DIR:-/opt/nms-agent}"
MAX="${NMS_GUARD_MAX_LAUNCHES:-5}"
STATE="$STATE_DIR/update.state"
EXE="$INSTALL_DIR/nms-agent"

[ -f "$STATE" ] || exit 0

get() { sed -n "s/^$1=//p" "$STATE" | head -n 1; }

version="$(get version)"
previous="$(get previous)"
launches="$(get launches)"
case "$launches" in ''|*[!0-9]*) launches=0 ;; esac
launches=$((launches + 1))

if [ "$launches" -gt "$MAX" ]; then
  if [ -f "$EXE.prev" ]; then
    mv -f "$EXE.prev" "$EXE" || exit 0
    {
      echo "failed=$version"
      echo "restored=$previous"
      echo "reason=agent $version failed to start $((launches - 1)) times"
      echo "at=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    } > "$STATE_DIR/rollback.state.tmp" && mv -f "$STATE_DIR/rollback.state.tmp" "$STATE_DIR/rollback.state"
    echo "nms-agent-guard: agent $version failed to start $((launches - 1)) times; restored $previous" >&2
  else
    echo "nms-agent-guard: agent $version keeps failing but there is no previous binary to restore" >&2
  fi
  rm -f "$STATE"
  exit 0
fi

if grep -q '^launches=' "$STATE"; then
  sed -i "s/^launches=.*/launches=$launches/" "$STATE"
else
  echo "launches=$launches" >> "$STATE"
fi
exit 0
