#!/bin/bash
set -euo pipefail

usage() {
    cat <<'EOF'
Uninstall the Home Assistant macOS Screen Time tooling.

Usage:
  sudo ./scripts/uninstall.sh [--all] [--yes]

Default (no flags):
  Removes only the new root daemon + voice helper (LaunchDaemon, LaunchAgent,
  installed scripts, root-daemon state, logs). Leaves config.json, the
  shared venv, and the old per-user agent (if installed) untouched — useful
  if you're reverting a machine back to the old agent only.

--all:
  Removes EVERYTHING: the above, plus the old per-user agent, the shared
  config.json (has your MQTT credentials in it), the shared venv, and the
  whole /Library/Application Support/ha-screen-agent directory. No
  breadcrumbs left behind on this machine.

--yes:
  Skip the confirmation prompt.
EOF
}

REMOVE_ALL=false
ASSUME_YES=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --all)
            REMOVE_ALL=true
            shift 1
            ;;
        --yes)
            ASSUME_YES=true
            shift 1
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage
            exit 1
            ;;
    esac
done

if [[ "$(id -u)" -ne 0 ]]; then
    echo "This script must be run as root (use sudo)." >&2
    exit 1
fi

AGENT_DIR="/Library/Application Support/ha-screen-agent"
CONFIG_PATH="$AGENT_DIR/config.json"
PYTHON_BIN="/usr/bin/python3"

DAEMON_PLIST_LABEL="com.ha.screen-daemon"
DAEMON_PLIST_PATH="/Library/LaunchDaemons/${DAEMON_PLIST_LABEL}.plist"
HELPER_PLIST_LABEL="com.ha.user-voice-helper"
HELPER_PLIST_PATH="/Library/LaunchAgents/${HELPER_PLIST_LABEL}.plist"
OLD_AGENT_PLIST_LABEL="com.ha.screen-agent"
OLD_AGENT_PLIST_PATH="/Library/LaunchAgents/${OLD_AGENT_PLIST_LABEL}.plist"

echo "This will remove:"
echo "  - LaunchDaemon:  $DAEMON_PLIST_PATH"
echo "  - LaunchAgent:   $HELPER_PLIST_PATH (all sessions)"
echo "  - $AGENT_DIR/root_daemon.py"
echo "  - $AGENT_DIR/user_voice_helper.py"
echo "  - $AGENT_DIR/root-daemon-state/"
echo "  - /var/log/ha-screen-daemon.*.log, /var/log/root_daemon_skeleton.log"
if [[ "$REMOVE_ALL" == true ]]; then
    echo ""
    echo "--all was passed, ALSO removing:"
    echo "  - Old per-user agent: $OLD_AGENT_PLIST_PATH, $AGENT_DIR/agent.py"
    echo "  - Old agent per-user state files (~/Library/Application Support/ha-screen-agent/state.json)"
    echo "  - Per-user logs (~/Library/Logs/ha-screen-agent/, ~/Library/Logs/ha-user-voice-helper/)"
    echo "    for each managed kid still on this machine"
    echo "  - Shared config.json (contains MQTT credentials): $CONFIG_PATH"
    echo "  - Shared venv: $AGENT_DIR/venv"
    echo "  - The entire $AGENT_DIR directory"
else
    echo ""
    echo "Left in place (re-run with --all to remove these too):"
    echo "  - Shared config.json: $CONFIG_PATH"
    echo "  - Shared venv: $AGENT_DIR/venv"
    echo "  - Old per-user agent (if installed)"
fi
echo ""

if [[ "$ASSUME_YES" != true ]]; then
    read -r -p "Proceed? [y/N]: " CONFIRM
    case "$CONFIRM" in
        [Yy]*) ;;
        *) echo "Aborted."; exit 0 ;;
    esac
fi

# Enumerate managed mac usernames from config (if it still exists) so we
# can bootout per-user LaunchAgents from any currently-active sessions.
CHILD_USERS=""
if [[ -f "$CONFIG_PATH" && -x "$PYTHON_BIN" ]]; then
    CHILD_USERS="$(
        CONFIG_PATH="$CONFIG_PATH" "$PYTHON_BIN" - <<'PY'
import json, os
try:
    with open(os.environ["CONFIG_PATH"]) as fh:
        data = json.load(fh)
except Exception:
    raise SystemExit(0)
for entry in data.get("managed_users") or []:
    mac = (entry.get("mac_user_account") or "").strip()
    if mac:
        print(mac)
PY
    )"
fi

bootout_from_all_sessions() {
    local plist_path="$1"
    if [[ -n "$CHILD_USERS" ]]; then
        while IFS= read -r CHILD_USER; do
            [[ -z "$CHILD_USER" ]] && continue
            if id "$CHILD_USER" >/dev/null 2>&1; then
                CHILD_UID="$(id -u "$CHILD_USER")"
                launchctl bootout "gui/${CHILD_UID}" "$plist_path" >/dev/null 2>&1 || true
            fi
        done <<<"$CHILD_USERS"
    fi
}

echo "Stopping root daemon..."
launchctl bootout system "$DAEMON_PLIST_PATH" >/dev/null 2>&1 || true
rm -f "$DAEMON_PLIST_PATH"

echo "Stopping voice helper in all active sessions..."
bootout_from_all_sessions "$HELPER_PLIST_PATH"
rm -f "$HELPER_PLIST_PATH"

rm -f "$AGENT_DIR/root_daemon.py"
rm -f "$AGENT_DIR/user_voice_helper.py"
rm -rf "$AGENT_DIR/root-daemon-state"

rm -f /var/log/ha-screen-daemon.out.log /var/log/ha-screen-daemon.err.log
rm -f /var/log/ha-user-voice-helper.out.log /var/log/ha-user-voice-helper.err.log
rm -f /var/log/root_daemon_skeleton.log

echo "New daemon and voice helper removed."

if [[ "$REMOVE_ALL" == true ]]; then
    if [[ -f "$OLD_AGENT_PLIST_PATH" ]]; then
        echo "Stopping old per-user agent in all active sessions..."
        bootout_from_all_sessions "$OLD_AGENT_PLIST_PATH"
        rm -f "$OLD_AGENT_PLIST_PATH"
        rm -f "$AGENT_DIR/agent.py"
        echo "Old agent removed."
    fi

    if [[ -n "$CHILD_USERS" ]]; then
        while IFS= read -r CHILD_USER; do
            [[ -z "$CHILD_USER" ]] && continue
            CHILD_HOME="$(dscl . -read "/Users/$CHILD_USER" NFSHomeDirectory 2>/dev/null | awk '{print $2}')"
            if [[ -n "$CHILD_HOME" && -f "$CHILD_HOME/Library/Application Support/ha-screen-agent/state.json" ]]; then
                rm -f "$CHILD_HOME/Library/Application Support/ha-screen-agent/state.json"
                echo "Removed old agent state for $CHILD_USER."
            fi
            if [[ -n "$CHILD_HOME" ]]; then
                # Per-user log directories (see config/CONFIG_REFERENCE.md
                # on why these live under ~/Library/Logs, not /tmp or
                # /var/log) — root can remove another user's files here,
                # it just can't create/write into their existing ones.
                rm -rf "$CHILD_HOME/Library/Logs/ha-screen-agent"
                rm -rf "$CHILD_HOME/Library/Logs/ha-user-voice-helper"
            fi
        done <<<"$CHILD_USERS"
    fi

    # Harmless no-op on a machine already migrated past the old /tmp log
    # locations (see CONFIG_REFERENCE.md) — kept for machines that never
    # got that fix before being uninstalled.
    rm -f /tmp/ha_screen_agent.out.log /tmp/ha_screen_agent.err.log
    rm -rf "$AGENT_DIR"
    echo "Removed config.json, venv, and $AGENT_DIR entirely."
fi

echo ""
echo "Uninstall complete."
