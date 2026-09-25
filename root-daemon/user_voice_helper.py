#!/usr/bin/env python3
"""
Per-user LaunchAgent companion to root_daemon_skeleton.py: plays voice
warnings natively inside each managed kid's own session.

WHY THIS EXISTS: root_daemon_skeleton.py runs as root, with no GUI
session of its own. Extensive real-hardware testing (see
Context/HANDOFF.md in the umbrella repo, "Voice warnings: UNRESOLVED")
confirmed that no mechanism tried from that context — `launchctl asuser`,
`sudo -u` (from an already-root caller), with every combination of output
redirection, phrase length, foreground/background target, and
Accessibility permission — reliably produces audio when targeting
another user's session. This sidesteps the problem entirely: this helper
runs NATIVELY inside the target kid's own session (same as the original
per-kid agent always did, which is exactly why its own audio always
worked reliably), so `say` just works normally — no privilege-crossing,
no session boundary to cross at all.

Install as a per-user LaunchAgent (loads into EVERY GUI session
automatically via /Library/LaunchAgents/, same pattern as
screentime_enforcer.py's own install) — self-determines whether it's
relevant by checking if the CURRENT logged-in user matches a
managed_users entry in the same config.json the root daemon reads, and
idles harmlessly (exits cleanly) if not (e.g. the admin account).

Listens on {topic_prefix}/mac/{device_id}/voice_command and speaks
whatever plain-text payload arrives, and on .../lock_command, which locks
this session. Both unretained, no discovery needed (command channels, not
state values with their own HA entity).

Locking lives here for the same reason audio does: from inside the
session, SACLockScreenImmediate (the private login.framework call behind
the menu's Lock Screen command) gives a real, password-required lock
regardless of the account's "require password after sleep" setting, with
no Accessibility permission. The root daemon can only blank the display,
which is a real lock only when that setting is on. If this helper isn't
running, the daemon falls back to that.
"""

from __future__ import annotations

import ctypes
import getpass
import json
import logging
import os
from pathlib import Path
import subprocess

import paho.mqtt.client as mqtt  # type: ignore

logger = logging.getLogger("user-voice-helper")

CONFIG_PATH = "/Library/Application Support/ha-screen-agent/config.json"

# ~/Library/Logs, not /var/log: this runs as an unprivileged per-user
# LaunchAgent (bootstrapped into gui/<uid>), and /var/log is root:wheel —
# not writable by a regular user (confirmed: a non-root `touch` there gets
# Permission denied). ~/Library/Logs is the standard per-user location,
# always writable by that user, and — unlike /tmp — survives reboots.
LOG_PATH = Path("~/Library/Logs/ha-user-voice-helper/helper.log").expanduser()


def _setup_logging() -> None:
    LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    handler = logging.FileHandler(LOG_PATH)
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logging.basicConfig(level=logging.INFO, handlers=[handler])


def _sanitize_device_id(value: str) -> str:
    """Matches screentime_enforcer.py's / root_daemon_skeleton.py's
    _sanitize_device_id exactly — same file, same transform, needed so
    this helper subscribes to the exact topic the root daemon publishes
    to."""
    sanitized = "".join(ch if ch.isalnum() or ch in "-_" else "-" for ch in value.lower())
    return sanitized or "mac"


def speak_locally(text: str) -> None:
    """Runs natively inside this session — no subprocess user-switching
    needed at all, unlike everything root_daemon_skeleton.py tried."""
    try:
        subprocess.run(["/usr/bin/say", text], check=True)
    except (subprocess.CalledProcessError, OSError):
        logger.warning("Failed to play voice alert.", exc_info=True)


LOGIN_FRAMEWORK = "/System/Library/PrivateFrameworks/login.framework/Versions/Current/login"


def lock_locally() -> None:
    """Locks THIS session — only if this user is actually at the console.
    The daemon only ever asks the console kid's helper, but a backgrounded
    session's helper must never lock whoever is really in front of the Mac."""
    try:
        if os.stat("/dev/console").st_uid != os.getuid():
            logger.warning("Lock requested but this user isn't at the console; ignoring.")
            return
        ctypes.CDLL(LOGIN_FRAMEWORK).SACLockScreenImmediate()
        logger.info("Locked the screen.")
    except Exception:
        logger.warning("Failed to lock the screen.", exc_info=True)


def main() -> None:
    _setup_logging()
    mac_user = getpass.getuser()

    with open(CONFIG_PATH, "r", encoding="utf-8") as fh:
        data = json.load(fh)

    entry = next(
        (
            e
            for e in data.get("managed_users", [])
            if e.get("mac_user_account") == mac_user
        ),
        None,
    )
    if entry is None:
        logger.info(
            "'%s' is not a managed account — nothing to do here, exiting.", mac_user
        )
        return

    child = entry["child_name"]
    topic_prefix = entry.get("topic_prefix") or f"screen/{child}"
    device_id = _sanitize_device_id(data["device_id"])
    voice_topic = f"{topic_prefix}/mac/{device_id}/voice_command"
    lock_topic = f"{topic_prefix}/mac/{device_id}/lock_command"

    def on_connect(client, userdata, flags, reason_code, properties=None):
        rc = int(getattr(reason_code, "value", reason_code))
        if rc != 0:
            logger.error("MQTT connection failed (rc=%s).", rc)
            return
        logger.info("Connected to MQTT. Subscribed to %s and %s.", voice_topic, lock_topic)
        client.subscribe(voice_topic)
        client.subscribe(lock_topic)

    def on_disconnect(client, userdata, disconnect_flags, reason_code, properties=None):
        rc = int(getattr(reason_code, "value", reason_code))
        if rc != 0:
            logger.warning("Unexpected MQTT disconnect (rc=%s).", rc)

    def on_message(client, userdata, message):
        if message.topic == lock_topic:
            lock_locally()
            return
        text = (message.payload or b"").decode("utf-8", errors="ignore").strip()
        if not text:
            return
        logger.info("Speaking: %s", text)
        speak_locally(text)

    client = mqtt.Client(
        callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
        client_id=f"ha-user-voice-helper-{device_id}-{child}",
        protocol=mqtt.MQTTv311,
        clean_session=True,
    )
    if data.get("mqtt_username"):
        client.username_pw_set(data["mqtt_username"], password=data.get("mqtt_password"))
    if data.get("mqtt_tls"):
        client.tls_set()
    client.on_connect = on_connect
    client.on_disconnect = on_disconnect
    client.on_message = on_message

    logger.info(
        "Connecting to MQTT %s:%s as '%s' (managed child '%s').",
        data["mqtt_host"],
        data.get("mqtt_port", 1883),
        mac_user,
        child,
    )
    client.connect(data["mqtt_host"], int(data.get("mqtt_port", 1883)), keepalive=60)
    client.loop_forever()  # blocks; paho reconnects automatically on drop


if __name__ == "__main__":
    main()
