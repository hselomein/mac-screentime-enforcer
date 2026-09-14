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
import subprocess
import time
import logging
from typing import Optional

# SCDynamicStore gives us the actual logged-in console user, correctly
# updated across logout/login and fast-user-switch, without needing a
# per-user LaunchAgent to tell us. This is the load-bearing piece.
from SystemConfiguration import (  # type: ignore
    SCDynamicStoreCreate,
    SCDynamicStoreCopyValue,
)

# CGSessionCopyCurrentDictionary reports the console display's lock state
# (screensaver/password prompt up or not) — separate from WHO the console
# user is. A kid can still be the console user while their screen is
# locked (idle timeout, manual lock, walked away), and that shouldn't
# count against their time budget either.
import Quartz  # type: ignore

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
)
logger = logging.getLogger("root-screen-daemon")

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
    True if the console display is currently locked (screensaver +
    password prompt up), regardless of who the console user is.

    Lets the daemon pause time accounting when a kid is nominally still
    "at console" but has actually stepped away and the screen has locked
    on its own (idle timeout) or been locked manually — as distinct from
    having been fast-user-switched away from entirely, which
    get_console_user() already covers.

    CGSSessionScreenIsLocked is only present in the session dict while the
    screen is locked; absent means unlocked. No session dict at all
    (get_console_user() returning None, e.g. at the login window) is
    treated as locked too, since nobody's actively using the machine then.
    """
    session_info = Quartz.CGSessionCopyCurrentDictionary()
    if session_info is None:
        return True
    return bool(session_info.get("CGSSessionScreenIsLocked", False))


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
        logger.info(
            "Loaded %d managed user mapping(s): %s",
            len(self._by_mac_user),
            list(self._by_mac_user.keys()),
        )

    def child_for(self, mac_user: str) -> Optional[str]:
        entry = self._by_mac_user.get(mac_user)
        return entry["child_name"] if entry else None


def main() -> None:
    # TODO: point at the real config path once this is wired into the
    # actual daemon; kept as a placeholder constant for now during
    # standalone testing.
    registry = ManagedUserRegistry(
        "/Library/Application Support/ha-screen-agent/config.json"
    )

    last_seen_user: Optional[str] = object()  # sentinel, never equals a real value
    last_seen_sessions: dict[str, int] = {}
    active_child: Optional[str] = None  # the kid currently accruing time, if any

    logger.info("Starting console-user + lock-state detection loop (Ctrl+C to stop).")
    try:
        while True:
            current_user = get_console_user()
            locked = is_screen_locked()
            all_sessions = get_all_logged_in_sessions()

            if current_user != last_seen_user:
                if current_user is None:
                    logger.info("Console is at the login window (nobody logged in).")
                elif registry.child_for(current_user) is None:
                    logger.info(
                        "Console user changed to '%s' (not a managed account, "
                        "e.g. an admin session).",
                        current_user,
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
                    # TODO: hook point — stop the per-minute countdown for
                    # active_child once minute-tracking is ported over, AND
                    # (requirement #9) publish retained MQTT state clearing
                    # this device's active_child, e.g.
                    #   screen/<this_device>/active_child = "" (retained)
                    # so HA's per-kid "current device" sensor reflects them
                    # no longer being active here.
                if should_be_active is not None:
                    logger.info(
                        "RESUME accounting for '%s' (console user '%s', unlocked).",
                        should_be_active,
                        current_user,
                    )
                    # TODO: hook point — start/resume the per-minute
                    # countdown for should_be_active, and check their
                    # retained 'allowed' state immediately (lock right
                    # away if they've already exhausted their budget —
                    # this is what actually closes the fast-user-switch
                    # bypass, without needing to reach into any other
                    # session to lock it). ALSO (requirement #9): publish
                    # retained MQTT state for this device's active_child,
                    # e.g. screen/<this_device>/active_child = should_be_active
                    # plus a timestamp, so HA can surface cross-device
                    # sibling-borrowing in its logbook/history even though
                    # we can't prevent it outright.
                active_child = should_be_active

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


if __name__ == "__main__":
    main()
