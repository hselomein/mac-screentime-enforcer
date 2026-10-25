# Windows Screen Time Agent for Home Assistant

The Windows 10/11 counterpart of the Mac root daemon (`../root-daemon/`). One
service per PC tracks every managed kid's minutes, reports them to Home
Assistant over MQTT, and locks whoever is at the screen when their retained
`allowed` topic says they're blocked. Kids share one budget across every Mac
and PC: each machine adds the others' minutes into the same
**Total Minutes Today**, and the same budget blueprint drives `allowed`.

**Status: written, compile-checked, not yet run on real Windows hardware.**
Test it on one PC with `"root_daemon_dry_run": true` first (see Testing below).

## How it works

- **ScreenTimeService.exe**: a Windows service running as LocalSystem. Every
  2 seconds it reads who owns the console and whether it's locked (WTS
  session APIs), adds time for a managed kid who is at the console and
  unlocked, publishes minutes and session state, and locks a blocked kid.
  Fast user switching is handled the same way as on the Mac: a switched-away
  kid pauses, and whoever switches in is checked straight away.
- **ScreenTimeHelper.exe**: a tiny .NET Framework 4.8 program the service
  starts *inside the kid's session* for one command and then exits:
  `speak "text"` (Windows speech, the kid's default voice) or `lock` (the
  same lock as Windows+L). A service can't play sound or lock a session
  itself, which is the same reason the Mac has `user_voice_helper.py`.
  Unlike the Mac helper, it isn't a login item and never connects to MQTT,
  so there's nothing for a kid to quit and no password for it to read.
- **Lock fallback**: if the session doesn't show as locked within 3 seconds,
  the service disconnects it, which sends the PC to the sign-in screen.
- **Rapid-relogin protection, voice warnings, fail modes, bonus time**: same
  rules, thresholds and wording as the Mac daemon. Each kid's **Offline
  Mode** and **Offline Grace Minutes** in HA (shared with the Macs) override
  `root_daemon_fail_mode` / `root_daemon_fail_grace_minutes`, and are saved
  in `state\<kid>.settings.json` so they apply after a reboot with HA
  unreachable.

### Differences from the Mac daemon

| | Mac | Windows |
|---|---|---|
| Per-device topics | `screen/<kid>/mac/<device>/...` | `screen/<kid>/win/<device>/...` |
| HA device per machine | `<kid> mac (<name>)` | `<kid> pc (<name>)` |
| Helper | Login item listening on MQTT | Started by the service per command |
| Config account field | `mac_user_account` | `windows_user_account` |
| Leaving the console while blocked | Not counted | Counts as a sign-in attempt for rapid relogin, so the disconnect fallback can't be bypassed by signing back in again and again |

Not handled yet (same as the Mac): idle time, and real offline behaviour.
Once an `allowed` value has arrived it's kept through an outage.

## Requirements

- Windows 10 or 11, x64 or ARM64.
- Kids on **standard** (not administrator) accounts. An admin can stop the
  service. Microsoft accounts are fine (see `windows_user_account` below).
- The Macs on root daemon **0.2.3 or later**, and their ACL blocks
  regenerated. Older Macs don't read `/win/` minutes, so a kid's Windows
  time wouldn't count toward the total those Macs publish.
- To build: the .NET 10 SDK, on Windows or a Mac.

## Build

On Windows:

```powershell
cd windows
.\scripts\build.ps1                     # or -Runtime win-arm64 for ARM PCs
```

On a Mac (cross-compiling works; the helper needs no Windows SDK):

```sh
cd windows
dotnet publish src/ScreenTimeService/ScreenTimeService.csproj -c Release -r win-x64 -o publish -p:EnableWindowsTargeting=true
dotnet publish src/ScreenTimeHelper/ScreenTimeHelper.csproj -c Release -o publish
```

Either way, `publish\` ends up with `ScreenTimeService.exe` (~40 MB,
self-contained, so the PC needs no .NET install), `ScreenTimeHelper.exe`
and `ScreenTimeHelper.exe.config`.

## Install

Copy the `windows` folder (with `publish\` in it) to the PC, then from an
**elevated** PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\install.ps1
```

The first run creates `C:\ProgramData\ScreenTimeEnforcer\config.json` from
the sample and stops. Fill it in:

- `managed_users`: one `{"windows_user_account", "child_name", "topic_prefix"}`
  per kid. `windows_user_account` is what `whoami` prints after the
  backslash when signed in as that kid (compared ignoring case). For a
  Microsoft account it's the local profile name Windows made at setup,
  often the first five letters of the email, not the email itself.
  `child_name` must match the name used on the Macs so the kid shares one
  budget.
- `device_id`: unique across **every** Mac and PC (each machine's minutes
  are keyed by it). `device_friendly_name` is the label shown in HA.
- `mqtt_*`: one broker user per machine, e.g. `screentime_<device_id>`.

Then run `.\scripts\install.ps1` again. It starts the service and prints
this PC's Mosquitto ACL block (also saved to
`C:\ProgramData\ScreenTimeEnforcer\mosquitto_acl_snippet.txt`). Add it to
the broker and restart Mosquitto.

Re-run `install.ps1` any time to upgrade; it stops the service, copies the
new exes and starts it again, keeping the config and today's minutes.
`.\scripts\uninstall.ps1` removes it (`-All` also deletes config, state and
logs).

Files:

- `C:\Program Files\ScreenTimeEnforcer\`: the two exes.
- `C:\ProgramData\ScreenTimeEnforcer\`: `config.json`, `state\<kid>.json`
  (today's minutes), `logs\service.log`. Only SYSTEM and Administrators can
  read it, since the config holds the MQTT password.
- `%LOCALAPPDATA%\ScreenTimeEnforcer\helper.log` in each kid's profile:
  only written when the helper hits an error.

## Home Assistant

Nothing new to set up. The PC's entities join the kid's existing device
(Allowed, Daily Budget, Bonus Minutes, Max Bonus Minutes, Offline Mode,
Offline Grace Minutes, Parent Override, Total Minutes Today), and a `<kid> pc (<name>)` device appears with Minutes,
Active, Online and Session State. The existing budget and daily-reset
blueprints already work off the combined total. Rerun
`scripts/make_ha_dashboard.py` to have the dashboard list the PCs next to
the Macs.

## Testing (do this before trusting it)

1. Set `"root_daemon_dry_run": true`, install, and watch
   `C:\ProgramData\ScreenTimeEnforcer\logs\service.log` while a kid signs
   in, locks (Windows+L), unlocks, and switches users. You should see
   RESUME/PAUSE lines and session_state changing in HA. Dry run logs "would
   lock" and "would shut down" instead of acting.
2. Check the kid's PC minutes count in their Total Minutes Today, and that a
   Mac shows the same total.
3. Turn dry run off. Turn **Allowed** off in HA while the kid is signed in:
   the screen should lock within a few seconds and the log should say
   "Locked session N via the helper". Unlock: it should lock again.
4. Check the voice lines play (arrival line ~10 s after sign-in, budget
   changes, the 15/10/5/1 warnings).
5. Rapid relogin: with a low `root_daemon_rapid_relogin_max_attempts`,
   confirm the warning plays and the PC shuts down. It's a forced shutdown,
   so close anything important first.
6. Test the fallback by renaming `ScreenTimeHelper.exe` temporarily: a
   blocked kid should be sent to the sign-in screen instead.

Running `ScreenTimeService.exe` from an admin console works for quick checks
of detection and MQTT, but speech and the helper lock need LocalSystem
(the log shows `WTSQueryUserToken failed ... error 1314`). Use the
installed service, or `psexec -s`, for those.

## Layout

```
windows/
  ScreenTimeEnforcer.slnx
  config/config.sample.json
  scripts/build.ps1, install.ps1, uninstall.ps1
  src/ScreenTimeService/   the service (.NET 10)
    Enforcer.cs            the enforcement loop (port of the Mac daemon's main())
    Sessions.cs, Native.cs console user, lock state, session list (WTS APIs)
    SessionHelper.cs       starts the helper inside a kid's session
    MqttLink.cs            MQTT connection and incoming state
    Discovery.cs           Home Assistant discovery payloads
    Topics.cs, Config.cs, UsageState.cs, Voice.cs, Log.cs
  src/ScreenTimeHelper/    speak / lock, run inside the kid's session (.NET Framework 4.8)
```
