#!/usr/bin/env python3
"""
Skeleton for the root-level, multi-user-aware Screen Time daemon.

This is NOT a drop-in replacement for screentime_enforcer.py yet — it's a
starting skeleton for the specific new piece the per-user LaunchAgent
architecture can't do: knowing WHO is currently at the console, from a
single always-running, root-owned process, without depending on macOS
bootstrapping a separate agent per account.

Design goals carried over from the requirements doc (project plan, Section 13):
  - One process per machine (not one per managed user)
  - Detect the active console user directly (SCDynamicStore), not via
    per-user LaunchAgent bootstrap
  - Track each managed kid's ACTIVE vs PAUSED state for time accounting —
    a kid only burns budget while they are both (a) the console user and
    (b) unlocked. Backgrounded (fast-user-switched away) or screen-locked
    both pause the countdown, since neither means they're using the
    machine right now. This is the practical shape requirement #3 ended
    up taking: rather than actively enforcing a lock on every backgrounded
    managed session, correct accounting plus locking whoever *becomes* the
    console user while over budget (already covered by the console-user
    branch below) closes the fast-user-switch bypass without the added
    complexity of reaching into another session to lock it remotely.
    Residual edge case, accepted as out of scope: a blocked kid can still
    switch into an already-unlocked SIBLING session and use their time
    instead — a social/policy problem between siblings, not an
    enforcement gap.
  - Reuse the existing lock/kill/rapid-relogin/voice logic from
    screentime_enforcer.py once this loop is proven out — this file only
    covers the NEW piece (session + lock-state detection), not a full
    reimplementation
  - One MQTT identity per machine (not per kid) — matches the ACL model
    already in place

Run this manually first (as root, via `sudo python3 root_daemon_skeleton.py`)
to validate console-user detection on real hardware before wiring it into a
LaunchDaemon plist. Console user detection is the part most worth proving
out first, since it's the actual new capability this rewrite depends on.
"""

from __future__ import annotations

import json
import plistlib
import subprocess
import time
import logging
from datetime import datetime
from pathlib import Path
from typing import Optional

# SCDynamicStore gives us the actual logged-in console user, correctly
# updated across logout/login and fast-user-switch, without needing a
# per-user LaunchAgent to tell us. This is the load-bearing piece.
from SystemConfiguration import (  # type: ignore
    SCDynamicStoreCreate,
    SCDynamicStoreCopyValue,
)

# Same paho-mqtt client the original per-user agent uses (screentime_enforcer.py),
# reused nearly unchanged per the handoff. NOTE: this needs to be run with the
# production venv's interpreter (/Library/Application Support/ha-screen-agent/venv/bin/python3),
# not plain `sudo python3` — paho-mqtt is installed under the invoking user's
# site-packages, which root (via sudo) can't see.
import paho.mqtt.client as mqtt  # type: ignore

# Screen-lock detection: see ScreenLockTracker below for why this reads
# ioreg's IOConsoleUsers rather than a Quartz/notification-based approach.

# Also logs to a file (world-readable, since this runs as root but is
# meant to be inspected afterward as a regular user) so test output can
# be reviewed after the fact without needing to watch the SSH session
# live. /var/log, NOT /tmp — confirmed the hard way: macOS clears /tmp
# on every reboot, which silently destroyed the log from the one test
# that actually triggered a real reboot (the rapid-relogin shutdown
# test). /var/log survives reboots, and matches where the real installed
# daemon's plist (com.ha.screen-daemon.plist) already expects its own
# logs to live.
LOG_FILE_PATH = "/var/log/root_daemon_skeleton.log"

_file_handler = logging.FileHandler(LOG_FILE_PATH)
_file_handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    handlers=[logging.StreamHandler(), _file_handler],
)
logger = logging.getLogger("root-screen-daemon")

try:
    import os

    os.chmod(LOG_FILE_PATH, 0o644)
except OSError:
    pass

CONSOLE_USER_KEY = "State:/Users/ConsoleUser"
POLL_INTERVAL_SECONDS = 2.0


def get_console_user() -> Optional[str]:
    """
    Returns the short username of whoever currently owns the console
    session (i.e., whose desktop is actually being displayed right now),
    or None if nobody is logged in (e.g., at the login window).

    This is the same mechanism macOS itself uses to track fast-user-switch
    state — it updates immediately on switch, logout, or login, with no
    polling delay beyond however often we choose to check it here.
    """
    store = SCDynamicStoreCreate(None, "root-screen-daemon", None, None)
    value = SCDynamicStoreCopyValue(store, CONSOLE_USER_KEY)
    if value is None:
        return None
    # Value is a CFDictionary; the console username is under key "Name".
    # Root/system session sometimes reports as "loginwindow" — treat that
    # the same as "nobody logged in" for our purposes.
    name = value.get("Name")
    if not name or name == "loginwindow":
        return None
    return str(name)


def is_screen_locked() -> bool:
    """
    True if the ACTUAL PHYSICAL CONSOLE session is currently locked,
    regardless of which session this process itself belongs to.

    Two prior approaches were tried and both failed on real hardware,
    consistent with the same root cause: this daemon is meant to run
    outside any particular user's GUI session (root, no session of its
    own — confirmed here via SSH, which is architecturally the same
    situation a real LaunchDaemon is in), and macOS's session-scoped IPC
    mechanisms don't cross that boundary:

    1. CGSessionCopyCurrentDictionary (Quartz) only reflected lock state
       for whichever session the calling process's own identity happened
       to be tied to (our own SSH login) — never fired for other
       accounts' sessions.
    2. NSDistributedNotificationCenter, listening for
       com.apple.screenIsLocked/screenIsUnlocked: registered with no
       error, but never received anything posted from a *different*
       audit session than our own — distributed notifications appear not
       to cross audit-session boundaries by default, same class of
       problem as #1, different mechanism.

    This third approach reads IOConsoleUsers from the IOKit registry via
    `ioreg` instead — a kernel registry, not session-scoped IPC, so it
    isn't subject to either failure mode. Confirmed manually on real
    hardware: readable as a plain unprivileged, non-GUI process with no
    special session context, correctly describing the actual console
    session. Simple poll, not a callback — no run loop needed.
    """
    try:
        output = subprocess.check_output(["ioreg", "-n", "Root", "-d1", "-a"])
        data = plistlib.loads(output)
    except (subprocess.CalledProcessError, ValueError):
        logger.exception("Failed to read/parse ioreg IOConsoleUsers.")
        return True

    for user in data.get("IOConsoleUsers", []):
        if user.get("kCGSSessionOnConsoleKey"):
            return bool(user.get("CGSSessionScreenIsLocked", False))
    return True  # no console session at all (e.g. login window) -> locked


def get_all_logged_in_sessions() -> dict[str, int]:
    """
    Returns {username: uid} for every macOS account that currently has an
    active GUI login session on this machine — including sessions that are
    logged in but sitting backgrounded via fast user switching, not just
    whoever is at the console right now.

    Not used for enforcement (see module docstring — we don't reach into
    backgrounded sessions to lock them). This is here for visibility only:
    logging/debugging which managed kids are logged in but not currently
    being tracked for time, and as a hook point if a future requirement
    genuinely needs it. Each GUI login session, console or backgrounded,
    runs its own `loginwindow` process under that account's UID for as
    long as the session is alive, so enumerating those processes gives us
    every logged-in session on the machine in one shot.
    """
    sessions: dict[str, int] = {}
    try:
        output = subprocess.check_output(
            ["ps", "-Ao", "user,uid,comm"], text=True
        )
    except subprocess.CalledProcessError:
        logger.exception("Failed to enumerate processes for session detection.")
        return sessions

    for line in output.splitlines()[1:]:
        parts = line.split(None, 2)
        if len(parts) < 3:
            continue
        user, uid, comm = parts
        if comm.endswith("loginwindow.app/Contents/MacOS/loginwindow"):
            try:
                sessions[user] = int(uid)
            except ValueError:
                continue
    return sessions


def _now_local() -> datetime:
    """Matches screentime_enforcer.py's _now_local exactly."""
    return datetime.now().astimezone()


def usage_state_path(child: str) -> str:
    """
    One state file per kid, root-owned — NOT screentime_enforcer.py's
    per-instance `state_path` (which lives under that kid's own home
    directory, since the original agent runs natively in their session).
    This daemon runs as root for every kid at once, so their state files
    live together in one root-owned location instead.
    """
    return f"/Library/Application Support/ha-screen-agent/root-daemon-state/{child}.json"


class UsageState:
    """
    Matches screentime_enforcer.py's UsageState class closely (same file
    format, same atomic-write pattern, same local-midnight reset logic),
    generalized to one instance per kid instead of one per agent process.

    This is the actual fix for the confusing behavior found during
    testing 2026-09-17: accumulated usage was tracked in-memory only, so
    it never reset and had no relationship to "minutes used TODAY" in the
    way a parent would expect — a long testing session could leave a kid
    looking like they'd already used most of a freshly-set small budget,
    purely from earlier unrelated testing activity. Persisting to disk
    with a real local-date check fixes both problems: survives daemon
    restarts, and actually resets at local midnight rather than only at
    process start.
    """

    def __init__(self, path: Path) -> None:
        self.path = path
        self._data = {
            "date": _now_local().date().isoformat(),
            "seconds_today": 0.0,
        }
        self._load()

    def _load(self) -> None:
        try:
            with self.path.open("r", encoding="utf-8") as handle:
                data = json.load(handle)
            if data.get("date") == _now_local().date().isoformat():
                self._data = {
                    "date": data.get("date"),
                    "seconds_today": float(data.get("seconds_today", 0.0)),
                }
        except FileNotFoundError:
            pass
        except (json.JSONDecodeError, ValueError, TypeError, OSError):
            logger.warning(
                "Failed to read state file %s, starting fresh.", self.path, exc_info=True
            )

    def add_seconds(self, seconds: float) -> None:
        self._data["seconds_today"] = float(self._data.get("seconds_today", 0.0)) + max(
            0.0, seconds
        )

    def minutes_today(self) -> int:
        return int(self._data.get("seconds_today", 0.0) // 60)

    def ensure_today(self) -> None:
        """Call every poll tick, for every kid (not just whoever's
        active) — matches the original's ensure_today, needed so a kid
        who's backgrounded (not accruing time right now) still gets reset
        correctly if the daemon happens to be running across local
        midnight while they're not the one active."""
        today = _now_local().date().isoformat()
        if self._data.get("date") != today:
            self._data = {"date": today, "seconds_today": 0.0}
            self.save()

    def save(self) -> None:
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            tmp_path = self.path.with_suffix(".tmp")
            with tmp_path.open("w", encoding="utf-8") as handle:
                json.dump(self._data, handle)
            tmp_path.replace(self.path)
        except OSError:
            logger.error("Unable to persist usage state to %s.", self.path, exc_info=True)


class ManagedUserRegistry:
    """
    Loads which macOS accounts on this machine correspond to which managed
    kids. This intentionally mirrors the existing `managed_users` schema
    from screentime_enforcer.py's config.json, so migrating a machine's
    config from the per-user agent to this daemon is a matter of pointing
    at the same file, not redesigning the schema from scratch.
    """

    def __init__(self, config_path: str):
        self.config_path = config_path
        self._by_mac_user: dict[str, dict] = {}
        self._by_child: dict[str, dict] = {}
        self.reload()

    def reload(self) -> None:
        with open(self.config_path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
        entries = data.get("managed_users", [])
        self._by_mac_user = {
            entry["mac_user_account"]: entry
            for entry in entries
            if "mac_user_account" in entry and "child_name" in entry
        }
        self._by_child = {
            entry["child_name"]: entry
            for entry in entries
            if "child_name" in entry
        }
        logger.info(
            "Loaded %d managed user mapping(s): %s",
            len(self._by_mac_user),
            list(self._by_mac_user.keys()),
        )

    def child_for(self, mac_user: str) -> Optional[str]:
        entry = self._by_mac_user.get(mac_user)
        return entry["child_name"] if entry else None

    def topic_prefix_for(self, child_name: str) -> Optional[str]:
        entry = self._by_child.get(child_name)
        return entry.get("topic_prefix") if entry else None

    def mac_user_for(self, child_name: str) -> Optional[str]:
        entry = self._by_child.get(child_name)
        return entry.get("mac_user_account") if entry else None

    def all_children(self) -> list[str]:
        return list(self._by_child.keys())


def _sanitize_device_id(value: str) -> str:
    """
    Matches screentime_enforcer.py's _sanitize_device_id exactly (same
    file, same transform) — critical, not cosmetic: topics built from an
    unsanitized device_id land on entirely different MQTT topics than the
    ones HA's existing entities are actually subscribed to. Confirmed on
    real hardware: config.json's raw device_id is "MacbookProM1" (mixed
    case), but every existing retained topic on the broker uses the
    lowercased "macbookprom1" — publishing without this sanitization step
    silently wrote to a parallel set of topics nothing was listening to.
    """
    sanitized = "".join(ch if ch.isalnum() or ch in "-_" else "-" for ch in value.lower())
    return sanitized or "mac"


class DaemonMqttConfig:
    """
    The top-level (device-wide) MQTT fields from config.json — separate
    from ManagedUserRegistry, which only cares about the per-kid
    managed_users entries. Same config file, two narrow readers, rather
    than one class doing both jobs.
    """

    def __init__(
        self,
        device_id: str,
        mqtt_host: str,
        mqtt_port: int,
        mqtt_username: Optional[str],
        mqtt_password: Optional[str],
        mqtt_tls: bool,
        sample_interval_seconds: float,
        fail_mode: str,
        fail_grace_minutes: float,
        rapid_relogin_shutdown_enabled: bool,
        rapid_relogin_window_seconds: float,
        rapid_relogin_max_attempts: int,
        rapid_relogin_warn_attempt: int,
        rapid_relogin_warn_voice: bool,
    ) -> None:
        self.device_id = _sanitize_device_id(device_id)
        self.mqtt_host = mqtt_host
        self.mqtt_port = mqtt_port
        self.mqtt_username = mqtt_username
        self.mqtt_password = mqtt_password
        self.mqtt_tls = mqtt_tls
        self.sample_interval_seconds = sample_interval_seconds
        self.fail_mode = fail_mode
        self.fail_grace_minutes = fail_grace_minutes
        self.rapid_relogin_shutdown_enabled = rapid_relogin_shutdown_enabled
        self.rapid_relogin_window_seconds = rapid_relogin_window_seconds
        self.rapid_relogin_max_attempts = rapid_relogin_max_attempts
        self.rapid_relogin_warn_attempt = rapid_relogin_warn_attempt
        self.rapid_relogin_warn_voice = rapid_relogin_warn_voice

    @classmethod
    def load(cls, config_path: str) -> "DaemonMqttConfig":
        with open(config_path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
        # root_daemon_fail_mode is intentionally SEPARATE from the shared
        # `fail_mode` key screentime_enforcer.py also reads from this same
        # file — that loader raises ValueError on anything but "safe"/
        # "open", so writing a third value ("grace") into the shared key
        # would crash the still-running production per-user agent on its
        # next restart. Defaults to mirroring `fail_mode` so behavior is
        # unchanged unless explicitly opted into.
        fail_mode = str(
            data.get("root_daemon_fail_mode", data.get("fail_mode", "safe"))
        ).lower()
        if fail_mode not in {"safe", "open", "grace"}:
            fail_mode = "safe"
        return cls(
            device_id=data["device_id"],
            mqtt_host=data["mqtt_host"],
            mqtt_port=int(data.get("mqtt_port", 1883)),
            mqtt_username=data.get("mqtt_username"),
            mqtt_password=data.get("mqtt_password"),
            mqtt_tls=bool(data.get("mqtt_tls", False)),
            sample_interval_seconds=float(data.get("sample_interval_seconds", 15)),
            fail_mode=fail_mode,
            fail_grace_minutes=float(data.get("root_daemon_fail_grace_minutes", 120)),
            # rapid_relogin_shutdown_enabled/window_seconds/warn_voice: read
            # directly under their original screentime_enforcer.py names —
            # these are plain settings valid for both tools, no conflict.
            # max_attempts/warn_attempt get root_daemon_-prefixed overrides
            # (same pattern as fail_mode above), since these are the ones
            # actually worth deliberately tuning for a one-off test (e.g.
            # a lower threshold to verify the shutdown path actually
            # fires) without silently changing the still-installed old
            # agent's real production behavior if it's later re-enabled
            # without remembering to revert this file.
            rapid_relogin_shutdown_enabled=bool(
                data.get("rapid_relogin_shutdown_enabled", True)
            ),
            rapid_relogin_window_seconds=float(data.get("rapid_relogin_window_seconds", 60)),
            rapid_relogin_max_attempts=int(
                data.get(
                    "root_daemon_rapid_relogin_max_attempts",
                    data.get("rapid_relogin_max_attempts", 4),
                )
            ),
            rapid_relogin_warn_attempt=int(
                data.get(
                    "root_daemon_rapid_relogin_warn_attempt",
                    data.get("rapid_relogin_warn_attempt", 3),
                )
            ),
            rapid_relogin_warn_voice=bool(data.get("rapid_relogin_warn_voice", True)),
        )


# Topic scheme matches screentime_enforcer.py's AgentConfig properties
# exactly (see minutes_topic/active_topic/availability_topic there), so
# these land on the SAME HA entities the existing per-kid agent already
# published discovery config for — no new discovery messages needed here.
def minutes_topic(topic_prefix: str, device_id: str) -> str:
    return f"{topic_prefix}/mac/{device_id}/minutes_today"


def active_topic(topic_prefix: str, device_id: str) -> str:
    return f"{topic_prefix}/mac/{device_id}/active"


def availability_topic(topic_prefix: str, device_id: str) -> str:
    return f"{topic_prefix}/mac/{device_id}/availability"


def allow_topic(topic_prefix: str) -> str:
    """Matches screentime_enforcer.py's allow_topic exactly: {prefix}/allowed
    — NOT device-scoped, since it's a per-kid decision HA/the parent makes,
    not something that varies by which Mac they're on."""
    return f"{topic_prefix}/allowed"


def daily_budget_topic(child: str, device_id: str) -> str:
    """Matches screentime_enforcer.py's budget_state_topic exactly:
    homeassistant/{child}_{device_id}_mac/daily_budget/state — this is the
    HA-managed `number` entity's own state/command topic (same topic for
    both), not something under our screen/... namespace."""
    return f"homeassistant/{child}_{device_id}_mac/daily_budget/state"


def _as_bool(payload: str) -> Optional[bool]:
    """Matches screentime_enforcer.py's _as_bool exactly."""
    normalized = payload.strip().lower()
    if normalized in {"1", "true", "on", "yes"}:
        return True
    if normalized in {"0", "false", "off", "no"}:
        return False
    return None


def resolve_allowed(
    child: str,
    allowed_state: dict,
    fail_mode: str,
    grace_started_at: dict,
    fail_grace_minutes: float,
) -> bool:
    """
    Extends screentime_enforcer.py's _current_allowed_state with a third
    mode ("grace") this daemon adds on top — the original only has
    "safe" (fail closed) and "open" (fail open indefinitely). Neither
    covers the actual incident from the handoff: a kid with NO seeded
    allowed value fails closed instantly, with no window for an operator
    to notice before rapid-relogin shutdown escalation kicks in (this
    happened for real, with aaron). "grace" is the fix: bounded temporary
    allowance instead of instant-lock or infinite-allow.

    - An explicit received value always wins, full stop.
    - No value received (never seeded, or connection lost): "open" -> True
      always; "safe" -> False always; "grace" -> True for
      fail_grace_minutes, timed PER KID from the moment THEY specifically
      hit this unknown state — not from whenever the underlying problem
      began — so whoever logs in gets their own full window regardless of
      how long the daemon's been up or MQTT's been down. Timer clears the
      moment a real value arrives, so any later lapse starts a fresh
      window rather than resuming an old countdown.
    """
    value = allowed_state.get(child)
    if value is not None:
        grace_started_at.pop(child, None)
        return value

    if fail_mode == "open":
        return True
    if fail_mode != "grace":
        return False  # "safe", or anything unrecognized -> fail closed

    started = grace_started_at.setdefault(child, time.monotonic())
    elapsed_minutes = (time.monotonic() - started) / 60.0
    return elapsed_minutes < fail_grace_minutes


def lock_session(uid: int, expected_mac_user: str) -> bool:
    """
    Locks the console session belonging to `uid`, from this process
    (root, no GUI session of its own) — via `launchctl asuser`, the
    standard mechanism for a privileged process to act inside a specific
    user's GUI session. Confirmed on real hardware (2026-09-14, against
    cj's actual session): `pmset displaysleepnow` run this way genuinely
    locks it — waking requires cj's password — given "require password
    after sleep" is already a documented hard requirement for every
    managed account, not new configuration this introduces.

    Deliberately NOT using screentime_enforcer.py's other fallbacks:
    - CGSession -suspend: the binary doesn't exist at all on macOS 26.6.2
      Tahoe (confirmed — Apple removed it), so it's not viable here
      regardless of which process calls it.
    - The System Events keyboard-shortcut (Ctrl+Cmd+Q via AppleScript):
      needs Accessibility permission, and it was genuinely unclear
      whether that's satisfied when invoked via launchctl asuser from
      root rather than from a process already running inside the kid's
      own session. pmset needs no such permission, so this sidesteps the
      question entirely rather than resolving it.
    - ScreenSaverEngine: same Accessibility-permission uncertainty
      doesn't apply, but it's a heavier action (launches an app) for the
      same effect pmset achieves directly; not tested since pmset already
      worked.

    IMPORTANT — confirmed on real hardware this is NOT actually
    session-scoped at the hardware level: there's one physical display,
    so `pmset displaysleepnow` blanks whatever's currently showing,
    regardless of which uid technically issued it via `launchctl asuser`
    (that only scopes the command's execution context, not the effect).
    Caught this for real: with FUS in play, a decision made from a
    console-user reading up to POLL_INTERVAL_SECONDS old could still fire
    after the console had already switched away, blanking the WRONG
    (now-current) session. Mitigated by re-checking get_console_user()
    immediately before the subprocess call — the tightest window
    practical, though not a hard guarantee (still a TOCTOU race, just a
    much smaller one). expected_mac_user is the account this call was
    decided for; if the console user has changed since, this aborts
    rather than blanking whoever's actually there now.

    Verified via OUR is_screen_locked() (ioreg/IOConsoleUsers), NOT
    screentime_enforcer.py's CGSessionCopyCurrentDictionary-based check —
    we already proved that one doesn't reliably reflect another session's
    lock state from this process's context.
    """
    current = get_console_user()
    if current != expected_mac_user:
        logger.warning(
            "Aborting lock for uid %d: console user changed from '%s' to "
            "'%s' since this was decided — would have blanked the wrong "
            "session.",
            uid,
            expected_mac_user,
            current,
        )
        return False

    try:
        subprocess.run(
            ["/bin/launchctl", "asuser", str(uid), "/usr/bin/pmset", "displaysleepnow"],
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    except (subprocess.CalledProcessError, OSError):
        logger.exception("Lock command failed for uid %d.", uid)
        return False

    time.sleep(0.4)  # give it a moment before checking, matches screentime_enforcer.py's own pacing
    if is_screen_locked():
        return True
    logger.warning(
        "Lock command ran for uid %d but the session doesn't show as locked yet.", uid
    )
    return False


# Only the English rapid-relogin warning phrases are ported for now —
# screentime_enforcer.py's full multi-language SUPPORTED_LANG_PHRASES
# table covers login/budget announcements too, which are the separate
# "voice warnings" work item, explicitly deprioritized below rapid-relogin
# shutdown escalation. This is scoped to just what the shutdown escalation
# itself needs.
RAPID_RELOGIN_WARN_VOICE_ONE = (
    "Warning. The next login attempt will shut down this computer. "
    "Make sure your parents have given you access."
)
RAPID_RELOGIN_WARN_VOICE_MANY = (
    "Warning. {count} more login attempts will shut down this computer. "
    "Make sure your parents have given you access."
)

# New — not in screentime_enforcer.py at all (its budget warnings only
# cover 5 and 1 minutes remaining, and it never announces a budget
# CHANGE, only the remaining time at login). Thresholds checked in
# descending order; BUDGET_WARNING_TEXT keys must match exactly.
#
# Three distinct budget-change phrasings, not one: the first value ever
# observed for a kid (this daemon run — last_announced_budget itself is
# NOT persisted, unlike actual usage minutes; see UsageState) gets the
# plain "set to" phrasing; any value that changes AFTER that gets
# increase/decrease-specific phrasing with the delta, so a kid can tell
# "my parent gave me more time" apart from "my parent's initial daily
# limit" without having to do the math themselves.
def _minutes_text(n: int) -> str:
    return "1 minute" if n == 1 else f"{n} minutes"


BUDGET_INITIAL_VOICE = "Your daily screen time limit has been set to {minutes}."
BUDGET_INCREASED_VOICE = (
    "Your parent has added {delta} to your daily limit. "
    "Your daily limit is now {minutes}."
)
BUDGET_DECREASED_VOICE = (
    "Your parent has decreased your daily limit by {delta}. "
    "Your daily limit is now {minutes}."
)
BUDGET_WARNING_THRESHOLDS = [15, 10, 5, 1]
BUDGET_WARNING_TEXT = {
    15: "15 minutes of screen time remaining.",
    10: "10 minutes of screen time remaining.",
    5: "5 minutes of screen time remaining.",
    1: "1 minute of screen time remaining.",
}


# New topic, matching the device-scoped pattern of minutes/active/
# session_state — a kid could in principle have a session backgrounded
# on more than one machine, so this stays per-device: only the helper
# actually running on THIS Mac should react.
def voice_command_topic(topic_prefix: str, device_id: str) -> str:
    return f"{topic_prefix}/mac/{device_id}/voice_command"


def speak(mqtt_client: mqtt.Client, topic_prefix: str, device_id: str, text: str) -> None:
    """
    Publishes a voice command for `user_voice_helper.py` (a per-user
    LaunchAgent, see that file) to speak — does NOT attempt to produce
    audio itself.

    Direct subprocess approaches (`launchctl asuser`, `sudo -u`, with
    every combination of redirection/phrase-length/target/Accessibility
    permission tried) were extensively tested on real hardware 2026-09-17
    and NONE reliably produced audio when invoked from this daemon (root,
    no GUI session of its own) targeting another user's session — see
    Context/HANDOFF.md, "Voice warnings: UNRESOLVED", before attempting
    another direct fix here. The actual fix is architectural: hand this
    off to a small helper that runs NATIVELY inside the target kid's own
    session (same as how the original per-kid agent's audio always
    worked reliably, since it never had to cross that boundary either),
    rather than trying to cross the privilege/session boundary directly.
    Not retained — this is an ephemeral command, not a state value.
    """
    mqtt_client.publish(
        voice_command_topic(topic_prefix, device_id), payload=text, qos=1, retain=False
    )


def shutdown_computer() -> None:
    """
    Forces a full system shutdown — the rapid-relogin escalation's last
    resort when a blocked kid keeps re-entering their password.

    Deliberately simpler than screentime_enforcer.py's _shutdown_computer:
    that one runs as an unprivileged per-user LaunchAgent, so it has to go
    through GUI-session mechanisms (osascript telling System Events/Finder
    to shut down, quitting blocking apps first so they don't cancel it
    with a "save changes?" dialog, falling back to force-logout) to
    accomplish something it has no direct permission to do itself. This
    daemon runs AS ROOT — it can just call `/sbin/shutdown` directly,
    which forces a shutdown without routing through the normal graceful
    app-quit sequence at all, sidestepping the "blocking app" problem
    those GUI-session mechanisms exist to work around. `launchctl reboot
    halt` as a fallback if that fails for some reason.

    NOT YET verified on real hardware — unlike everything else in this
    file, this hasn't been tested against the real machine (deliberately:
    it's destructive and only fires after repeated confirmed rapid-relogin
    attempts). Confirm the command paths/behavior in a low-stakes way
    before trusting this in anger.
    """
    logger.critical("Rapid relogin threshold reached. Initiating shutdown.")
    try:
        subprocess.run(["/sbin/shutdown", "-h", "now"], check=True)
        return
    except (subprocess.CalledProcessError, OSError):
        logger.exception("/sbin/shutdown failed, trying launchctl reboot halt.")

    try:
        subprocess.run(["/bin/launchctl", "reboot", "halt"], check=True)
    except (subprocess.CalledProcessError, OSError):
        logger.exception("launchctl reboot halt also failed. Giving up on shutdown.")


# New topic — not part of screentime_enforcer.py's scheme, since the
# per-user agent never needed it (it only ever ran while ITS OWN kid was
# active; there was no "check another kid's state" concept). This one
# reports one of "active" | "locked" | "backgrounded" | "offline" for
# EVERY managed kid on this device, not just whoever's currently active —
# richer visibility than the boolean `active` topic alone.
def session_state_topic(topic_prefix: str, device_id: str) -> str:
    return f"{topic_prefix}/mac/{device_id}/session_state"


ROOT_DAEMON_VERSION = "0.1.0-skeleton"


def _discovery_device(child: str, device_id: str) -> dict:
    """Same identifiers screentime_enforcer.py's _discovery_device uses,
    so this groups under the SAME existing device card in HA rather than
    creating a duplicate."""
    return {
        "identifiers": [f"{child}_{device_id}_mac"],
        "name": f"{child} mac",
        "manufacturer": "Screen Time Agent",
        "model": "macOS agent",
        "sw_version": ROOT_DAEMON_VERSION,
    }


def build_mqtt_client(
    mqtt_config: DaemonMqttConfig,
    registry: ManagedUserRegistry,
    allowed_state: dict,
    budget_state: dict,
) -> mqtt.Client:
    """
    ONE client for the whole machine (requirement #4), not one per kid —
    the actual architectural point of this rewrite. client_id is keyed by
    device only.

    allowed_state/budget_state are plain dicts the caller owns — this
    function's on_message callback writes into them (from paho's
    background thread, via loop_start()) and main()'s poll loop reads
    from them (main thread). No lock around that: matches the original
    agent's own informal thread-safety model (self._allowed touched from
    both threads there too), fine for simple last-write-wins on a single
    value per key.
    """
    client_id = f"ha-root-daemon-{mqtt_config.device_id}"
    client = mqtt.Client(
        callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
        client_id=client_id,
        protocol=mqtt.MQTTv311,
        clean_session=True,
    )
    if mqtt_config.mqtt_username:
        client.username_pw_set(
            mqtt_config.mqtt_username, password=mqtt_config.mqtt_password or None
        )
    if mqtt_config.mqtt_tls:
        client.tls_set()

    def on_connect(client, userdata, flags, reason_code, properties=None):
        rc = int(getattr(reason_code, "value", reason_code))
        if rc != 0:
            logger.error("MQTT connection failed (rc=%s).", rc)
            return
        logger.info("Connected to MQTT broker.")
        # One daemon covers every managed kid, not just whoever's active
        # right now — so on connect, mark ALL of them online, not just
        # one. (No per-connection LWT covering all of them yet: paho only
        # supports a single last-will topic per client, so a hard crash
        # won't flip these back to offline automatically. Best-effort for
        # now; publishing "offline" happens explicitly on clean shutdown
        # below. Revisit if crash-detection turns out to matter here.)
        for child in registry.all_children():
            prefix = registry.topic_prefix_for(child)
            if not prefix:
                continue
            # Retained topic — subscribing delivers the current value
            # immediately via on_message below, same as the original
            # agent's own _on_connect subscribe.
            client.subscribe(allow_topic(prefix))
            client.subscribe(daily_budget_topic(child, mqtt_config.device_id))
            client.publish(
                availability_topic(prefix, mqtt_config.device_id),
                payload="online",
                retain=True,
                qos=1,
            )
            # session_state discovery — new sensor, not part of the
            # original agent's scheme (see session_state_topic above for
            # why). Re-publishing discovery on every connect is cheap and
            # idempotent (retained, same payload), so no "already
            # published" guard needed like the original agent's
            # _discovery_published flag.
            base_id = f"{child}_{mqtt_config.device_id}_mac"
            discovery_topic = f"homeassistant/sensor/{base_id}_session_state/config"
            discovery_payload = {
                "name": f"{child} Mac Session State",
                "unique_id": f"{base_id}_session_state",
                "state_topic": session_state_topic(prefix, mqtt_config.device_id),
                "icon": "mdi:account-clock",
                "device": _discovery_device(child, mqtt_config.device_id),
            }
            client.publish(
                discovery_topic, json.dumps(discovery_payload), retain=True, qos=1
            )

            # Fix for the existing "allowed" switch's discovery config:
            # screentime_enforcer.py's original definition never sets
            # "retain": true, so HA's MQTT switch integration doesn't
            # retain the command it publishes when a PARENT manually
            # toggles it in the UI (confirmed on the real broker:
            # screen/cj/allowed's retained value was stale "1" even with
            # the switch showing off in HA, because the manual toggle was
            # never retained). Republishing the SAME unique_id/topics
            # here, with retain added, updates HA's existing entity in
            # place — no duplicate entity, no change from HA's side.
            # Existing stale retained values aren't fixed retroactively by
            # this alone; toggling the switch once after this deploys
            # will correctly retain going forward.
            allowed_discovery_topic = f"homeassistant/switch/{base_id}_allowed/config"
            allowed_discovery_payload = {
                "name": f"{child} Mac Allowed",
                "unique_id": f"{base_id}_allowed",
                "state_topic": allow_topic(prefix),
                "command_topic": allow_topic(prefix),
                "payload_on": "1",
                "payload_off": "0",
                "retain": True,
                "icon": "mdi:shield-check",
                "device": _discovery_device(child, mqtt_config.device_id),
            }
            client.publish(
                allowed_discovery_topic,
                json.dumps(allowed_discovery_payload),
                retain=True,
                qos=1,
            )

            # Same retain fix, same reasoning, for the daily_budget number
            # entity — confirmed on the real broker this topic currently
            # has NO retained value at all for cj, consistent with the
            # same missing "retain": true gap in the original's discovery
            # config.
            budget_topic = daily_budget_topic(child, mqtt_config.device_id)
            budget_discovery_topic = f"homeassistant/number/{base_id}_daily_budget_minutes/config"
            budget_discovery_payload = {
                "name": f"{child} Mac Daily Budget (min)",
                "unique_id": f"{base_id}_daily_budget_minutes",
                "state_topic": budget_topic,
                "command_topic": budget_topic,
                "min": 0,
                "max": 240,
                "step": 5,
                "mode": "box",
                "retain": True,
                "unit_of_measurement": "min",
                "icon": "mdi:timer-sand",
                "device": _discovery_device(child, mqtt_config.device_id),
            }
            client.publish(
                budget_discovery_topic,
                json.dumps(budget_discovery_payload),
                retain=True,
                qos=1,
            )

    def on_disconnect(client, userdata, disconnect_flags, reason_code, properties=None):
        rc = int(getattr(reason_code, "value", reason_code))
        if rc != 0:
            logger.warning("Unexpected MQTT disconnect (rc=%s).", rc)

    topic_to_child = {
        allow_topic(prefix): child
        for child in registry.all_children()
        for prefix in [registry.topic_prefix_for(child)]
        if prefix
    }
    budget_topic_to_child = {
        daily_budget_topic(child, mqtt_config.device_id): child
        for child in registry.all_children()
    }

    def on_message(client, userdata, message):
        if message.topic in budget_topic_to_child:
            child = budget_topic_to_child[message.topic]
            payload = (message.payload or b"").decode("utf-8", errors="ignore")
            try:
                budget_value = max(0.0, float(payload))
            except ValueError:
                logger.warning(
                    "Received invalid budget payload '%s' on %s", payload, message.topic
                )
                return
            previous = budget_state.get(child)
            budget_state[child] = budget_value
            if previous != budget_value:
                logger.info("Budget for '%s' updated to %s minutes.", child, budget_value)
            return

        child = topic_to_child.get(message.topic)
        if child is None:
            return
        payload = (message.payload or b"").decode("utf-8", errors="ignore")
        value = _as_bool(payload)
        if value is None:
            logger.warning(
                "Received invalid allowed payload '%s' on %s", payload, message.topic
            )
            return
        previous = allowed_state.get(child)
        allowed_state[child] = value
        if previous != value:
            logger.info("Allowed state for '%s' updated to %s.", child, value)

    client.on_connect = on_connect
    client.on_disconnect = on_disconnect
    client.on_message = on_message
    return client


def main() -> None:
    # TODO: point at the real config path once this is wired into the
    # actual daemon; kept as a placeholder constant for now during
    # standalone testing.
    CONFIG_PATH = "/Library/Application Support/ha-screen-agent/config.json"
    registry = ManagedUserRegistry(CONFIG_PATH)
    mqtt_config = DaemonMqttConfig.load(CONFIG_PATH)

    # One persisted UsageState per kid — see that class's docstring for
    # why this replaced a plain in-memory accumulated_seconds dict.
    usage_states: dict[str, UsageState] = {
        child: UsageState(Path(usage_state_path(child))) for child in registry.all_children()
    }
    last_state_save = time.monotonic()
    STATE_SAVE_INTERVAL_SECONDS = 30.0  # matches screentime_enforcer.py's own cadence

    # Written by build_mqtt_client's on_message (background MQTT thread),
    # read here in main()'s poll loop (main thread) — see build_mqtt_client's
    # docstring for why no lock is used.
    allowed_state: dict = {}
    budget_state: dict = {}  # child -> float minutes, written the same way as allowed_state
    grace_started_at: dict = {}  # per-kid, only used when fail_mode="grace"

    # Budget voice warnings — new, not in screentime_enforcer.py (which
    # only covers 5/1 minutes remaining and never announces a budget
    # CHANGE). last_announced_budget seeds silently on first sight per
    # child (so login doesn't announce a "change" that never happened),
    # then only speaks on a genuine later difference. budget_warned_thresholds
    # is per-kid, cleared whenever remaining rises back above all
    # thresholds (budget increased, or a new day once persistence exists).
    last_announced_budget: dict = {}  # child -> float
    budget_warned_thresholds: dict = {}  # child -> set[int]

    # Rapid-relogin protection state, all per-kid (dict keyed by child
    # name) — each kid accumulates their own independent streak, matching
    # the original's per-instance state but generalized since one daemon
    # now covers every kid instead of one agent per kid. Only actually
    # evaluated for whichever kid is the CURRENT console user (see
    # ENFORCEMENT block below) — while backgrounded, `locked` reflects the
    # console session, not necessarily theirs, so it wouldn't mean
    # anything for a kid who isn't currently the one being displayed.
    rapid_relogin_attempts: dict = {}  # child -> list[float] (monotonic timestamps)
    rapid_relogin_warned_count: dict = {}  # child -> int
    last_locked_while_enforced: dict = {}  # child -> bool, last `locked` seen for them
    blocked_unlock_counted: dict = {}  # child -> bool, matches original's per-instance flag

    mqtt_client = build_mqtt_client(mqtt_config, registry, allowed_state, budget_state)
    logger.info(
        "Connecting to MQTT %s:%s as device '%s'.",
        mqtt_config.mqtt_host,
        mqtt_config.mqtt_port,
        mqtt_config.device_id,
    )
    mqtt_client.connect_async(mqtt_config.mqtt_host, mqtt_config.mqtt_port, keepalive=60)
    mqtt_client.loop_start()  # background thread; publish() calls below are non-blocking

    last_seen_user: Optional[str] = object()  # sentinel, never equals a real value
    last_seen_sessions: dict[str, int] = {}
    last_seen_locked: Optional[bool] = None  # sentinel, always logs the first reading
    active_child: Optional[str] = None  # the kid currently accruing time, if any

    last_published_minutes: dict[str, int] = {}
    last_published_state: dict[str, str] = {}

    logger.info("Starting console-user + lock-state detection loop (Ctrl+C to stop).")
    try:
        while True:
            current_user = get_console_user()
            locked = is_screen_locked()
            all_sessions = get_all_logged_in_sessions()

            # Every kid, every tick — not just whoever's active, so a
            # backgrounded kid's usage still resets correctly if the
            # daemon happens to be running across local midnight while
            # they're not the one active. Matches screentime_enforcer.py's
            # own ensure_today() being called every loop tick.
            for child in registry.all_children():
                usage_states[child].ensure_today()

            if time.monotonic() - last_state_save >= STATE_SAVE_INTERVAL_SECONDS:
                for child in registry.all_children():
                    usage_states[child].save()
                last_state_save = time.monotonic()

            # Per-kid session_state, for EVERY managed kid, not just
            # whoever's currently active — richer than the boolean
            # `active` topic. Published only on change.
            for child in registry.all_children():
                mac_user = registry.mac_user_for(child)
                if mac_user == current_user:
                    state = "locked" if locked else "active"
                elif mac_user in all_sessions:
                    state = "backgrounded"
                else:
                    state = "offline"
                if state != last_published_state.get(child):
                    prefix = registry.topic_prefix_for(child)
                    if prefix:
                        mqtt_client.publish(
                            session_state_topic(prefix, mqtt_config.device_id),
                            payload=state,
                            retain=True,
                            qos=1,
                        )
                    last_published_state[child] = state

            # General lock-state visibility for ANY console user, not just
            # managed kids — the PAUSE/RESUME messages below only fire for
            # managed accounts (they're the only ones with a budget to
            # pause), so without this, locking as e.g. an admin account
            # produces no log line at all. Useful for confirming lock
            # detection is working in general while testing as yourself.
            if locked != last_seen_locked:
                logger.info(
                    "Lock state changed: locked=%s (console user='%s').",
                    locked,
                    current_user,
                )
                last_seen_locked = locked

            if current_user != last_seen_user:
                if current_user is None:
                    logger.info("Console is at the login window (nobody logged in).")
                else:
                    child = registry.child_for(current_user)
                    if child is not None:
                        logger.info(
                            "Console user changed to '%s' -> managed child '%s' "
                            "(locked=%s).",
                            current_user,
                            child,
                            locked,
                        )
                    else:
                        logger.info(
                            "Console user changed to '%s' (not a managed account, "
                            "e.g. an admin session) (locked=%s).",
                            current_user,
                            locked,
                        )
                last_seen_user = current_user

            # Who SHOULD be accruing time right now: the console user, if
            # they're a managed kid, and only while unlocked. Anything else
            # (nobody logged in, an unmanaged/admin console user, or a
            # managed kid whose screen is locked) means nobody accrues.
            should_be_active = (
                registry.child_for(current_user) if current_user and not locked else None
            )

            if should_be_active != active_child:
                if active_child is not None:
                    logger.info(
                        "PAUSE accounting for '%s' (console user now '%s', locked=%s).",
                        active_child,
                        current_user,
                        locked,
                    )
                    prefix = registry.topic_prefix_for(active_child)
                    if prefix:
                        mqtt_client.publish(
                            active_topic(prefix, mqtt_config.device_id),
                            payload="0",
                            retain=False,
                            qos=0,
                        )
                    # TODO: requirement #9 — also publish retained MQTT
                    # state clearing this device's active_child, e.g.
                    #   screen/<this_device>/active_child = "" (retained)
                    # so HA's per-kid "current device" sensor reflects them
                    # no longer being active here.
                if should_be_active is not None:
                    logger.info(
                        "RESUME accounting for '%s' (console user '%s', unlocked).",
                        should_be_active,
                        current_user,
                    )
                    prefix = registry.topic_prefix_for(should_be_active)
                    if prefix:
                        mqtt_client.publish(
                            active_topic(prefix, mqtt_config.device_id),
                            payload="1",
                            retain=False,
                            qos=0,
                        )
                    # Informational only here — the actual allowed check
                    # and locking happens in the ENFORCEMENT block below,
                    # which runs every tick (not just this transition), so
                    # it also catches a kid re-entering their own password
                    # after being locked (current_user doesn't change when
                    # they unlock their own session, so this transition
                    # block wouldn't see it happen again).
                    if (
                        mqtt_config.fail_mode == "grace"
                        and allowed_state.get(should_be_active) is None
                    ):
                        remaining = mqtt_config.fail_grace_minutes - (
                            (time.monotonic() - grace_started_at[should_be_active]) / 60.0
                        )
                        logger.warning(
                            "'%s' is in the fail-mode grace window (no allowed "
                            "value received yet) — allowed for ~%.1f more "
                            "minute(s) before failing closed.",
                            should_be_active,
                            max(remaining, 0.0),
                        )
                    # TODO: requirement #9 — publish this device's new
                    # active_child (see PAUSE branch above).
                active_child = should_be_active

            # ENFORCEMENT — runs every poll tick, not just on the
            # transition above (mirrors screentime_enforcer.py's
            # _enforce_if_required, also called every loop tick there).
            # This is what actually closes the fast-user-switch bypass:
            # checks the retained 'allowed' state continuously, so a kid
            # re-entering their own password after being locked gets
            # re-locked immediately, not just checked once on first
            # becoming console user.
            if current_user is not None:
                enforced_child = registry.child_for(current_user)
                if enforced_child is not None:
                    is_allowed = resolve_allowed(
                        enforced_child,
                        allowed_state,
                        mqtt_config.fail_mode,
                        grace_started_at,
                        mqtt_config.fail_grace_minutes,
                    )
                    blocked = not is_allowed
                    # Tracked on EVERY tick this kid is enforced_child,
                    # regardless of blocked state — matches
                    # screentime_enforcer.py's _last_session_locked, which
                    # also updates unconditionally every loop tick. Read
                    # BEFORE overwriting below, so it reflects the prior
                    # tick's state, not this one.
                    was_locked = last_locked_while_enforced.get(enforced_child)
                    last_locked_while_enforced[enforced_child] = locked

                    if not blocked:
                        if rapid_relogin_attempts.get(enforced_child):
                            logger.info(
                                "Clearing rapid relogin streak for '%s' after "
                                "access restored.",
                                enforced_child,
                            )
                        rapid_relogin_attempts[enforced_child] = []
                        rapid_relogin_warned_count[enforced_child] = 0
                        blocked_unlock_counted[enforced_child] = False
                    elif mqtt_config.rapid_relogin_shutdown_enabled:
                        # Edge-triggered: was locked, now unlocked, while
                        # blocked = enforced_child just re-entered their own
                        # password to get back in. Matches
                        # screentime_enforcer.py's _handle_rapid_relogin_protection
                        # exactly, just keyed per-kid instead of per-instance.
                        if locked:
                            blocked_unlock_counted[enforced_child] = False
                        elif was_locked and not blocked_unlock_counted.get(
                            enforced_child, False
                        ):
                            now = time.monotonic()
                            attempts = [
                                t
                                for t in rapid_relogin_attempts.get(enforced_child, [])
                                if now - t < mqtt_config.rapid_relogin_window_seconds
                            ]
                            attempts.append(now)
                            rapid_relogin_attempts[enforced_child] = attempts
                            attempt_count = len(attempts)
                            blocked_unlock_counted[enforced_child] = True
                            logger.warning(
                                "Rapid relogin attempt detected for '%s' while "
                                "blocked: %d/%d within %ds.",
                                enforced_child,
                                attempt_count,
                                mqtt_config.rapid_relogin_max_attempts,
                                mqtt_config.rapid_relogin_window_seconds,
                            )
                            warned_count = rapid_relogin_warned_count.get(enforced_child, 0)
                            if (
                                mqtt_config.rapid_relogin_warn_voice
                                and attempt_count >= mqtt_config.rapid_relogin_warn_attempt
                                and warned_count < mqtt_config.rapid_relogin_warn_attempt
                            ):
                                remaining_attempts = max(
                                    0,
                                    mqtt_config.rapid_relogin_max_attempts - attempt_count,
                                )
                                warn_prefix = registry.topic_prefix_for(enforced_child)
                                if remaining_attempts > 0 and warn_prefix:
                                    if remaining_attempts == 1:
                                        speak(
                                            mqtt_client,
                                            warn_prefix,
                                            mqtt_config.device_id,
                                            RAPID_RELOGIN_WARN_VOICE_ONE,
                                        )
                                    else:
                                        speak(
                                            mqtt_client,
                                            warn_prefix,
                                            mqtt_config.device_id,
                                            RAPID_RELOGIN_WARN_VOICE_MANY.format(
                                                count=remaining_attempts
                                            ),
                                        )
                                rapid_relogin_warned_count[enforced_child] = attempt_count
                            if attempt_count >= mqtt_config.rapid_relogin_max_attempts:
                                shutdown_computer()

                    if blocked and not locked:
                        uid = all_sessions.get(current_user)
                        if uid is None:
                            logger.error(
                                "Cannot lock '%s' (%s): no uid found in current "
                                "session list.",
                                enforced_child,
                                current_user,
                            )
                        else:
                            logger.warning(
                                "Locking '%s' now (allowed=%s, fail_mode=%s).",
                                enforced_child,
                                allowed_state.get(enforced_child),
                                mqtt_config.fail_mode,
                            )
                            lock_session(uid, expected_mac_user=current_user)

            # Minute accumulation + publish — persisted per kid (see
            # UsageState), resets at local midnight. Publishes only when
            # the whole minutes value actually changes, not every tick.
            if active_child is not None:
                usage_states[active_child].add_seconds(POLL_INTERVAL_SECONDS)
                minutes = usage_states[active_child].minutes_today()
                if minutes != last_published_minutes.get(active_child):
                    prefix = registry.topic_prefix_for(active_child)
                    if prefix:
                        mqtt_client.publish(
                            minutes_topic(prefix, mqtt_config.device_id),
                            payload=str(minutes),
                            retain=True,
                            qos=1,
                        )
                    last_published_minutes[active_child] = minutes

                # Budget voice warnings — the FIRST value ever seen for a
                # kid (this daemon run — last_announced_budget itself is
                # not persisted) gets the plain "set to" phrasing; any
                # later change gets increase/decrease-specific phrasing
                # with the delta, so a kid can tell "my parent gave me
                # more time" apart from the initial daily limit. Then
                # checks the 15/10/5/1-minutes-remaining thresholds —
                # `remaining` is now computed from the persisted
                # UsageState, so it correctly means "minutes actually
                # used today," not "minutes active since the daemon
                # process happened to start."
                current_budget = budget_state.get(active_child)
                if current_budget is not None:
                    prev_announced = last_announced_budget.get(active_child)
                    voice_prefix = registry.topic_prefix_for(active_child)
                    if prev_announced is None and voice_prefix:
                        speak(
                            mqtt_client,
                            voice_prefix,
                            mqtt_config.device_id,
                            BUDGET_INITIAL_VOICE.format(
                                minutes=_minutes_text(int(current_budget))
                            ),
                        )
                        last_announced_budget[active_child] = current_budget
                    elif (
                        prev_announced is not None
                        and current_budget != prev_announced
                        and voice_prefix
                    ):
                        delta = int(current_budget) - int(prev_announced)
                        template = (
                            BUDGET_INCREASED_VOICE if delta > 0 else BUDGET_DECREASED_VOICE
                        )
                        speak(
                            mqtt_client,
                            voice_prefix,
                            mqtt_config.device_id,
                            template.format(
                                delta=_minutes_text(abs(delta)),
                                minutes=_minutes_text(int(current_budget)),
                            ),
                        )
                        last_announced_budget[active_child] = current_budget
                        budget_warned_thresholds[active_child] = set()  # new budget, fresh thresholds

                    remaining = current_budget - usage_states[active_child].minutes_today()
                    warned = budget_warned_thresholds.setdefault(active_child, set())
                    if remaining > max(BUDGET_WARNING_THRESHOLDS):
                        warned.clear()
                    elif voice_prefix:
                        # Only the single most-specific (lowest) applicable
                        # threshold ever gets spoken — a big drop (e.g. the
                        # budget itself gets cut so remaining jumps straight
                        # to 5 or 0) should say "5 minutes remaining" once,
                        # not machine-gun 15/10/5 all at once just because
                        # they were technically all crossed in the same
                        # tick. Marking every threshold >= the one just
                        # spoken as warned means a slow, normal countdown
                        # still fires each threshold individually as it's
                        # actually reached (only one is ever "applicable"
                        # at a time in that case) — this only changes
                        # behavior for a sudden multi-threshold jump.
                        applicable = [t for t in BUDGET_WARNING_THRESHOLDS if remaining <= t]
                        if applicable:
                            threshold = min(applicable)
                            if threshold not in warned:
                                speak(
                                    mqtt_client,
                                    voice_prefix,
                                    mqtt_config.device_id,
                                    BUDGET_WARNING_TEXT[threshold],
                                )
                                warned.update(t for t in BUDGET_WARNING_THRESHOLDS if t >= threshold)

            if all_sessions != last_seen_sessions:
                for user, uid in all_sessions.items():
                    if user == current_user:
                        continue
                    child = registry.child_for(user)
                    if child:
                        logger.info(
                            "(info) managed child '%s' (account '%s', uid %d) is "
                            "logged in but backgrounded — not accruing time, no "
                            "action needed.",
                            child,
                            user,
                            uid,
                        )
                last_seen_sessions = all_sessions

            time.sleep(POLL_INTERVAL_SECONDS)
    except KeyboardInterrupt:
        logger.info("Stopping (Ctrl+C).")
    finally:
        for child in registry.all_children():
            usage_states[child].save()
        if active_child is not None:
            prefix = registry.topic_prefix_for(active_child)
            if prefix:
                mqtt_client.publish(
                    active_topic(prefix, mqtt_config.device_id),
                    payload="0",
                    retain=False,
                    qos=0,
                )
        for child in registry.all_children():
            prefix = registry.topic_prefix_for(child)
            if prefix:
                mqtt_client.publish(
                    availability_topic(prefix, mqtt_config.device_id),
                    payload="offline",
                    retain=True,
                    qos=1,
                )
        time.sleep(0.5)  # give the background loop a moment to flush these
        mqtt_client.loop_stop()
        mqtt_client.disconnect()


if __name__ == "__main__":
    main()
