#!/bin/bash
set -euo pipefail

usage() {
    cat <<'EOF'
Install the Home Assistant macOS Screen Time root daemon (LaunchDaemon) plus
its per-user voice helper (LaunchAgent).

Usage:
  sudo ./scripts/install_root_daemon.sh [options]

Options:
  --config PATH        Use an existing config.json instead of building one.
  --keep-old-agent      Leave the old per-user agent (screentime_enforcer.py)
                         installed and running. UNSAFE unless combined with
                         --dry-run (or the config already has
                         root_daemon_dry_run: true) — see the warning this
                         prints when used.
  --dry-run              Force root_daemon_dry_run: true in the written/
                         patched config: the new daemon observes and reports
                         to HA but never actually locks or shuts down.
  -h, --help              Show this help.

If no config exists at the shared install location, this scans local macOS
accounts on the Mac and walks you through building one (device ID, MQTT
settings — one account for the whole machine, not per kid, since any kid
can use any Mac — fail-safe mode, rapid-relogin tuning). If a config
already exists, you'll be offered the same walkthrough again to review or
update it, with existing values as defaults. See config/CONFIG_REFERENCE.md
for every field.
EOF
}

KEEP_OLD_AGENT=false
FORCE_DRY_RUN=false
CONFIG_SRC_INPUT=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --config)
            CONFIG_SRC_INPUT="$2"
            shift 2
            ;;
        --keep-old-agent)
            KEEP_OLD_AGENT=true
            shift 1
            ;;
        --dry-run)
            FORCE_DRY_RUN=true
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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DEFAULT_CONFIG_SRC="$PROJECT_DIR/config/root_daemon.config.sample.json"
PYTHON_BIN="/usr/bin/python3"

# Single shared install directory for both the old agent and the new
# daemon/helper — one config.json, one venv, one place to look.
AGENT_DIR="/Library/Application Support/ha-screen-agent"
CONFIG_PATH="$AGENT_DIR/config.json"
DAEMON_PATH="$AGENT_DIR/root_daemon.py"
HELPER_PATH="$AGENT_DIR/user_voice_helper.py"
VENV_PATH="$AGENT_DIR/venv"

DAEMON_PLIST_LABEL="com.ha.screen-daemon"
DAEMON_PLIST_PATH="/Library/LaunchDaemons/${DAEMON_PLIST_LABEL}.plist"
HELPER_PLIST_LABEL="com.ha.user-voice-helper"
HELPER_PLIST_PATH="/Library/LaunchAgents/${HELPER_PLIST_LABEL}.plist"

OLD_AGENT_PLIST_LABEL="com.ha.screen-agent"
OLD_AGENT_PLIST_PATH="/Library/LaunchAgents/${OLD_AGENT_PLIST_LABEL}.plist"

prompt_boolean() {
    local prompt default response
    prompt="$1"; default="$2"
    read -r -p "$prompt [$default]: " response || true
    response=${response:-$default}
    case "$response" in
        [Yy]*) echo true ;;
        *) echo false ;;
    esac
}

# Real (human) local accounts only: UniqueID >= 500 excludes system/service
# accounts (root, daemon, _mysql, etc, which all sit below 500 on macOS),
# plus an explicit skip list for the handful of non-numeric special cases.
detect_local_accounts() {
    local user uid
    while IFS= read -r user; do
        [[ -z "$user" ]] && continue
        case "$user" in
            root|daemon|nobody|Guest) continue ;;
            _*) continue ;;
        esac
        uid="$(dscl . -read "/Users/$user" UniqueID 2>/dev/null | awk '{print $2}')"
        [[ -z "$uid" ]] && continue
        (( uid < 500 )) && continue
        echo "$user"
    done < <(dscl . -list /Users | sort)
}

is_admin_user() {
    dseditgroup -o checkmember -m "$1" admin >/dev/null 2>&1
}

# Reads back an existing config.json (if any) so a reconfigure walkthrough
# can offer real values as defaults instead of empty prompts. Emits shell
# assignments meant to be eval'd by the caller. Never echoes the password
# to the terminal — it's only carried through as a variable so "leave
# blank to keep it" works without making the operator retype it.
load_existing_defaults() {
    [[ -f "$CONFIG_PATH" ]] || return 0
    CONFIG_PATH="$CONFIG_PATH" "$PYTHON_BIN" - <<'PY'
import json, os
def esc(v):
    return str(v).replace("\\", "\\\\").replace('"', '\\"').replace("$", "\\$")
with open(os.environ["CONFIG_PATH"]) as fh:
    data = json.load(fh)
print(f'EXISTING_DEVICE_ID="{esc(data.get("device_id",""))}"')
print(f'EXISTING_FRIENDLY_NAME="{esc(data.get("device_friendly_name",""))}"')
print(f'EXISTING_MQTT_HOST="{esc(data.get("mqtt_host",""))}"')
print(f'EXISTING_MQTT_PORT="{esc(data.get("mqtt_port",1883))}"')
print(f'EXISTING_MQTT_USER="{esc(data.get("mqtt_username",""))}"')
print(f'EXISTING_MQTT_PASS_RAW="{esc(data.get("mqtt_password",""))}"')
print(f'EXISTING_MQTT_TLS="{"y" if data.get("mqtt_tls") else "n"}"')
fail_mode = data.get("root_daemon_fail_mode", data.get("fail_mode","safe"))
print(f'EXISTING_FAIL_MODE="{esc(fail_mode)}"')
print(f'EXISTING_GRACE_MINUTES="{esc(data.get("root_daemon_fail_grace_minutes",120))}"')
print(f'EXISTING_RR_MAX="{esc(data.get("root_daemon_rapid_relogin_max_attempts", data.get("rapid_relogin_max_attempts",4)))}"')
print(f'EXISTING_RR_WARN="{esc(data.get("root_daemon_rapid_relogin_warn_attempt", data.get("rapid_relogin_warn_attempt",3)))}"')
pairs = []
for e in data.get("managed_users") or []:
    mu, cn = e.get("mac_user_account"), e.get("child_name")
    if mu and cn:
        pairs.append(f"{esc(mu)}\t{esc(cn)}")
# A REAL newline between pairs, not the two characters "\" + "n" — this
# string round-trips through `eval` as a double-quoted bash assignment,
# where \n has no escape meaning and would otherwise survive as literal
# backslash-n text, merging every pair's fields into one when read back.
print('EXISTING_MANAGED_PAIRS="' + "\n".join(pairs) + '"')
PY
}

existing_child_for() {
    local mac_user="$1"
    [[ -z "${EXISTING_MANAGED_PAIRS:-}" ]] && return 0
    printf '%s\n' "$EXISTING_MANAGED_PAIRS" | awk -F'\t' -v u="$mac_user" '$1==u{print $2}'
}

build_config_interactive() {
    if [[ "$RECONFIGURE" == true ]]; then
        echo "Reviewing existing config at $CONFIG_PATH — press enter to keep" >&2
        echo "each current value, or type a new one. Full field reference:" >&2
        echo "  $PROJECT_DIR/config/CONFIG_REFERENCE.md" >&2
        eval "$(load_existing_defaults)"
    else
        echo "No existing config found at $CONFIG_PATH. Let's create one." >&2
        echo "Press enter to accept the default value. Full field reference:" >&2
        echo "  $PROJECT_DIR/config/CONFIG_REFERENCE.md" >&2
    fi

    if [[ -n "${EXISTING_DEVICE_ID:-}" ]]; then
        # Reconfiguring an existing install: never silently change this —
        # entities/topics are already keyed to it, and changing it starts
        # a fresh set rather than continuing this device's history.
        SUGGESTED_DEVICE_ID="$EXISTING_DEVICE_ID"
    else
        # Fresh install: hostname alone isn't safe to suggest blindly.
        # macOS's own de-duplication (appending " (2)", " (3)" to a
        # computer name) only reliably fires when both Macs were actually
        # on the same network during each other's setup — two Macs set up
        # separately, or one restored from the other's backup/clone, can
        # both end up named identically with nothing to catch it. If that
        # happens and both get the same device_id, they'd silently
        # publish to identical MQTT topics and clobber each other's
        # state. Append a short suffix from the hardware serial number
        # instead — genuinely unique per physical Mac, no dependency on
        # anything the user might not have customized — so hitting enter
        # here is safe even if the hostname isn't actually unique.
        HOSTNAME_SLUG=$(hostname -s | tr '[:upper:] ' '[:lower:]-' | tr -cd '[:alnum:]-_')
        HOSTNAME_SLUG=${HOSTNAME_SLUG:-mac}
        SERIAL_SUFFIX=$(
            ioreg -rd1 -c IOPlatformExpertDevice 2>/dev/null \
                | awk -F'"' '/IOPlatformSerialNumber/{print $4}' \
                | tr '[:upper:]' '[:lower:]' | tr -cd '[:alnum:]' | tail -c 4
        )
        if [[ -n "$SERIAL_SUFFIX" ]]; then
            SUGGESTED_DEVICE_ID="${HOSTNAME_SLUG}-${SERIAL_SUFFIX}"
        else
            SUGGESTED_DEVICE_ID="$HOSTNAME_SLUG"
        fi
    fi
    echo "" >&2
    echo "Device ID identifies this Mac in MQTT topics — it must be" >&2
    echo "unique across every Mac you manage, not just distinct-looking." >&2
    echo "The suggested default below is safe to just accept even if this" >&2
    echo "Mac's name isn't actually unique (see the comment in this" >&2
    echo "script if you're curious why); type your own only if you want" >&2
    echo "something more readable than the auto-generated one." >&2
    read -r -p "Device ID [$SUGGESTED_DEVICE_ID]: " DEVICE_ID
    DEVICE_ID=${DEVICE_ID:-$SUGGESTED_DEVICE_ID}

    echo "" >&2
    echo "That ID is for topics, not for reading — a kid who uses more" >&2
    echo "than one Mac would otherwise see multiple identically-named" >&2
    echo "devices in HA with no way to tell them apart. Give this Mac a" >&2
    echo "short label to show alongside it there instead (e.g. \"Living" >&2
    echo "Room MacBook\", \"cj's MacBook Pro\") — optional, blank is fine." >&2
    read -r -p "Friendly name for Home Assistant [${EXISTING_FRIENDLY_NAME:-none}]: " FRIENDLY_NAME
    FRIENDLY_NAME=${FRIENDLY_NAME:-${EXISTING_FRIENDLY_NAME:-}}

    echo "" >&2
    echo "This machine uses ONE MQTT account for the whole daemon, not one" >&2
    echo "per kid — any managed kid can use any Mac in the house, and Home" >&2
    echo "Assistant tracks time per child regardless of which Mac they're" >&2
    echo "on, so credentials are scoped per-device here, not per-child." >&2
    read -r -p "MQTT host (hostname/IP) [${EXISTING_MQTT_HOST:-mqtt.local}]: " MQTT_HOST
    MQTT_HOST=${MQTT_HOST:-${EXISTING_MQTT_HOST:-mqtt.local}}
    read -r -p "MQTT port [${EXISTING_MQTT_PORT:-1883}]: " MQTT_PORT
    MQTT_PORT=${MQTT_PORT:-${EXISTING_MQTT_PORT:-1883}}
    read -r -p "MQTT username for this Mac [${EXISTING_MQTT_USER:-none}]: " MQTT_USER
    MQTT_USER=${MQTT_USER:-${EXISTING_MQTT_USER:-}}
    if [[ "$RECONFIGURE" == true ]]; then
        read -r -s -p "MQTT password (blank = keep existing, type 'clear' to remove it, not shown): " MQTT_PASS_INPUT; echo
        if [[ -z "$MQTT_PASS_INPUT" ]]; then
            MQTT_PASS="${EXISTING_MQTT_PASS_RAW:-}"
        elif [[ "$MQTT_PASS_INPUT" == "clear" ]]; then
            MQTT_PASS=""
        else
            MQTT_PASS="$MQTT_PASS_INPUT"
        fi
    else
        read -r -s -p "MQTT password (not shown, blank for none): " MQTT_PASS; echo
    fi
    MQTT_TLS=$(prompt_boolean "Use MQTT TLS?" "${EXISTING_MQTT_TLS:-n}")

    # --- managed users: scan real local accounts instead of free-typing ---
    echo "" >&2
    echo "Scanning local macOS accounts on this Mac..." >&2
    DETECTED_USERS=()
    while IFS= read -r u; do DETECTED_USERS+=("$u"); done < <(detect_local_accounts)

    MANAGED_JSON=""
    if [[ ${#DETECTED_USERS[@]} -gt 0 ]]; then
        echo "Found ${#DETECTED_USERS[@]} local account(s):" >&2
        # A lone admin account on an otherwise-empty Mac is very likely
        # that family member's own single-user machine, not a shared
        # parent login with no separate kid account — default to tracking
        # it rather than defaulting to skip, which would leave this Mac
        # unmanageable entirely.
        SOLE_ACCOUNT=false
        [[ ${#DETECTED_USERS[@]} -eq 1 ]] && SOLE_ACCOUNT=true
        for u in "${DETECTED_USERS[@]}"; do
            ADMIN_TAG=""
            is_admin_user "$u" && ADMIN_TAG=" (admin)"
            EXISTING_CHILD="$(existing_child_for "$u")"
            DEFAULT_ANSWER="y"
            if [[ -n "$ADMIN_TAG" ]]; then
                DEFAULT_ANSWER="n"
                if [[ "$SOLE_ACCOUNT" == true ]]; then
                    DEFAULT_ANSWER="y"
                    echo "  '$u' is the only account on this Mac — likely" >&2
                    echo "  this person's own machine, not a shared parent" >&2
                    echo "  login, so defaulting to track it." >&2
                fi
            fi
            INCLUDE=$(prompt_boolean "  Track '$u'$ADMIN_TAG on this Mac as a managed kid?" "$DEFAULT_ANSWER")
            if [[ "$INCLUDE" == "true" ]]; then
                CHILD_DEFAULT="${EXISTING_CHILD:-$u}"
                read -r -p "    Child name in Home Assistant for '$u' [$CHILD_DEFAULT]: " CHILD_NAME_FOR_USER
                CHILD_NAME_FOR_USER=${CHILD_NAME_FOR_USER:-$CHILD_DEFAULT}
                MANAGED_JSON+="${MANAGED_JSON:+, }{\"mac_user_account\": \"${u}\", \"child_name\": \"${CHILD_NAME_FOR_USER}\", \"topic_prefix\": \"screen/${CHILD_NAME_FOR_USER}\"}"
            fi
        done
    else
        echo "No local accounts auto-detected." >&2
    fi

    ADD_MORE=$(prompt_boolean "Add another managed user manually (e.g. an account not yet created on this Mac)?" "n")
    while [[ "$ADD_MORE" == "true" ]]; do
        read -r -p "  macOS account short username: " EXTRA_MAC_USER
        [[ -z "$EXTRA_MAC_USER" ]] && break
        read -r -p "  Child name in Home Assistant [$EXTRA_MAC_USER]: " EXTRA_CHILD_NAME
        EXTRA_CHILD_NAME=${EXTRA_CHILD_NAME:-$EXTRA_MAC_USER}
        MANAGED_JSON+="${MANAGED_JSON:+, }{\"mac_user_account\": \"${EXTRA_MAC_USER}\", \"child_name\": \"${EXTRA_CHILD_NAME}\", \"topic_prefix\": \"screen/${EXTRA_CHILD_NAME}\"}"
        ADD_MORE=$(prompt_boolean "Add another?" "n")
    done
    MANAGED_JSON="[$MANAGED_JSON]"

    echo "" >&2
    echo "What should this Mac do for a kid if it loses its connection to" >&2
    echo "Home Assistant (network drop, broker down, or right after a" >&2
    echo "reboot) and genuinely doesn't know whether they're allowed yet?" >&2
    echo "This only matters during that gap — once HA tells it the real" >&2
    echo "answer, that's what's used." >&2
    echo "  safe  - keep them blocked until it hears otherwise (recommended:" >&2
    echo "          a dropped connection should never hand out free time)" >&2
    echo "  open  - let them use the computer until it hears otherwise (no" >&2
    echo "          time limit on this — an outage could last hours)" >&2
    echo "  grace - keep them blocked, but give a small bounded window first" >&2
    echo "          (see below), so a real outage doesn't look like their" >&2
    echo "          computer is just broken" >&2
    read -r -p "Fail mode [safe/open/grace] (${EXISTING_FAIL_MODE:-safe}): " FAIL_MODE
    FAIL_MODE=${FAIL_MODE:-${EXISTING_FAIL_MODE:-safe}}
    ROOT_DAEMON_FAIL_MODE="$FAIL_MODE"
    SHARED_FAIL_MODE="$FAIL_MODE"
    GRACE_MINUTES="${EXISTING_GRACE_MINUTES:-120}"
    if [[ "$FAIL_MODE" == "grace" ]]; then
        # "grace" isn't a value the OLD agent's config loader accepts — it
        # would crash the old agent on its next restart if it's still
        # installed. Keep the shared fail_mode key at "safe" and put grace
        # only under the daemon-specific key.
        SHARED_FAIL_MODE="safe"
        read -r -p "Grace period minutes [$GRACE_MINUTES]: " GRACE_MINUTES_INPUT
        GRACE_MINUTES=${GRACE_MINUTES_INPUT:-$GRACE_MINUTES}
    fi

    read -r -p "Rapid-relogin shutdown: max attempts before forcing a shutdown [${EXISTING_RR_MAX:-4}]: " RR_MAX
    RR_MAX=${RR_MAX:-${EXISTING_RR_MAX:-4}}
    read -r -p "Rapid-relogin shutdown: attempt to start voice warning at [${EXISTING_RR_WARN:-3}]: " RR_WARN
    RR_WARN=${RR_WARN:-${EXISTING_RR_WARN:-3}}

    DRY_RUN_DEFAULT="n"
    if [[ "$KEEP_OLD_AGENT" == true ]]; then
        DRY_RUN_DEFAULT="y"
        echo "" >&2
        echo "--keep-old-agent was passed: the old agent stays the real" >&2
        echo "enforcement, so this daemon should run in dry-run (observe" >&2
        echo "only) mode to avoid both tools fighting over the same lock." >&2
    fi
    if [[ "$FORCE_DRY_RUN" == true ]]; then
        DRY_RUN="true"
    else
        DRY_RUN=$(prompt_boolean "Run new daemon in dry-run (observe-only) mode?" "$DRY_RUN_DEFAULT")
    fi

    TMP_CONFIG=$(mktemp)
    cat >"$TMP_CONFIG" <<EOF
{
  "device_id": "$DEVICE_ID",
  "device_friendly_name": "$FRIENDLY_NAME",
  "mqtt_host": "$MQTT_HOST",
  "mqtt_port": $MQTT_PORT,
  "mqtt_username": "$MQTT_USER",
  "mqtt_password": "$MQTT_PASS",
  "mqtt_tls": $MQTT_TLS,
  "sample_interval_seconds": 15,
  "idle_timeout_seconds": 180,
  "enforcement_mode": "lock",
  "fail_mode": "$SHARED_FAIL_MODE",
  "root_daemon_fail_mode": "$ROOT_DAEMON_FAIL_MODE",
  "root_daemon_fail_grace_minutes": $GRACE_MINUTES,
  "offline_grace_period_seconds": 180,
  "rapid_relogin_shutdown_enabled": true,
  "rapid_relogin_window_seconds": 60,
  "rapid_relogin_max_attempts": $RR_MAX,
  "rapid_relogin_warn_attempt": $RR_WARN,
  "rapid_relogin_warn_voice": true,
  "root_daemon_rapid_relogin_max_attempts": $RR_MAX,
  "root_daemon_rapid_relogin_warn_attempt": $RR_WARN,
  "root_daemon_dry_run": $DRY_RUN,
  "managed_users": $MANAGED_JSON,
  "state_path": "~/Library/Application Support/ha-screen-agent/state.json",
  "log_file": "~/Library/Logs/ha-screen-agent/agent.out.log",
  "err_log_file": "~/Library/Logs/ha-screen-agent/agent.err.log",
  "debug_mqtt": false,
  "track_active_app": false
}
EOF
    CONFIG_SRC="$TMP_CONFIG"
}

if [[ ! -x "$PYTHON_BIN" ]]; then
    echo "Python 3 not found at $PYTHON_BIN" >&2
    exit 1
fi

CONFIG_EXISTS=false
[[ -f "$CONFIG_PATH" ]] && CONFIG_EXISTS=true

CONFIG_SRC="$CONFIG_SRC_INPUT"
RECONFIGURE=false
if [[ -z "$CONFIG_SRC" ]]; then
    if [[ "$CONFIG_EXISTS" == false ]]; then
        build_config_interactive
    elif [[ -t 0 ]]; then
        RUN_SETUP=$(prompt_boolean "Existing config found at $CONFIG_PATH. Walk through setup again to review/update it?" "n")
        if [[ "$RUN_SETUP" == "true" ]]; then
            RECONFIGURE=true
            build_config_interactive
        fi
    fi
fi

if [[ -n "$CONFIG_SRC" ]]; then
    if [[ ! -f "$CONFIG_SRC" ]]; then
        echo "Config source '$CONFIG_SRC' does not exist." >&2
        exit 1
    fi
    CONFIG_SRC="$(cd "$(dirname "$CONFIG_SRC")" && pwd)/$(basename "$CONFIG_SRC")"
fi

mkdir -p "$AGENT_DIR"

if [[ -n "$CONFIG_SRC" ]]; then
    if [[ "$CONFIG_EXISTS" == true ]]; then
        echo "Updating config at $CONFIG_PATH"
    else
        echo "Creating config at $CONFIG_PATH"
    fi
    install -o root -g wheel -m 0644 "$CONFIG_SRC" "$CONFIG_PATH"
else
    echo "Existing config preserved at $CONFIG_PATH"
fi

# --keep-old-agent without an explicit dry-run opt-in is only safe if the
# config already says so. If not, patch root_daemon_dry_run to true rather
# than silently letting both tools fight over the same lock — see
# Context/HANDOFF.md, "old agent + new daemon coexistence".
if [[ "$KEEP_OLD_AGENT" == true ]]; then
    CURRENT_DRY_RUN="$(
        CONFIG_PATH="$CONFIG_PATH" "$PYTHON_BIN" - <<'PY'
import json, os
with open(os.environ["CONFIG_PATH"]) as fh:
    data = json.load(fh)
print("true" if data.get("root_daemon_dry_run") else "false")
PY
    )"
    if [[ "$CURRENT_DRY_RUN" != "true" ]]; then
        echo "" >&2
        echo "WARNING: --keep-old-agent was passed but root_daemon_dry_run" >&2
        echo "is not set in $CONFIG_PATH. Forcing it to true now — running" >&2
        echo "both tools with real enforcement on the same machine WILL" >&2
        echo "collide (a backgrounded kid's lock attempt can blank whoever" >&2
        echo "is actually at the console). The new daemon will observe and" >&2
        echo "report to HA only; the old agent remains the real enforcement." >&2
        CONFIG_PATH="$CONFIG_PATH" "$PYTHON_BIN" - <<'PY'
import json, os
path = os.environ["CONFIG_PATH"]
with open(path) as fh:
    data = json.load(fh)
data["root_daemon_dry_run"] = True
with open(path, "w") as fh:
    json.dump(data, fh, indent=2)
    fh.write("\n")
PY
    fi
fi

install -o root -g wheel -m 0755 "$PROJECT_DIR/root-daemon/root_daemon_skeleton.py" "$DAEMON_PATH"
install -o root -g wheel -m 0755 "$PROJECT_DIR/root-daemon/user_voice_helper.py" "$HELPER_PATH"

if [[ ! -d "$VENV_PATH" ]]; then
    echo "Creating virtual environment under $VENV_PATH"
    "$PYTHON_BIN" -m venv "$VENV_PATH"
else
    echo "Reusing existing virtual environment at $VENV_PATH"
fi

"$VENV_PATH/bin/pip" install --upgrade pip wheel >/tmp/ha-root-daemon-pip.log
"$VENV_PATH/bin/pip" install -r "$PROJECT_DIR/requirements.txt" >/tmp/ha-root-daemon-install.log

CHILD_USERS="$(
    CONFIG_PATH="$CONFIG_PATH" "$PYTHON_BIN" - <<'PY'
import json, os
with open(os.environ["CONFIG_PATH"]) as fh:
    data = json.load(fh)
for entry in data.get("managed_users") or []:
    mac = (entry.get("mac_user_account") or "").strip()
    if mac:
        print(mac)
PY
)"

CONFIG_GROUP="wheel"
CONFIG_MODE="0644"
FIRST_CHILD_USER="$(echo "$CHILD_USERS" | head -n1)"
if [[ -n "$FIRST_CHILD_USER" && "$(id -un "$FIRST_CHILD_USER" 2>/dev/null)" == "$FIRST_CHILD_USER" ]]; then
    CHILD_GROUP="$(id -gn "$FIRST_CHILD_USER" 2>/dev/null || true)"
    if [[ -n "$CHILD_GROUP" ]]; then
        CONFIG_GROUP="$CHILD_GROUP"
        CONFIG_MODE="0640"
    fi
fi
chown root:"$CONFIG_GROUP" "$CONFIG_PATH"
chmod "$CONFIG_MODE" "$CONFIG_PATH"
echo "Config permissions set to $CONFIG_MODE (group: $CONFIG_GROUP)."

# --- old agent handling -----------------------------------------------
if [[ -f "$OLD_AGENT_PLIST_PATH" ]]; then
    if [[ "$KEEP_OLD_AGENT" == true ]]; then
        echo ""
        echo "Leaving the old per-user agent installed and running (--keep-old-agent)."
        echo "New daemon dry_run is enforced true — see warning above if it was just set."
    else
        echo ""
        echo "Removing the old per-user agent (found at $OLD_AGENT_PLIST_PATH)."
        if [[ -n "$CHILD_USERS" ]]; then
            while IFS= read -r CHILD_USER; do
                [[ -z "$CHILD_USER" ]] && continue
                if id "$CHILD_USER" >/dev/null 2>&1; then
                    CHILD_UID="$(id -u "$CHILD_USER")"
                    launchctl bootout "gui/${CHILD_UID}" "$OLD_AGENT_PLIST_PATH" >/dev/null 2>&1 || true
                    echo "  Booted old agent out of ${CHILD_USER}'s session (if it was running)."
                fi
            done <<<"$CHILD_USERS"
        fi
        rm -f "$OLD_AGENT_PLIST_PATH"
        rm -f "$AGENT_DIR/agent.py"
        echo "  Old agent LaunchAgent and installed script removed."
    fi
else
    echo ""
    echo "No old per-user agent found installed — nothing to migrate away from."
fi

# --- LaunchDaemon (new root daemon) ------------------------------------
cat > "$DAEMON_PLIST_PATH" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>${DAEMON_PLIST_LABEL}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${VENV_PATH}/bin/python3</string>
        <string>${DAEMON_PATH}</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>UserName</key>
    <string>root</string>
    <key>StandardOutPath</key>
    <string>/var/log/ha-screen-daemon.out.log</string>
    <key>StandardErrorPath</key>
    <string>/var/log/ha-screen-daemon.err.log</string>
</dict>
</plist>
EOF
chown root:wheel "$DAEMON_PLIST_PATH"
chmod 0644 "$DAEMON_PLIST_PATH"

launchctl bootout system "$DAEMON_PLIST_PATH" >/dev/null 2>&1 || true
if launchctl bootstrap system "$DAEMON_PLIST_PATH"; then
    echo "Root daemon loaded (system-wide, runs regardless of who's logged in)."
else
    echo "Failed to bootstrap the root daemon. Try manually:" >&2
    echo "  sudo launchctl bootstrap system $DAEMON_PLIST_PATH" >&2
fi

# --- LaunchAgent (voice helper, per managed user) -----------------------
cat > "$HELPER_PLIST_PATH" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>${HELPER_PLIST_LABEL}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${VENV_PATH}/bin/python3</string>
        <string>${HELPER_PATH}</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>
    <!-- /dev/null, not /var/log: this runs as the unprivileged kid, and
         /var/log is root:wheel, not writable by a regular user. The
         helper sets up its own ~/Library/Logs file instead. -->
    <key>StandardOutPath</key>
    <string>/dev/null</string>
    <key>StandardErrorPath</key>
    <string>/dev/null</string>
    <key>LimitLoadToSessionType</key>
    <array>
        <string>Aqua</string>
    </array>
</dict>
</plist>
EOF
chown root:wheel "$HELPER_PLIST_PATH"
chmod 0644 "$HELPER_PLIST_PATH"

if [[ -n "$CHILD_USERS" ]]; then
    while IFS= read -r CHILD_USER; do
        [[ -z "$CHILD_USER" ]] && continue
        if id "$CHILD_USER" >/dev/null 2>&1; then
            CHILD_UID="$(id -u "$CHILD_USER")"
            echo "Bootstrapping voice helper for GUI session user '${CHILD_USER}' (uid ${CHILD_UID})."
            launchctl bootout "gui/${CHILD_UID}" "$HELPER_PLIST_PATH" >/dev/null 2>&1 || true
            if launchctl bootstrap "gui/${CHILD_UID}" "$HELPER_PLIST_PATH"; then
                echo "  Voice helper loaded for ${CHILD_USER}."
            else
                echo "  Not currently logged in or failed to bootstrap — it will load automatically on next login." >&2
            fi
        else
            echo "Warning: managed_users entry '${CHILD_USER}' is not a local user." >&2
        fi
    done <<<"$CHILD_USERS"
else
    echo "No 'managed_users' configured. Voice helper installed but not bootstrapped for anyone yet."
fi

# --- Mosquitto ACL snippet -------------------------------------------------
# Generated, not hand-written: every past ACL block for this project was
# manually derived in conversation and got real details wrong more than
# once (device_id, child_name typos). This computes it directly from what
# THIS install actually has in config.json, so it can't drift from reality
# the way a hand-written one did.
#
# Only grants what the daemon actually uses (same "only what's used"
# discipline as the rest of this project's ACL work) — notably does NOT
# grant anything on .../override/state, since the daemon never reads or
# writes that topic itself, only publishes its discovery config (already
# covered by the broad homeassistant/+/+/config grant below).
ACL_SNIPPET="$(
    CONFIG_PATH="$CONFIG_PATH" "$PYTHON_BIN" - <<'PY'
import json, os

def sanitize_device_id(value):
    s = "".join(ch if ch.isalnum() or ch in "-_" else "-" for ch in str(value).lower())
    return s or "mac"

with open(os.environ["CONFIG_PATH"]) as fh:
    data = json.load(fh)

device_id = sanitize_device_id(data.get("device_id", ""))
mqtt_user = (data.get("mqtt_username") or "").strip() or "<SET mqtt_username IN config.json FIRST>"

lines = [f"user {mqtt_user}", ""]
for entry in data.get("managed_users") or []:
    child = (entry.get("child_name") or "").strip()
    if not child:
        continue
    # Built from the same topic_prefix the daemon uses, so the ACL can never
    # quietly disagree with what the daemon actually publishes/subscribes.
    prefix = (entry.get("topic_prefix") or f"screen/{child}").strip().rstrip("/")
    lines.append(f"# --- {child} on this machine ---")
    if not (prefix == f"screen/{child}" or prefix.startswith(f"screen/{child}/")):
        lines.append(f"# !! WARNING: topic_prefix '{prefix}' doesn't match child_name '{child}'.")
        lines.append("# !! HA won't see this kid. Fix topic_prefix in config.json and rerun.")
    lines.append(f"topic readwrite {prefix}/mac/{device_id}/#")
    lines.append(f"topic read {prefix}/allowed")
    lines.append(f"topic read {prefix}/mac/+/minutes_today")
    lines.append(f"topic write {prefix}/total_minutes_today")
    lines.append(f"topic read homeassistant/{child}_shared/+/state")
    lines.append("")
lines.append("topic write homeassistant/+/+/config")
print("\n".join(lines))
PY
)"

ACL_SNIPPET_PATH="$AGENT_DIR/mosquitto_acl_snippet.txt"
printf '%s\n' "$ACL_SNIPPET" > "$ACL_SNIPPET_PATH"
chmod 0644 "$ACL_SNIPPET_PATH"

echo ""
echo "Add this to your Mosquitto broker's ACL file (all 3 Macs share ONE"
echo "accesscontrollist — this is only this machine's block, append it,"
echo "don't replace the file):"
echo "------------------------------------------------------------"
printf '%s\n' "$ACL_SNIPPET"
echo "------------------------------------------------------------"
echo "Saved for later at: $ACL_SNIPPET_PATH"

# Best-effort clipboard copy. Root has no GUI session of its own — same
# class of problem this project already hit with pmset (fixed by
# targeting the console user via launchctl asuser) and with audio (asuser
# wasn't even enough there, needed a whole companion agent). Unconfirmed
# whether asuser is sufficient for pbcopy specifically — attempted, not
# guaranteed; the screen output and saved file above are the reliable
# fallback either way, so a failure here is silent and non-fatal.
CONSOLE_USER="$(stat -f%Su /dev/console 2>/dev/null || true)"
if [[ -n "$CONSOLE_USER" && "$CONSOLE_USER" != "root" ]]; then
    CONSOLE_UID="$(id -u "$CONSOLE_USER" 2>/dev/null || true)"
    if [[ -n "$CONSOLE_UID" ]]; then
        if printf '%s\n' "$ACL_SNIPPET" | launchctl asuser "$CONSOLE_UID" pbcopy >/dev/null 2>&1; then
            echo "(Also attempted to copy this to your clipboard — check with Cmd-V;"
            echo " if it's not there, use the printed text or saved file above.)"
        fi
    fi
fi

# --- Blueprint delivery ---------------------------------------------------
BUDGET_BLUEPRINT="$PROJECT_DIR/homeassistant/blueprints/kid_mac_budget_enforcement.yaml"
RESET_BLUEPRINT="$PROJECT_DIR/homeassistant/blueprints/kid_mac_daily_reset.yaml"

# HA's Import Blueprint dialog only accepts a URL, not pasted YAML — so
# the useful thing to hand back here is a ready-to-paste raw GitHub URL,
# not the file contents. Derived from this checkout's own git remote so
# it's correct for whoever's actually running this (their own fork),
# not hardcoded to one specific repo.
REMOTE_URL="$(git -C "$PROJECT_DIR" config --get remote.origin.url 2>/dev/null || true)"
CURRENT_BRANCH="$(git -C "$PROJECT_DIR" rev-parse --abbrev-ref HEAD 2>/dev/null || true)"
CURRENT_BRANCH=${CURRENT_BRANCH:-main}
RAW_BASE=""
case "$REMOTE_URL" in
    https://github.com/*.git)
        REPO_PATH="${REMOTE_URL#https://github.com/}"
        RAW_BASE="https://raw.githubusercontent.com/${REPO_PATH%.git}/${CURRENT_BRANCH}"
        ;;
    git@github.com:*.git)
        REPO_PATH="${REMOTE_URL#git@github.com:}"
        RAW_BASE="https://raw.githubusercontent.com/${REPO_PATH%.git}/${CURRENT_BRANCH}"
        ;;
esac
if [[ -n "$RAW_BASE" ]]; then
    BLUEPRINT_IMPORT_TEXT="Paste each URL into Import Blueprint:
     ${RAW_BASE}/homeassistant/blueprints/kid_mac_budget_enforcement.yaml
     ${RAW_BASE}/homeassistant/blueprints/kid_mac_daily_reset.yaml
   (Needs the repo public on GitHub. If it's private, copy the two files
   from this checkout into <HA config>/blueprints/automation/ instead,
   then reload automations.)"
else
    BLUEPRINT_IMPORT_TEXT="No GitHub URL found for this checkout, so copy these two files
   into <HA config>/blueprints/automation/ and reload automations:
     $BUDGET_BLUEPRINT
     $RESET_BLUEPRINT"
fi

# --- Home Assistant setup checklist ----------------------------------------
# Everything that still has to be done BY HAND in HA, filled in with this
# install's real kid names. The Macs create every entity themselves via MQTT
# discovery — including one device per kid (named after the kid) holding
# the shared Allowed / Daily Budget / Bonus Minutes / Max Bonus Minutes /
# Parent Override / Total Minutes Today — so no helpers or template sensors
# are needed. Depends only on kid names, not on this Mac, so every Mac
# prints the same list: do it once per household, not once per Mac.
HA_CHECKLIST="$(
    CONFIG_PATH="$CONFIG_PATH" BLUEPRINT_IMPORT_TEXT="$BLUEPRINT_IMPORT_TEXT" "$PYTHON_BIN" - <<'PY'
import json, os
with open(os.environ["CONFIG_PATH"]) as fh:
    data = json.load(fh)
kids = []
for e in data.get("managed_users") or []:
    child = (e.get("child_name") or "").strip()
    if child:
        prefix = (e.get("topic_prefix") or f"screen/{child}").rstrip("/")
        kids.append((child, prefix))

out = []
out.append("Do this ONCE for the household (every Mac prints the same list).")
out.append("Every entity below is created automatically by the Macs. Each kid gets")
out.append("a device named after them (Settings > Devices & Services > MQTT) that")
out.append("holds that kid's shared entities; each Mac also gets its own device.")
out.append("")
out.append("=== 1. Import the two blueprints ===")
out.append("Settings > Automations & Scenes > Blueprints > Import Blueprint.")
out.append("   " + os.environ["BLUEPRINT_IMPORT_TEXT"])
out.append("")
for n, (child, prefix) in enumerate(kids, 2):
    out.append(f"=== {n}. {child} ===")
    out.append(f"A. Open the '{child}' device and set Daily Budget to {child}'s real")
    out.append("   daily minutes. (Max Bonus Minutes caps how much bonus counts; it's")
    out.append("   treated as 60 until you set it.)")
    out.append("B. Create Automation > Use blueprint > Kid Mac Budget Enforcement")
    out.append(f"     Total Minutes Today: {child} Total Minutes Today")
    out.append(f"     Daily Budget:        {child} Daily Budget")
    out.append(f"     Bonus Minutes:       {child} Bonus Minutes")
    out.append(f"     Max Bonus Minutes:   {child} Max Bonus Minutes")
    out.append(f"     Parent Override:     {child} Parent Override")
    out.append(f"     Allowed MQTT Topic:  {prefix}/allowed")
    out.append(f"   Save, then rename the automation to '{child} Budget Enforcement'")
    out.append("   (the blueprint's name field doesn't stick).")
    out.append("")
n = len(kids) + 2
out.append(f"=== {n}. Once, for all kids: daily reset ===")
out.append("Create Automation > Use blueprint > Kid Mac Daily Reset (all kids)")
out.append("  Allowed Switches:          '<kid> Allowed' for every kid")
out.append("  Parent Override Switches:  '<kid> Parent Override' for every kid")
out.append("  Bonus Minutes Numbers:     '<kid> Bonus Minutes' for every kid")
out.append("")
out.append(f"=== {n + 1}. After the first install ===")
out.append("- Flip each kid's Parent Override on and off once, so its state is saved.")
out.append("")
out.append("Current limitations to know about:")
out.append("- If a Mac goes offline, its last reported minutes stay counted in the")
out.append("  kid's Total Minutes Today until it reconnects (errs toward less time).")
out.append("- A Mac that loses its connection doesn't yet enforce the budget by")
out.append("  itself: it keeps the last Allowed value it received until it")
out.append("  reconnects (root_daemon_fail_mode only applies if it never received one).")
print("\n".join(out))
PY
)"
HA_CHECKLIST_PATH="$AGENT_DIR/ha_setup_checklist.txt"
printf '%s\n' "$HA_CHECKLIST" > "$HA_CHECKLIST_PATH"
chmod 0644 "$HA_CHECKLIST_PATH"

echo ""
echo "============================================================"
echo "HOME ASSISTANT SETUP — everything you do by hand"
echo "============================================================"
printf '%s\n' "$HA_CHECKLIST"
echo "------------------------------------------------------------"
echo "Saved for later at: $HA_CHECKLIST_PATH"

cat <<EOF

------------------------------------------------------------
Root daemon installed to : $DAEMON_PATH
Voice helper installed to: $HELPER_PATH
Config location          : $CONFIG_PATH
LaunchDaemon              : $DAEMON_PLIST_PATH
LaunchAgent (voice helper): $HELPER_PLIST_PATH

Next steps:
  1. Add this Mac's ACL block (printed above, saved at
     $ACL_SNIPPET_PATH) to the broker's accesscontrollist, replacing
     any older block for this Mac, then restart Mosquitto.
  2. Follow the Home Assistant checklist above (once per household;
     skip it on the second and later Macs if it's already done).
  3. Confirm the daemon is running (a PID means running):
       sudo launchctl list | grep com.ha.screen-daemon
       sudo tail -50 /var/log/root_daemon_skeleton.log
  4. Toggle a kid's Allowed switch in Home Assistant to confirm enforcement.
  5. To remove everything cleanly later, run: sudo ./scripts/uninstall.sh
------------------------------------------------------------
EOF
