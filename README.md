# macOS Screen Time Agent for Home Assistant

Tracks a child's Mac usage, reports it to Home Assistant over MQTT, and enforces the retained "allowed" flag from HA — no Apple Screen Time APIs or special entitlements. Two tools live here, covering the same job differently:

- **The old per-user agent** (`screentime_enforcer.py`) — a LaunchAgent that runs entirely inside the child's own user session. Simple, but needs one installed per macOS account, a reboot between account switches, and can't see who's actually at the console if a backgrounded kid gets blocked.
- **The root daemon** (`root-daemon/`, recommended) — a single root-owned LaunchDaemon per Mac that watches every managed account on that machine at once, plus a small per-user voice helper for audio (root has no session of its own to play sound in). No per-account install, no reboot between switches, and it stays correct through macOS Fast User Switching. See "Install the root daemon" below.

## What you get

- **Accurate local tracking**: counts minutes only when the child session is unlocked and not idle.
- **Enforcement**: locks or logs out within seconds when HA publishes `allowed=0`.
- **MQTT discovery & telemetry**: retained minutes, live active flag, heartbeat status, optional active app sensor.
- **Fail-safe defaults**: configurable grace window; fails closed when MQTT is down or config is bad.

## Requirements

- macOS with a **parent admin** account and at least one **child non-admin** account (the root daemon can also manage a Mac's admin account itself, see below — useful if that account is a single family member's own Mac rather than a shared parent login).
- Home Assistant with MQTT discovery enabled and an MQTT broker (e.g., Mosquitto).
- MQTT credentials: one `mqtt_username`/`mqtt_password` per machine's `config.json`, for either tool — optionally restrict what topics that credential can actually touch via a broker-side ACL (see the ACL example further down). Internet to install Apple Command Line Tools once.

## Install (parent admin account)

1. Install Command Line Tools: `xcode-select --install`
2. Clone: `git clone https://github.com/hselomein/mac-screentime-enforcer.git && cd mac-screentime-enforcer`
3. Install as root: `sudo ./scripts/install_service.sh`
   - Prompts for child name, device ID, MQTT host/creds, managed users (mac_user=child_name pairs), optional active-app sensor if no config exists. Default managed user is the child name (set it to the child’s macOS short name if different).
   - Reuse an existing config via `--config /path/to/config.json`.
4. Update later: edit `/Library/Application Support/ha-screen-agent/config.json` as root, rerun the installer.
5. Log in as the child and verify: `log show --predicate 'process == "python3"' --last 5m | grep ha-screen-agent`
6. Home Assistant: with MQTT discovery on, a device named `<child> mac` appears under **Settings → Devices & Services → Integrations → MQTT** with minutes, active, allowed switch, budget number, parent override switch, optional active-app sensor. Add the automations below to drive `allowed`.

### macOS prompts & permissions

- **Background item notice** on the child’s first login (expected for the LaunchAgent).
- **Accessibility approval (admin required)** for `python3` at `/Library/Application Support/ha-screen-agent/agent.py` so it can lock/log out and, if enabled, read the frontmost app. Approve under **Settings → Privacy & Security → Accessibility**, then log out/in.

## Install the root daemon (recommended for multiple kids on one Mac)

The old per-user agent above needs one LaunchAgent per macOS account, a
reboot between account switches, and can't see who's actually at the
console when a backgrounded kid gets blocked. The root daemon replaces it
with a single root-owned LaunchDaemon plus a small per-user voice helper
(audio has to run inside the real session — see `root-daemon/`'s docs for
why). One shared install directory, one shared `config.json`, same
`managed_users` schema — migrating a machine is pointing the new installer
at the existing config, not starting over.

1. `sudo ./scripts/install_root_daemon.sh`
   - If no config exists yet, scans this Mac's real local accounts
     (`dscl`) and walks you through which ones to manage, plus device ID,
     MQTT, fail mode, and rapid-relogin tuning — see
     [`config/CONFIG_REFERENCE.md`](config/CONFIG_REFERENCE.md) for every
     field. Reuse an existing config with `--config /path/to/config.json`.
   - MQTT credentials are per **machine**, not per kid — one account
     covers every managed kid on that Mac. Some households dedicate one
     Mac to one kid; others have kids who can log into any Mac in the
     house. Either way works here, since HA tracks time by child name,
     not by which Mac reported it — this just means each Mac's daemon
     only needs one set of credentials to speak for whichever kids
     `managed_users` lists for it, rather than provisioning a separate
     credential per kid per machine.
   - The scan defaults to skipping the Mac's admin account (so a parent's
     own login isn't accidentally tracked) — but you can include it, and
     if that account turns out to be the *only* local account on the
     machine, the scan defaults to including it instead: a single-user
     Mac that's really one family member's own machine, admin bit and
     all, is a real setup this should still handle.
   - If a config already exists, you're offered the same walkthrough
     again to review or update it — existing values (including already-
     managed accounts and their child names) are pre-filled as defaults;
     press enter to keep any of them, or type a new value to change it.
     The MQTT password prompt is a special case: leave it blank to keep
     the existing one, or type `clear` to remove it.
   - By default, if the old per-user agent is installed on this machine,
     it's booted out and removed — running both with real enforcement on
     the same machine reintroduces the exact console-collision bug this
     daemon exists to fix.
   - `--keep-old-agent` leaves the old agent as the real enforcement and
     forces the new daemon into `root_daemon_dry_run` (observe/report to
     HA only, never locks or shuts down) — the only combination that's
     actually safe to run together, useful for validating the new
     daemon's detection on your hardware before cutting over for real.
   - At the end it offers to print both HA blueprints straight to the
     terminal for copy/paste.
2. Confirm it's running: `log show --predicate 'process == "python3"' --last 5m | grep ha-screen-daemon`
3. To remove it later: `sudo ./scripts/uninstall.sh` (add `--all` to also
   remove the old agent, shared config, and venv — leaves nothing behind).

## Home Assistant integration

- **MQTT topics (child_id=kiddo, device_id=mac-mini)**  
  - Agent → HA (retained): `screen/kiddo/mac/mac-mini/minutes_today` (integer minutes)  
  - Agent → HA (retained): `screen/kiddo/mac/mac-mini/active` (`0/1`)  
  - Agent → HA: `screen/kiddo/mac/mac-mini/status` (JSON heartbeat)  
  - HA → Agent (retained): `screen/kiddo/allowed` (`0/1`, `on/off`, `true/false`)
- **Discovery entities**: minutes sensor, active binary sensor, allowed switch, daily budget number (HA-managed), parent override switch (HA-managed), optional active app sensor. Entity names follow "`<child> Mac <Field>`" (e.g. "kiddo Mac Allowed") — HA slugifies that into the entity_id itself (likely `switch.kiddo_mac_allowed`, no device name baked in), but the exact slug can vary by HA version, so check the kid's Mac device page under **Settings → Devices & Services → MQTT** rather than assuming any example below is exact.
- **Daily reset**: the agent resets its local minutes at midnight while running. If it is offline at midnight, wrap the minutes sensor in a HA `utility_meter` with a daily cycle to keep a strict per-day view.

### Budget enforcement automation

**Use the blueprint** at `homeassistant/blueprints/kid_mac_budget_enforcement.yaml`
instead of hand-copying YAML per kid — import it into Home Assistant once
(Settings → Automations & Scenes → Blueprints → Import Blueprint, or drop the
file into your `config/blueprints/automation/` folder), then create one
automation per managed kid from it, filling in that kid's four entities
(minutes sensor, daily budget number, parent override switch, allowed MQTT
topic).

This fixes a real gap an earlier version of this example had: it only
triggered on the minutes sensor changing. That's fine while a kid is
actively using their budget, but once they're already locked out, minutes
stops changing (they're not using the computer) — so increasing their
budget at that point would silently do nothing, since nothing was left to
re-trigger the automation. The blueprint triggers on **both** the minutes
sensor and the daily budget number, so a budget change re-evaluates
`allowed` immediately, even while a kid is currently locked out.

**Multiple Macs per kid?** The blueprint above tracks minutes/budget **per
machine** — right for a household that wants separate per-Mac limits, wrong
if you want one combined total across every Mac a kid uses (using up the
budget on one Mac wouldn't block the others). For that, use
`kid_mac_budget_enforcement_combined.yaml` instead — see the "Optional:
ONE combined budget across multiple Macs" section in
[`homeassistant/configuration.yaml`](homeassistant/configuration.yaml) for
the template sensor + helper it needs first. The two aren't meant to be
mixed for the same kid; pick one model per kid.

For reference, this is the underlying automation each blueprint-created
instance is equivalent to (with `!input` values filled in for one kid):

```yaml
# Entity IDs below are illustrative — confirm the real ones on the kid's
# Mac device page (Settings → Devices & Services → MQTT) before using.
alias: "Kiddo Mac Budget Enforcement"
description: "Publishes allowed=0/1 based on minutes vs budget, retained. Skips when parent override is on."
trigger:
  - platform: state
    entity_id: sensor.kiddo_mac_minutes
  - platform: state
    entity_id: number.kiddo_mac_daily_budget_min
condition:
  - condition: not
    conditions:
      - condition: state
        entity_id: switch.kiddo_mac_parent_override
        state: "on"
action:
  - choose:
      - conditions:
          - condition: numeric_state
            entity_id: sensor.kiddo_mac_minutes
            above: number.kiddo_mac_daily_budget_min
        sequence:
          - service: mqtt.publish
            data:
              topic: screen/kiddo/allowed
              qos: 1
              retain: true
              payload: "0"
      - conditions:
          - condition: numeric_state
            entity_id: sensor.kiddo_mac_minutes
            below: number.kiddo_mac_daily_budget_min
        sequence:
          - service: mqtt.publish
            data:
              topic: screen/kiddo/allowed
              qos: 1
              retain: true
              payload: "1"
mode: single

  # bonus_minutes below is published by the root-daemon rewrite
  # (root-daemon/), not this agent — omit that step if you're only
  # running screentime_enforcer.py. Entity IDs illustrative, same caveat
  # as above — and unlike the budget automation, this one resets EVERY
  # kid at once (see the blueprint below), so in practice each
  # entity_id here would be a list of every kid's own entity.
  - alias: "All Kids Mac - Reset each morning"
    trigger:
      - platform: time
        at: "03:00:00"
    action:
      - service: switch.turn_on
        target:
          entity_id: [switch.kiddo_mac_allowed]
      - service: switch.turn_off
        target:
          entity_id: [switch.kiddo_mac_parent_override]
      - service: number.set_value
        target:
          entity_id: [number.kiddo_mac_bonus_minutes]
        data:
          value: 0
      # Minutes reset locally at midnight in the root-daemon (persisted,
      # see Context/HANDOFF.md's UsageState). daily_budget and
      # max_bonus_minutes are deliberately NOT reset here — see the
      # kid_mac_daily_reset blueprint's description for why.
```

**Use the blueprint** at `homeassistant/blueprints/kid_mac_daily_reset.yaml` instead
of hand-editing this YAML per kid. Unlike the budget-enforcement blueprint
above, this one is just applying the same reset to a list of entities, not
per-kid conditional logic — so it's ONE automation total, not one per kid:
import it once, then pick every kid's allowed switch / parent override
switch / bonus minutes number in its three multi-select inputs.

## Configuration
Configuration lives in `/Library/Application Support/ha-screen-agent/config.json`. This table covers the old agent's own fields; for the root daemon's additional fields (`root_daemon_*`) and a plainer explanation of what `fail_mode` actually does, see [`config/CONFIG_REFERENCE.md`](config/CONFIG_REFERENCE.md).

| Field | Required | Notes |
|-------|----------|-------|
| `managed_users` | ✅ | List of mappings (one per macOS account to manage). Each entry: `mac_user_account`, `child_name` (letters/numbers/hyphen/underscore), optional `topic_prefix` (must start with `screen/<child_name>`), optional `device_id`. The agent only runs when the current macOS user matches an entry and uses that child name for topics/discovery. |
| `device_id` | ➖ | Defaults to sanitized hostname if not set in the entry. |
| `mqtt_host`, `mqtt_port`, `mqtt_username`, `mqtt_password`, `mqtt_tls` | ✅ | MQTT connectivity (TLS optional). |
| `sample_interval_seconds` | ➖ | 5–60, default 15. |
| `blocked_check_seconds` | ➖ | Polling interval (seconds) while blocked; 0.5–10, default 1.0 to re-lock quickly if the child reauthenticates. |
| `idle_timeout_seconds` | ➖ | Idle threshold in seconds, default 120. |
| `enforcement_mode` | ➖ | `lock` (default) or `logout`. |
| `logout_method` | ➖ | How to force logout when `enforcement_mode=logout`: `osascript` (default, shows a prompt) or `kill_loginwindow` (kills the loginwindow process to bypass prompts). |
| `fail_mode` | ➖ | `safe` (fail closed) or `open`. |
| `offline_grace_period_seconds` | ➖ | Default 0. |
| `rapid_relogin_shutdown_enabled` | ➖ | Default `true`. When enabled, repeated blocked relogins can escalate to shutdown. |
| `rapid_relogin_window_seconds` | ➖ | Rolling window for blocked relogin attempts; 5–300, default 60. |
| `rapid_relogin_max_attempts` | ➖ | Number of blocked relogin attempts in the rolling window that triggers shutdown; 2–10, default 4. |
| `rapid_relogin_warn_attempt` | ➖ | Attempt number that triggers the spoken warning; must be less than `rapid_relogin_max_attempts`, default 3. |
| `rapid_relogin_warn_voice` | ➖ | Default `true`. Speaks a warning before shutdown escalation. |
| `state_path` | ➖ | Defaults to `~/Library/Application Support/ha-screen-agent/state.json`. Also stores rapid relogin streak data. |
| `log_file`, `err_log_file` | ➖ | Defaults `~/Library/Logs/ha-screen-agent/agent.{out,err}.log` (per-user; not `/tmp`, which is wiped every reboot). |
| `debug_mqtt` | ➖ | Set true for verbose client logging. |
| `track_active_app` | ➖ | Publish frontmost app name to MQTT. |

Edit as admin; keep it root-owned and readable by the child account (e.g., root:<child_group> 0640). The installer prompts for basics when no config exists.

Example for two kids on one Mac:

```json
{
  "mqtt_host": "mqtt.local",
  "managed_users": [
    { "mac_user_account": "kid1", "child_name": "alice" },
    { "mac_user_account": "kid2", "child_name": "bob", "topic_prefix": "screen/bobmac" }
  ]
}
```

## MQTT ACL example

**See [`homeassistant/mosquitto.acl`](homeassistant/mosquitto.acl)** for the
full, current example — it covers telemetry, the allowed flag, MQTT
discovery configs, and the daily budget/bonus/parent-override state
topics, none of which fully overlap with what an earlier, narrower
version of that file (or this section) used to show.

One thing worth knowing before writing your own: for the root daemon, one
MQTT credential now covers every kid a given Mac manages (see
`config/CONFIG_REFERENCE.md`'s note on `mqtt_username`), not one
credential per kid — so an ACL for it needs a block per managed kid on
that machine, not just one. The file above shows this, plus a legacy
per-child variant for anyone still running the old agent with genuinely
separate credentials per kid.

## Repository layout

```
.
├── screentime_enforcer.py                   # Old per-user agent (installed to .../ha-screen-agent/agent.py)
├── config/agent.config.sample.json          # Sample config for the old agent alone
├── config/root_daemon.config.sample.json    # Sample config with every root-daemon field too
├── config/CONFIG_REFERENCE.md               # Field-by-field reference for config.json (both tools)
├── scripts/install_service.sh               # Old agent installer (run with sudo)
├── scripts/install_root_daemon.sh           # Root-daemon + voice-helper installer (run with sudo)
├── scripts/uninstall.sh                     # Removes root daemon (or --all: everything), no breadcrumbs
├── requirements.txt                         # Python deps (PyObjC, MQTT, etc.) — shared venv, both tools
├── homeassistant/                           # Example HA snippets & docs
│   └── blueprints/                          # Reusable automation blueprints (import into HA)
└── root-daemon/                             # Root-daemon rewrite: root_daemon_skeleton.py, user_voice_helper.py
```

## Security & hardening

- Install and own all files as `root:wheel`; child account stays non-admin.
- LaunchAgent lives in `/Library/LaunchAgents` and is bootstrapped into the child’s GUI session.
- Default behavior is **fail-safe**: when MQTT is down beyond the grace window, the Mac locks until connectivity returns.
- `managed_users` controls which macOS accounts the agent will run under; broker ACLs should still enforce per-child topics.
- Rapid relogin protection is enabled by default: if a blocked child unlocks the session 4 times within 60 seconds, the agent warns on attempt 3 and shuts the Mac down on attempt 4.

## Troubleshooting

| Symptom | Checks |
|---------|--------|
| Agent does not start | `launchctl print gui/<uid> com.ha.screen-agent`; inspect `~/Library/Logs/ha-screen-agent/agent.err.log`. |
| Minutes not updating in HA | Confirm MQTT topics via `mosquitto_sub` and broker ACLs allow publishing. |
| Mac never unlocks after MQTT outage | Verify `offline_grace_period_seconds`, network reachability, and retained `allowed=1`. |
| Child can still use Mac when blocked | Ensure HA publishes retained `allowed=0`, LaunchAgent is running, and enforcement mode is set correctly. |
| Mac shuts down after repeated blocked relogins | This is expected when rapid relogin protection is enabled. Tune `rapid_relogin_*` settings if the window or threshold is too aggressive. |

### Harder lockouts (when logout prompts appear)

macOS shows a cancelable confirmation dialog when users are logged out, so a determined child can dodge `enforcement_mode=logout`. To make the block harder to bypass:

- Prefer `enforcement_mode=lock` (default). The agent immediately locks the session instead of attempting logout.
- Require a password to unlock after sleep/screensaver: **System Settings → Privacy & Security → Require password after sleep or screen saver begins** → set to *Immediately*.
- Give the child account its own password (even a simple PIN) so the lock screen cannot be dismissed without supervision.
- Disable automatic login and fast user switching so the lock screen is always shown.
- Shorten `idle_timeout_seconds` and keep `sample_interval_seconds` small (e.g., 5–10 seconds) to reduce any window where they can act before the lock triggers.

These steps keep the session locked instead of relying on logout, eliminating the cancelable prompt.

## License

GPL-3.0 — see `LICENSE`.
