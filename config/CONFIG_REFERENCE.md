# config.json field reference

Both the old per-user agent (`screentime_enforcer.py`) and the new root
daemon (`root_daemon_skeleton.py`) read the **same** `config.json` — that's
deliberate, so a machine can be migrated by pointing the new daemon at the
existing file rather than redesigning the schema. Some fields are read by
both, some only by one. This table is the source of truth; when in doubt,
prefer it over reading either script's defaults, since new options get
added here first.

Start from [`root_daemon.config.sample.json`](root_daemon.config.sample.json)
(every field below, with defaults) if you're setting up the new daemon, or
[`agent.config.sample.json`](agent.config.sample.json) if you're only
running the old per-user agent. The install script for the new daemon will
also offer to build this file interactively if it doesn't find one.

## Identity & connection (read by both)

| Field | Type | Default | Notes |
|---|---|---|---|
| `device_id` | string | *(required)* | Identifies this Mac in MQTT topics. Lowercased/sanitized automatically — case and non-alphanumerics don't matter, but keep it stable once devices exist in HA (changing it starts a fresh set of entities, not a rename). Both installers suggest a hostname + short hardware-serial-number suffix by default, specifically so it's still safe to just accept even if two Macs ever end up with the same computer name — see the comment above `SUGGESTED_DEVICE_ID` in `scripts/install_root_daemon.sh` if you want the full reasoning. |
| `device_friendly_name` | string | *(none)* | Shown alongside the device name in HA (e.g. "cj mac (Living Room MacBook)"), never used in any topic or unique_id. Left blank, the device name falls back to device_id instead (e.g. "cj mac (macbookprom2)") — NOT just a cosmetic fallback: confirmed on real hardware that two devices sharing the exact same display name for the same kid makes HA's own entity_id collision handling produce inconsistent, unpredictable entity_ids per machine (not a simple `_2`/`_3` suffix). The device name needs to be unique per machine one way or the other; this field just controls whether that uniqueness looks like a device_id or something more readable. Safe to change any time for a device HA has already discovered — updates the device's displayed name, doesn't change entity_ids already assigned. |
| `managed_users` | array | *(required)* | One entry per managed kid: `{"mac_user_account", "child_name", "topic_prefix"}`. `mac_user_account` is the macOS short username; `child_name` and `topic_prefix` drive MQTT topics/entity IDs. |
| `mqtt_host` | string | *(required)* | Broker hostname/IP. |
| `mqtt_port` | int | `1883` | |
| `mqtt_username` / `mqtt_password` | string | `""` | One account per **machine**, not per kid — deliberate: in a household where any kid can use any Mac, HA tracks time by `child_name` regardless of which physical Mac they're on, so each Mac's daemon just needs one set of credentials broad enough to publish/subscribe for every kid `managed_users` lists on it. Blank for an unauthenticated broker. |
| `mqtt_tls` | bool | `false` | |
| `sample_interval_seconds` | float | `15` | How often state (minutes, active, session) is sampled/published. |

## Fail-safe behavior when MQTT/HA is unreachable

This machine's daemon only knows whether a kid is "allowed" because Home
Assistant told it so over MQTT. If the Mac loses its network connection, the
broker goes down, or the daemon starts up before it's heard from HA yet
(e.g. right after a reboot), it genuinely doesn't know the real answer.
`fail_mode` decides what to do in that specific situation — it has no
effect at all once a real value has actually been received.

| Field | Type | Default | Read by | Notes |
|---|---|---|---|---|
| `fail_mode` | `"safe"` \| `"open"` | `"safe"` | old agent | **`safe`**: when the answer is unknown, treat the kid as blocked — screen time stays off until the connection comes back and HA tells it otherwise. Recommended: a network hiccup should never accidentally hand out free unmonitored time. **`open`**: when the answer is unknown, treat the kid as allowed — screen time keeps working through an outage, but so would it through a broker misconfiguration or an accidentally-unplugged access point, with no time limit on how long that lasts. This loader raises on any other value — never put `"grace"` here. |
| `root_daemon_fail_mode` | `"safe"` \| `"open"` \| `"grace"` | mirrors `fail_mode`, else `"safe"` | new daemon | Deliberately a separate key. **`grace`** is the new daemon's middle option: block like `safe`, but whenever the answer is unknown for a given kid (their first-ever login, or a fresh outage after they'd previously gotten a real answer), give them `root_daemon_fail_grace_minutes` of allowed time first, before falling back to blocked — so a genuine MQTT outage doesn't look, from the kid's side, like a computer that's supposedly theirs suddenly stopped working, without going as far as `open`'s no-limit allowance. The old agent doesn't understand `"grace"` — putting it under the shared `fail_mode` key above would crash the old agent on its next restart, which is why this is a separate key. |
| `root_daemon_fail_grace_minutes` | float | `120` | new daemon | How long the `grace` window above lasts, timed per kid from the moment *they* hit the unknown state (not from whenever the outage started). Only meaningful when `root_daemon_fail_mode` is `"grace"`. |
| `offline_grace_period_seconds` | float | `180` | old agent | Separate, older grace concept for brief MQTT drops before `fail_mode` kicks in. |

## Rapid-relogin shutdown escalation

Fires when a blocked kid repeatedly unlocks the screen in a short window
(rapid re-login as a bypass attempt) — warns via voice, then forces a
shutdown past a threshold.

| Field | Type | Default | Read by | Notes |
|---|---|---|---|---|
| `rapid_relogin_shutdown_enabled` | bool | `true` | both | |
| `rapid_relogin_window_seconds` | float | `60` | both | Sliding window the attempt count is measured over. |
| `rapid_relogin_max_attempts` | int | `4` | old agent | |
| `root_daemon_rapid_relogin_max_attempts` | int | mirrors `rapid_relogin_max_attempts`, else `4` | new daemon | Separate override — lets you dial this down for a one-off test of the shutdown path without changing the old agent's real production threshold if it's still installed. |
| `rapid_relogin_warn_attempt` | int | `3` | old agent | |
| `root_daemon_rapid_relogin_warn_attempt` | int | mirrors `rapid_relogin_warn_attempt`, else `3` | new daemon | Same override pattern. |
| `rapid_relogin_warn_voice` | bool | `true` | both | Speak a warning before the max-attempts shutdown actually fires. |

## New-daemon-only

| Field | Type | Default | Notes |
|---|---|---|---|
| `root_daemon_dry_run` | bool | `false` | Observe-only mode: console/lock/session detection, minute tracking, and HA reporting all run normally, but `lock_session()`/`shutdown_computer()` are never actually called — just logged as `[DRY RUN] would ...`. This is the **only** safe way to run the new daemon alongside the old agent still actively enforcing on the same machine — see "old agent + new daemon coexistence" in `Context/HANDOFF.md`. Set this if you pass `--keep-old-agent` to the installer. |

## Old-agent-only (ignored by the new daemon)

| Field | Type | Default | Notes |
|---|---|---|---|
| `blocked_check_seconds` | float | `1.0` | Poll interval while actively blocking. |
| `idle_timeout_seconds` | int | `180` | Idle detection for the old agent's own tracking loop. |
| `enforcement_mode` | string | `"lock"` | Old agent's enforcement style. |
| `logout_method` | string | `"osascript"` | How the old agent, running unprivileged in its own GUI session, asks the system to lock/logout. Irrelevant to the new daemon, which runs as root and calls `pmset`/`shutdown` directly. |
| `state_path` | string | `~/Library/Application Support/ha-screen-agent/state.json` | Old agent's per-user state file. The new daemon persists state per kid under `/Library/Application Support/ha-screen-agent/root-daemon-state/<child>.json` instead — not config-driven, and not shared with this key. |
| `log_file` / `err_log_file` | string | `~/Library/Logs/ha-screen-agent/agent.{out,err}.log` | Old agent's own log files — it opens these itself (`_setup_logging`), `~` expanded per the user it's actually running as. NOT `/tmp` (wiped every reboot) and NOT `/var/log` (root:wheel — this process runs as the unprivileged kid, so it can't write there). The plist's own `StandardOutPath`/`StandardErrorPath` are separately set to `/dev/null` — they'd otherwise be one shared, unwritable-or-colliding path across every managed kid's session, since the same plist file loads into all of them. |
| `debug_mqtt` | bool | `false` | Old agent verbose MQTT logging. |
| `track_active_app` | bool | `false` | Old agent's optional frontmost-app sensor. |

## Not config-driven (HA-managed instead)

`max_bonus_minutes` is **not** a `config.json` field — it's a standing cap
set via the `Max Bonus Minutes` number entity in HA (defaults to 60 in code
until a parent sets one). Likewise `daily_budget`, `bonus_minutes`, and the
`allowed` switch are all live MQTT-driven values, not config file settings.
