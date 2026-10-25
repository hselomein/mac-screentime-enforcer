using System.Diagnostics;

namespace ScreenTimeService;

/// <summary>
/// The enforcement loop: a port of the Mac root daemon's main(), one tick
/// every two seconds. Kept deliberately close to the Mac version (same
/// order of steps, same rules) so a behaviour fix on one platform is easy
/// to carry to the other.
///
/// A kid only accrues time while they are the console user AND unlocked;
/// switched away or locked both pause it. Whoever is at the console is
/// checked against their retained `allowed` value every tick and locked if
/// blocked, which also closes the fast-user-switching bypass.
/// </summary>
public sealed class Enforcer
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StateSaveInterval = TimeSpan.FromSeconds(30);
    // LockWorkStation is asynchronous and the helper takes a moment to start.
    private static readonly TimeSpan HelperLockWait = TimeSpan.FromSeconds(3);

    private readonly ServiceConfig _config;
    private readonly ManagedUserRegistry _registry;
    private readonly SharedState _shared;
    private readonly MqttLink _mqtt;
    private readonly SessionHelper _helper;
    private readonly Dictionary<string, UsageState> _usage = new();

    private double _lastStateSave = Now();
    private readonly Dictionary<string, double> _graceStartedAt = new();

    // Voice announcements. The last-announced values aren't persisted, so
    // the first value seen after a restart is announced as "set to".
    private readonly Dictionary<string, double> _lastAnnouncedBase = new();
    private readonly Dictionary<string, double> _lastAnnouncedBonusApplied = new();
    private readonly Dictionary<string, HashSet<int>> _budgetWarned = new();
    private readonly Dictionary<string, double> _arrivalVoiceDue = new();

    // Rapid-relogin protection, per kid.
    private readonly Dictionary<string, List<double>> _reloginAttempts = new();
    private readonly Dictionary<string, int> _reloginWarnedCount = new();
    private readonly Dictionary<string, bool> _lastLockedWhileEnforced = new();
    private readonly Dictionary<string, bool> _blockedUnlockCounted = new();

    private bool _firstTick = true;
    private string? _lastSeenUser;
    private bool? _lastSeenLocked;
    private Dictionary<string, int> _lastSeenSessions = new();
    private string? _activeChild;
    private readonly Dictionary<string, (int, string)> _lastPublishedMinutes = new();
    private readonly Dictionary<string, int> _lastPublishedTotal = new();
    private readonly Dictionary<string, string> _lastPublishedState = new();
    private bool _wasConnected;

    public Enforcer(ServiceConfig config, ManagedUserRegistry registry, SharedState shared, MqttLink mqtt, SessionHelper helper)
    {
        _config = config;
        _registry = registry;
        _shared = shared;
        _mqtt = mqtt;
        _helper = helper;
        foreach (var child in registry.AllChildren)
            _usage[child] = new UsageState(Path.Combine(Paths.StateDir, $"{child}.json"));
    }

    /// <summary>HA's Offline Mode for this kid if a parent has set one, else config.json's.</summary>
    private string FailModeFor(string child) =>
        _shared.Settings.TryGetValue(child, out var s) && s.FailMode != null ? s.FailMode : _config.FailMode;

    private double FailGraceMinutesFor(string child) =>
        _shared.Settings.TryGetValue(child, out var s) && s.FailGraceMinutes is double g ? g : _config.FailGraceMinutes;

    private static double Now() => Environment.TickCount64 / 1000.0;

    private static bool SameUser(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Tick()
    {
        var console = Sessions.GetConsole();
        var currentUser = console.User;
        var locked = console.Locked;
        var allSessions = Sessions.AllSignedIn();

        foreach (var usage in _usage.Values)
            usage.EnsureToday();

        if (Now() - _lastStateSave >= StateSaveInterval.TotalSeconds)
        {
            foreach (var usage in _usage.Values)
                usage.Save();
            _lastStateSave = Now();
        }

        PublishSessionStates(currentUser, locked, allSessions);

        if (locked != _lastSeenLocked)
        {
            Log.Info($"Lock state changed: locked={locked} (console user='{currentUser}').");
            _lastSeenLocked = locked;
        }

        if (_firstTick || !SameUser(currentUser, _lastSeenUser))
            OnConsoleUserChanged(currentUser, locked);

        UpdateAccounting(currentUser, locked);
        Enforce(console);

        if (_activeChild != null)
            _usage[_activeChild].AddSeconds(PollInterval.TotalSeconds);

        PublishMinutes();

        if (_activeChild != null)
            AnnounceBudget(_activeChild, console.SessionId);

        if (!SessionsEqual(allSessions, _lastSeenSessions))
        {
            foreach (var (user, sessionId) in allSessions)
            {
                if (SameUser(user, currentUser)) continue;
                if (_registry.ChildFor(user) is { } child)
                    Log.Info($"(info) managed child '{child}' (account '{user}', session {sessionId}) is signed in but switched away — not accruing time, no action needed.");
            }
            _lastSeenSessions = allSessions;
        }
    }

    /// <summary>"active" | "locked" | "backgrounded" | "offline" for every managed kid, on change.</summary>
    private void PublishSessionStates(string? currentUser, bool locked, Dictionary<string, int> allSessions)
    {
        foreach (var child in _registry.AllChildren)
        {
            var account = _registry.AccountFor(child);
            var state = SameUser(account, currentUser) ? (locked ? "locked" : "active")
                : allSessions.ContainsKey(account) ? "backgrounded"
                : "offline";
            if (_lastPublishedState.GetValueOrDefault(child) == state) continue;
            _mqtt.Publish(Topics.SessionState(_registry.PrefixFor(child), _config.DeviceId), state, retain: true);
            _lastPublishedState[child] = state;
        }
    }

    private void OnConsoleUserChanged(string? currentUser, bool locked)
    {
        _arrivalVoiceDue.Clear(); // only whoever is arriving now gets one
        // Windows-only: a kid leaving the console (user switch, or the
        // disconnect lock fallback) has to sign back in to return, so that
        // counts as an unlock for rapid-relogin purposes. Without this, a kid
        // sent to the sign-in screen by the fallback could sign back in
        // forever without the attempts adding up.
        if (!_firstTick && _registry.ChildFor(_lastSeenUser) is { } leaving)
            _lastLockedWhileEnforced[leaving] = true;

        if (currentUser == null)
        {
            Log.Info("Console is at the sign-in screen (nobody signed in).");
        }
        else if (_registry.ChildFor(currentUser) is { } child)
        {
            Log.Info($"Console user changed to '{currentUser}' -> managed child '{child}' (locked={locked}).");
            _arrivalVoiceDue[child] = Now() + Voice.ArrivalDelay.TotalSeconds;
        }
        else
        {
            Log.Info($"Console user changed to '{currentUser}' (not a managed account, e.g. a parent) (locked={locked}).");
        }
        _lastSeenUser = currentUser;
        _firstTick = false;
    }

    /// <summary>Who should be accruing time: the console user, if they're a managed kid, and only while unlocked.</summary>
    private void UpdateAccounting(string? currentUser, bool locked)
    {
        var shouldBeActive = currentUser != null && !locked ? _registry.ChildFor(currentUser) : null;
        if (shouldBeActive == _activeChild) return;

        if (_activeChild != null)
        {
            Log.Info($"PAUSE accounting for '{_activeChild}' (console user now '{currentUser}', locked={locked}).");
            _mqtt.Publish(Topics.Active(_registry.PrefixFor(_activeChild), _config.DeviceId), "0", retain: false, qos: 0);
        }
        if (shouldBeActive != null)
        {
            Log.Info($"RESUME accounting for '{shouldBeActive}' (console user '{currentUser}', unlocked).");
            _mqtt.Publish(Topics.Active(_registry.PrefixFor(shouldBeActive), _config.DeviceId), "1", retain: false, qos: 0);
            if (FailModeFor(shouldBeActive) == "grace" && !_shared.Allowed.ContainsKey(shouldBeActive))
            {
                if (!_graceStartedAt.TryGetValue(shouldBeActive, out var started))
                    _graceStartedAt[shouldBeActive] = started = Now();
                var remaining = FailGraceMinutesFor(shouldBeActive) - (Now() - started) / 60.0;
                Log.Warn($"'{shouldBeActive}' is in the fail-mode grace window (no allowed value received yet) — allowed for ~{Math.Max(remaining, 0):0.0} more minute(s) before failing closed.");
            }
        }
        _activeChild = shouldBeActive;
    }

    /// <summary>
    /// Same as the Mac's resolve_allowed. A received value always wins.
    /// Without one: "open" allows, "safe" blocks, "grace" allows for
    /// the grace minutes, timed per kid from when they first hit the unknown
    /// state, then blocks. Mode and minutes come from HA when set there.
    /// </summary>
    private bool ResolveAllowed(string child)
    {
        if (_shared.Allowed.TryGetValue(child, out var value))
        {
            _graceStartedAt.Remove(child);
            return value;
        }
        var failMode = FailModeFor(child);
        if (failMode == "open") return true;
        if (failMode != "grace") return false;
        if (!_graceStartedAt.TryGetValue(child, out var started))
            _graceStartedAt[child] = started = Now();
        return (Now() - started) / 60.0 < FailGraceMinutesFor(child);
    }

    /// <summary>
    /// Runs every tick, not just when the console user changes, so a kid
    /// unlocking their own session while blocked is locked again straight
    /// away (their user name doesn't change when they unlock).
    /// </summary>
    private void Enforce(ConsoleState console)
    {
        if (console.User == null) return;
        var child = _registry.ChildFor(console.User);
        if (child == null) return;

        var blocked = !ResolveAllowed(child);
        var locked = console.Locked;
        // The previous tick's value, read before it's overwritten.
        var wasLocked = _lastLockedWhileEnforced.GetValueOrDefault(child);
        _lastLockedWhileEnforced[child] = locked;

        if (!blocked)
        {
            if (_reloginAttempts.GetValueOrDefault(child) is { Count: > 0 })
                Log.Info($"Clearing rapid relogin streak for '{child}' after access restored.");
            _reloginAttempts[child] = new List<double>();
            _reloginWarnedCount[child] = 0;
            _blockedUnlockCounted[child] = false;
        }
        else if (_config.RapidReloginShutdownEnabled)
        {
            // Edge-triggered: locked last tick, unlocked now, while blocked =
            // the kid just typed their password to get back in.
            if (locked)
            {
                _blockedUnlockCounted[child] = false;
            }
            else if (wasLocked && !_blockedUnlockCounted.GetValueOrDefault(child))
            {
                CountReloginAttempt(child, console.SessionId);
            }
        }

        if (blocked && !locked)
        {
            if (_config.DryRun)
            {
                Log.Warn($"[DRY RUN] Would lock '{child}' now (allowed={AllowedText(child)}, fail_mode={FailModeFor(child)}).");
            }
            else
            {
                Log.Warn($"Locking '{child}' now (allowed={AllowedText(child)}, fail_mode={FailModeFor(child)}).");
                LockSession(console.SessionId, console.User);
            }
        }
    }

    private string AllowedText(string child) =>
        _shared.Allowed.TryGetValue(child, out var v) ? v.ToString() : "None";

    private void CountReloginAttempt(string child, int sessionId)
    {
        var now = Now();
        var attempts = _reloginAttempts.GetValueOrDefault(child, new List<double>())
            .Where(t => now - t < _config.RapidReloginWindowSeconds)
            .ToList();
        attempts.Add(now);
        _reloginAttempts[child] = attempts;
        _blockedUnlockCounted[child] = true;
        var count = attempts.Count;
        Log.Warn($"Rapid relogin attempt detected for '{child}' while blocked: {count}/{_config.RapidReloginMaxAttempts} within {_config.RapidReloginWindowSeconds}s.");

        var warnedCount = _reloginWarnedCount.GetValueOrDefault(child);
        if (_config.RapidReloginWarnVoice && count >= _config.RapidReloginWarnAttempt && warnedCount < _config.RapidReloginWarnAttempt)
        {
            var remaining = Math.Max(0, _config.RapidReloginMaxAttempts - count);
            if (remaining == 1)
                _helper.Speak(sessionId, Voice.RapidReloginWarnOne);
            else if (remaining > 1)
                _helper.Speak(sessionId, string.Format(Voice.RapidReloginWarnMany, remaining));
            _reloginWarnedCount[child] = count;
        }

        if (count >= _config.RapidReloginMaxAttempts)
        {
            if (_config.DryRun)
                Log.Warn($"[DRY RUN] Would shut down now: '{child}' hit {count} rapid relogin attempts.");
            else
                ShutdownComputer();
        }
    }

    /// <summary>
    /// Primary: the helper calls LockWorkStation inside the kid's session,
    /// the same lock as Windows+L. Fallback, if it doesn't show as locked in
    /// time (helper missing, or the kid somehow stopped it): disconnect the
    /// session, which sends the console to the sign-in screen.
    ///
    /// Re-checks the console user right before each action, as on the Mac:
    /// with fast user switching, a decision made from a two-second-old
    /// reading could otherwise lock whoever switched in meanwhile.
    /// </summary>
    private bool LockSession(int sessionId, string expectedUser)
    {
        var current = Sessions.ConsoleUser();
        if (!SameUser(current, expectedUser))
        {
            Log.Warn($"Aborting lock for session {sessionId}: console user changed from '{expectedUser}' to '{current}' since this was decided.");
            return false;
        }

        if (_helper.RequestLock(sessionId))
        {
            var deadline = Now() + HelperLockWait.TotalSeconds;
            while (Now() < deadline)
            {
                Thread.Sleep(250);
                if (Sessions.IsLocked(sessionId))
                {
                    Log.Info($"Locked session {sessionId} via the helper.");
                    return true;
                }
            }
            Log.Warn($"Helper didn't lock session {sessionId} in time — falling back to disconnecting it.");
            if (!SameUser(Sessions.ConsoleUser(), expectedUser))
                return false;
        }

        if (!Sessions.Disconnect(sessionId))
        {
            Log.Error($"WTSDisconnectSession failed for session {sessionId}.");
            return false;
        }
        Thread.Sleep(400);
        var after = Sessions.GetConsole();
        if (after.SessionId != sessionId || after.Locked)
            return true;
        Log.Warn($"Disconnected session {sessionId} but the console doesn't show as locked yet.");
        return false;
    }

    /// <summary>
    /// The rapid-relogin last resort. Forced (/f), so an app's "save
    /// changes?" prompt can't cancel it. Like the Mac version, test it in a
    /// low-stakes way before relying on it.
    /// </summary>
    private static void ShutdownComputer()
    {
        Log.Critical("Rapid relogin threshold reached. Initiating shutdown.");
        try
        {
            var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            using var process = Process.Start(new ProcessStartInfo(shutdown, "/s /f /t 0") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("shutdown.exe failed. Giving up on shutdown.", ex);
        }
    }

    /// <summary>
    /// This PC's own minutes (always current) plus every other machine's
    /// latest report for today. Skips this PC's own echo and anything dated
    /// another day or not dated at all (a leftover from an older Mac install).
    /// </summary>
    private int CombinedMinutes(string child)
    {
        var usage = _usage[child];
        var total = usage.MinutesToday;
        if (_shared.SiblingMinutes.TryGetValue(child, out var siblings))
        {
            foreach (var (device, (minutes, date)) in siblings)
            {
                if (device == _config.DeviceId || date != usage.Date) continue;
                total += minutes;
            }
        }
        return total;
    }

    /// <summary>
    /// Every kid's own minutes and combined total, on change only, so a
    /// midnight reset reaches HA even for a kid who never uses this PC that
    /// day. Everything is re-sent once after a reconnect.
    /// </summary>
    private void PublishMinutes()
    {
        var connected = _mqtt.IsConnected;
        if (connected && !_wasConnected)
        {
            _lastPublishedMinutes.Clear();
            _lastPublishedTotal.Clear();
            _lastPublishedState.Clear();
        }
        _wasConnected = connected;

        foreach (var child in _registry.AllChildren)
        {
            var prefix = _registry.PrefixFor(child);
            var own = (_usage[child].MinutesToday, _usage[child].Date);
            if (!_lastPublishedMinutes.TryGetValue(child, out var lastOwn) || lastOwn != own)
            {
                _mqtt.Publish(Topics.Minutes(prefix, _config.DeviceId), Topics.MinutesPayload(own.MinutesToday, own.Date), retain: true);
                _lastPublishedMinutes[child] = own;
            }
            var total = CombinedMinutes(child);
            if (!_lastPublishedTotal.TryGetValue(child, out var lastTotal) || lastTotal != total)
            {
                _mqtt.Publish(Topics.TotalMinutes(prefix), total.ToString(), retain: true);
                _lastPublishedTotal[child] = total;
            }
        }
    }

    /// <summary>
    /// The arrival line, budget/bonus change announcements and the
    /// 15/10/5/1-minutes-left warnings, exactly as on the Mac. Effective
    /// budget = base + min(bonus, max bonus).
    /// </summary>
    private void AnnounceBudget(string child, int sessionId)
    {
        if (!_shared.Budget.TryGetValue(child, out var currentBase)) return;

        var bonusRaw = _shared.Bonus.GetValueOrDefault(child, 0);
        var maxBonus = _shared.MaxBonus.TryGetValue(child, out var mb) ? mb : MqttLink.DefaultMaxBonusMinutes;
        var bonusApplied = Math.Min(bonusRaw, maxBonus);
        var effective = currentBase + bonusApplied;
        var remaining = effective - CombinedMinutes(child);
        var hasPrevBase = _lastAnnouncedBase.TryGetValue(child, out var prevBase);
        var hasPrevBonus = _lastAnnouncedBonusApplied.TryGetValue(child, out var prevBonusApplied);

        if (_arrivalVoiceDue.TryGetValue(child, out var arrivalDue))
        {
            // Just arrived: one "time left" line once the desktop is up,
            // which also covers any budget change made meanwhile.
            if (Now() >= arrivalDue)
            {
                _arrivalVoiceDue.Remove(child);
                if (remaining >= 1)
                    _helper.Speak(sessionId, string.Format(Voice.Arrival, Voice.MinutesText((int)remaining)));
                _lastAnnouncedBase[child] = currentBase;
                _lastAnnouncedBonusApplied[child] = bonusApplied;
                // Don't follow up with a threshold warning the arrival line already covered.
                _budgetWarned[child] = Voice.WarningThresholds.Where(t => t >= remaining).ToHashSet();
            }
        }
        else if (!hasPrevBase)
        {
            _helper.Speak(sessionId, string.Format(Voice.BudgetInitial, Voice.MinutesText((int)effective)));
            _lastAnnouncedBase[child] = currentBase;
            _lastAnnouncedBonusApplied[child] = bonusApplied;
        }
        else if (currentBase != prevBase)
        {
            // A base change wins over a bonus change landing in the same tick.
            var delta = (int)currentBase - (int)prevBase;
            var template = delta > 0 ? Voice.BudgetIncreased : Voice.BudgetDecreased;
            _helper.Speak(sessionId, string.Format(template, Voice.MinutesText(Math.Abs(delta)), Voice.MinutesText((int)effective)));
            _lastAnnouncedBase[child] = currentBase;
            _lastAnnouncedBonusApplied[child] = bonusApplied;
            _budgetWarned[child] = new HashSet<int>();
        }
        else if (!hasPrevBonus || bonusApplied != prevBonusApplied)
        {
            var delta = (int)bonusApplied - (int)(hasPrevBonus ? prevBonusApplied : 0);
            var template = delta > 0 ? Voice.BonusGranted : Voice.BonusReduced;
            _helper.Speak(sessionId, string.Format(template, Voice.MinutesText(Math.Abs(delta)), Voice.MinutesText((int)effective)));
            _lastAnnouncedBonusApplied[child] = bonusApplied;
            _budgetWarned[child] = new HashSet<int>();
        }

        if (!_budgetWarned.TryGetValue(child, out var warned))
            _budgetWarned[child] = warned = new HashSet<int>();
        if (remaining > Voice.WarningThresholds.Max())
        {
            warned.Clear();
        }
        else if (!_arrivalVoiceDue.ContainsKey(child))
        {
            // Only the lowest threshold crossed is spoken, so a sudden cut
            // says "5 minutes" once instead of 15, 10 and 5 back to back.
            var applicable = Voice.WarningThresholds.Where(t => remaining <= t).ToList();
            if (applicable.Count > 0)
            {
                var threshold = applicable.Min();
                if (!warned.Contains(threshold))
                {
                    _helper.Speak(sessionId, Voice.Warning(threshold));
                    warned.UnionWith(Voice.WarningThresholds.Where(t => t >= threshold));
                }
            }
        }
    }

    /// <summary>Saves usage and tells HA this PC is going offline. Called once on service stop.</summary>
    public void Shutdown()
    {
        foreach (var usage in _usage.Values)
            usage.Save();
        if (_activeChild != null)
            _mqtt.Publish(Topics.Active(_registry.PrefixFor(_activeChild), _config.DeviceId), "0", retain: false, qos: 0);
        foreach (var child in _registry.AllChildren)
            _mqtt.Publish(Topics.Availability(_registry.PrefixFor(child), _config.DeviceId), "offline", retain: true);
    }

    private static bool SessionsEqual(Dictionary<string, int> a, Dictionary<string, int> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
