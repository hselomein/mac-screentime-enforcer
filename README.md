# macOS Screen Time Agent for Home Assistant

Tracks a child's Mac usage, reports it to Home Assistant over MQTT, and enforces the retained "allowed" flag from HA — no Apple Screen Time APIs or special entitlements. Two tools live here, covering the same job differently:

- **The old per-user agent** (`screentime_enforcer.py`) — a LaunchAgent that runs entirely inside the child's own user session. Simple, but needs one installed per macOS account, a reboot between account switches, and can't see who's actually at the console if a backgrounded kid gets blocked.
- **The root daemon** (`root-daemon/`, recommended) — a single root-owned LaunchDaemon per Mac that watches every managed account on that machine at once, plus a small per-user voice helper for audio (root has no session of its own to play sound in). No per-account install, no reboot between switches, and it stays correct through macOS Fast User Switching. See "Install the root daemon" below.

## What you get

- **Local tracking**: minutes count only while the kid is the console user and the screen is unlocked; a backgrounded (fast-user-switched) or locked session pauses. The old agent also pauses after `idle_timeout_seconds` of no input; **the root daemon has no idle detection yet**, so a kid who walks away unlocked keeps accruing time.
- **Enforcement**: when HA publishes `allowed=0`, the root daemon locks the screen: its per-user helper, running inside the kid's session, triggers the real Lock Screen (a password is always required), with display sleep (`pmset displaysleepnow`) as a fallback if the helper isn't running; the old agent locks or logs out. Both escalate repeated unlock attempts to a shutdown (rapid-relogin protection).
- **MQTT discovery & telemetry**: entities appear in HA automatically. Root daemon: one device per kid (allowed, daily budget, bonus minutes, max bonus minutes, parent override, and total minutes today summed across every Mac) plus one per kid per Mac (minutes, active, online, session state). The old agent adds a JSON heartbeat and an optional active-app sensor, and has no session-state or bonus entities.
- **Voice warnings** (root daemon, via a per-user helper): budget set/changed, bonus granted, 15/10/5/1 minutes left, and rapid-relogin warnings.
- **Fail-safe when HA has never answered**: see `fail_mode` in [`config/CONFIG_REFERENCE.md`](config/CONFIG_REFERENCE.md). Once the root daemon has received an `allowed` value it keeps enforcing that last value through a later MQTT outage.

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
5. Log in as the child and verify: `launchctl print gui/$(id -u)/com.ha.screen-agent | grep state`, then `tail ~/Library/Logs/ha-screen-agent/agent.out.log`. (`log show` won't find it: the agent logs to that file, not macOS's unified log.)
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
   - At the end it prints two things, and saves both under
     `/Library/Application Support/ha-screen-agent/`:
     this Mac's **Mosquitto ACL block** (`mosquitto_acl_snippet.txt`) to
     add to the broker's ACL file, and the **Home Assistant checklist**
     (`ha_setup_checklist.txt`) with your kids' names filled in and
     ready-to-paste raw GitHub URLs for the blueprints (HA's Import
     Blueprint dialog only accepts a URL), derived from this checkout's
     git remote. See "What you create by hand" below.
2. Confirm it's running: `sudo launchctl list | grep com.ha.screen-daemon`
   (a PID means running), then `sudo tail -50 /var/log/root_daemon_skeleton.log`.
   `log show` won't show anything: the daemon logs to that file, not to
   macOS's unified log.
3. To update after a `git pull`: rerun the installer (it offers to keep the
   existing config), then `sudo launchctl kickstart -k system/com.ha.screen-daemon`.
4. To remove it later: `sudo ./scripts/uninstall.sh` (add `--all` to also
   remove the old agent, shared config, and venv — leaves nothing behind).

**Known limitations (root daemon):** no idle detection (see above);
minutes are counted as fixed 2-second ticks, which slightly undercounts;
a clean `launchctl` stop skips shutdown cleanup (up to 30s of
usage lost, "Agent Online" stays on); and `config.json`, which holds the
Mac's MQTT password, is group-readable by the first managed kid's primary
group, which on a stock Mac is `staff` (every local account).

## Home Assistant integration

Home Assistant makes every allow/block decision; the Macs just report
minutes and obey `allowed`. The root daemon creates every entity it needs
through MQTT discovery, so there are no helpers, template sensors or
`configuration.yaml` entries to write. (Everything in this section is for
the root daemon. The old agent keeps its original per-Mac entities and
isn't covered by these blueprints.)

- **Per-kid device**, named after the kid (e.g. `screentime_cj`). Every Mac
  managing that kid publishes it identically, so HA shows exactly one no
  matter how many Macs the kid uses. Entity ids are predictable:
  - `switch.<child>_allowed` — drives lock/unlock on every Mac
  - `number.<child>_daily_budget` — minutes per day (0–600)
  - `number.<child>_bonus_minutes` — extra minutes for today (0–240)
  - `number.<child>_max_bonus_minutes` — caps how much bonus counts (0–240; treated as 60 until set)
  - `switch.<child>_parent_override` — while on, the budget automation leaves `allowed` alone
  - `sensor.<child>_total_minutes_today` — the kid's minutes summed across every Mac, computed by the Macs
- **Per-Mac device**, named `<child> mac (<friendly name or device_id>)`,
  for things that really are per machine: Minutes (this Mac only), Active,
  Online, Session State (`active`/`locked`/`backgrounded`/`offline`).
- **MQTT topics (child_name=kiddo, topic_prefix=screen/kiddo, device_id=mac-mini)**
  - Mac → HA (retained): `screen/kiddo/mac/mac-mini/minutes_today` — JSON `{"minutes": 42, "date": "2026-09-25"}`; every Mac managing kiddo also reads the others' to build the total
  - Mac → HA (retained): `screen/kiddo/total_minutes_today` (integer minutes, all Macs)
  - Mac → HA: `screen/kiddo/mac/mac-mini/active` (`0/1`)
  - Mac → HA (retained): `screen/kiddo/mac/mac-mini/availability` (`online`/`offline`)
  - Mac → HA (retained): `screen/kiddo/mac/mac-mini/session_state`
  - HA → Mac (retained): `screen/kiddo/allowed` (`0/1`, `on/off`, `true/false`)
  - HA ↔ Mac (retained): `homeassistant/kiddo_shared/{daily_budget,bonus_minutes,max_bonus_minutes,override}/state`
  - Mac → voice helper (not retained): `screen/kiddo/mac/mac-mini/voice_command`
- **Daily reset of minutes**: at local midnight each Mac resets and
  republishes 0 for every kid it manages (persisted, so a restart doesn't
  lose the day). A sibling Mac's minutes only count toward today's total if
  they're dated today.
- **Old entities are cleaned up automatically**: on connect the daemon
  clears the discovery configs of the earlier per-Mac
  `<child> Mac Allowed / Daily Budget / ...` scheme, so they disappear from HA.

### What you create by hand in Home Assistant

Once per household (not once per Mac):
1. **Import two blueprints** (Settings → Automations & Scenes → Blueprints →
   Import Blueprint): `homeassistant/blueprints/kid_mac_budget_enforcement.yaml`
   and `homeassistant/blueprints/kid_mac_daily_reset.yaml`. If the repo
   isn't public, copy them into `<HA config>/blueprints/automation/` instead.
2. **Per kid**: set their Daily Budget, then create one automation from
   *Kid Mac Budget Enforcement*, picking that kid's Total Minutes Today,
   Daily Budget, Bonus Minutes, Max Bonus Minutes and Parent Override, and
   the topic `<topic_prefix>/allowed`. Rename the automation after saving
   (the blueprint's name field doesn't stick).
3. **Once**: one automation from *Kid Mac Daily Reset (all kids)*, picking
   every kid's Allowed, Parent Override and Bonus Minutes.
4. Flip each kid's Parent Override on and off once, so its state is saved.

**`install_root_daemon.sh` prints exactly this with your kids' names and
topics filled in**, and saves it to
`/Library/Application Support/ha-screen-agent/ha_setup_checklist.txt`.

### How the budget automation decides

It blocks (`allowed=0`, retained) when

    Total Minutes Today >= Daily Budget + min(Bonus Minutes, Max Bonus Minutes)

and allows (`allowed=1`) otherwise — the same formula the Mac uses for its
voice warnings. It re-evaluates whenever any of the five entities changes,
so raising the budget or granting bonus unlocks a kid who's already locked.
It does nothing while Parent Override is on, or until Daily Budget has been
set. Because it's driven by the combined total, using up the budget on one
Mac blocks the kid on every Mac.

The daily reset automation (03:00 by default) turns every kid's Allowed on,
Parent Override off and Bonus Minutes to 0. It leaves Daily Budget and Max
Bonus Minutes alone; see the blueprint's description for why.

**Current limitations:**
- If a Mac goes offline, its last reported minutes keep counting in the
  kid's total until it reconnects (errs toward less time, not more).
- A Mac that loses its connection doesn't yet enforce the budget itself: it
  keeps the last `allowed` value it received until it reconnects.
  `root_daemon_fail_mode` only applies if it never received one (see
  `config/CONFIG_REFERENCE.md`).

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

Example for two kids on one Mac (old agent). **For the root daemon, give every
entry a `topic_prefix`**: the old agent defaults a missing one to
`screen/<child_name>`, but the root daemon silently leaves a kid without one
off MQTT entirely (no entities, never receives `allowed`, so only `fail_mode`
governs them). `install_root_daemon.sh` always writes it.

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

**`install_root_daemon.sh` generates the exact block for each Mac** (printed,
and saved to `/Library/Application Support/ha-screen-agent/mosquitto_acl_snippet.txt`).
Add each Mac's block to the broker's ACL file, replacing any older block for
that Mac, and restart Mosquitto. See
[`homeassistant/mosquitto.acl`](homeassistant/mosquitto.acl) for an annotated
example of what it grants and why.

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
├── homeassistant/
│   ├── blueprints/                          # Budget enforcement (one per kid) + daily reset
│   ├── configuration.yaml                   # Notes: what discovery creates (no YAML needed)
│   └── mosquitto.acl                        # Annotated broker ACL example
└── root-daemon/
    ├── root_daemon_skeleton.py              # The daemon (installed as .../ha-screen-agent/root_daemon.py)
    ├── user_voice_helper.py                 # Per-user voice helper
    └── *.plist                              # Reference copies; the installer generates the real ones
```

## Security & hardening

- Install and own all files as `root:wheel`; child accounts stay non-admin.
- Root daemon: a `/Library/LaunchDaemons` job running as root, plus a voice-helper LaunchAgent in `/Library/LaunchAgents` that loads into every GUI session and exits immediately for non-managed accounts. Old agent: a LaunchAgent bootstrapped into each managed kid's GUI session.
- **The MQTT password in `config.json` is readable by the kids.** The file is `0640`, group-owned by the first managed kid's primary group so the per-user voice helper (and the old agent) can read it — on a stock Mac that's `staff`, i.e. every local account. A kid could use it to publish their own `minutes_today` as 0. A broker ACL limits what it can reach, so use one (see below), but it can't stop that.
- **Fail-safe covers only "never heard from HA".** Old agent: `fail_mode` plus `offline_grace_period_seconds`. Root daemon: `root_daemon_fail_mode` (`safe`/`open`/`grace`) applies only until the first `allowed` value arrives; after that it keeps enforcing the last value through an outage.
- `managed_users` controls which macOS accounts are tracked; broker ACLs should still scope what each machine's credential can publish.
- Rapid relogin protection is on by default: if a blocked child unlocks the session 4 times within 60 seconds, it warns on attempt 3 and shuts the Mac down on attempt 4.

## Troubleshooting

| Symptom | Checks |
|---------|--------|
| Root daemon not running | `sudo launchctl list \| grep com.ha.screen-daemon` (no PID = not running); `sudo tail -50 /var/log/root_daemon_skeleton.log`; crash output before logging starts goes to `/var/log/ha-screen-daemon.err.log`. |
| No voice warnings | The helper runs per kid session: check `~/Library/Logs/ha-user-voice-helper/helper.log` in that kid's home. Budget/bonus warnings also need the daemon to be receiving the budget number, so check the broker ACL allows its `homeassistant/<child>_<device_id>_mac/+/state` subscription. |
| Root daemon entities missing in HA | Kid's `managed_users` entry has no `topic_prefix` (skipped entirely), or the broker ACL denies `homeassistant/+/+/config` writes (discovery silently rejected — connection still looks fine). |
| Old agent does not start | `launchctl print gui/<uid> com.ha.screen-agent`; inspect `~/Library/Logs/ha-screen-agent/agent.err.log`. |
| Minutes not updating in HA | Confirm MQTT topics via `mosquitto_sub` and broker ACLs allow publishing. |
| Mac never unlocks after MQTT outage | Verify `offline_grace_period_seconds`, network reachability, and retained `allowed=1`. |
| Child can still use Mac when blocked | Ensure HA publishes retained `allowed=0`, LaunchAgent is running, and enforcement mode is set correctly. |
| Mac shuts down after repeated blocked relogins | This is expected when rapid relogin protection is enabled. Tune `rapid_relogin_*` settings if the window or threshold is too aggressive. |

### Harder lockouts (when logout prompts appear)

Mostly relevant to the **old agent**; the root daemon only ever locks, and handles fast user switching itself (a backgrounded session pauses, and whoever becomes the console user while blocked gets locked), so don't disable fast user switching for it. The password-after-sleep setting below is still **recommended** for the root daemon: its normal lock always requires the password, but its fallback (used only if the kid's per-user helper isn't running) is a display sleep, which is a real lock only with that setting on. On macOS 26 it's under **System Settings → Lock Screen → Require password after screen saver begins or display is turned off**, set per account while logged in as the kid.

macOS shows a cancelable confirmation dialog when users are logged out, so a determined child can dodge `enforcement_mode=logout`. To make the block harder to bypass:

- Prefer `enforcement_mode=lock` (default). The agent immediately locks the session instead of attempting logout.
- Require a password to unlock after sleep/screensaver: **System Settings → Privacy & Security → Require password after sleep or screen saver begins** → set to *Immediately*.
- Give the child account its own password (even a simple PIN) so the lock screen cannot be dismissed without supervision.
- Disable automatic login and fast user switching so the lock screen is always shown.
- Shorten `idle_timeout_seconds` and keep `sample_interval_seconds` small (e.g., 5–10 seconds) to reduce any window where they can act before the lock triggers.

These steps keep the session locked instead of relying on logout, eliminating the cancelable prompt.

## License

GPL-3.0 — see `LICENSE`.
