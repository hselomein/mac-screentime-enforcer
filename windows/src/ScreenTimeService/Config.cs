using System.Text.Json;

namespace ScreenTimeService;

/// <summary>
/// Everything the service reads from config.json. Same keys as the Mac root
/// daemon's config (config/root_daemon.config.sample.json) so the two stay
/// documented together in config/CONFIG_REFERENCE.md. The one difference:
/// each managed_users entry names its Windows account with
/// "windows_user_account" instead of "mac_user_account".
/// </summary>
public sealed class ServiceConfig
{
    public required IReadOnlyList<ManagedUser> ManagedUsers { get; init; }
    public required string DeviceId { get; init; }
    public string? DeviceFriendlyName { get; init; }
    public required string MqttHost { get; init; }
    public int MqttPort { get; init; } = 1883;
    public string? MqttUsername { get; init; }
    public string? MqttPassword { get; init; }
    public bool MqttTls { get; init; }
    public string FailMode { get; init; } = "safe";
    public double FailGraceMinutes { get; init; } = 120;
    public bool RapidReloginShutdownEnabled { get; init; } = true;
    public double RapidReloginWindowSeconds { get; init; } = 60;
    public int RapidReloginMaxAttempts { get; init; } = 4;
    public int RapidReloginWarnAttempt { get; init; } = 3;
    public bool RapidReloginWarnVoice { get; init; } = true;
    public bool DryRun { get; init; }

    public static ServiceConfig Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var users = new List<ManagedUser>();
        if (root.TryGetProperty("managed_users", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var account = Str(entry, "windows_user_account");
                var child = Str(entry, "child_name");
                if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(child))
                    continue;
                var prefix = (Str(entry, "topic_prefix") ?? $"screen/{child}").TrimEnd('/');
                users.Add(new ManagedUser(account.Trim(), child.Trim(), prefix));
            }
        }

        // Same lookup order as the Mac daemon: the root_daemon_-prefixed key
        // wins, so one config shape documents both platforms.
        var failMode = (Str(root, "root_daemon_fail_mode") ?? Str(root, "fail_mode") ?? "safe").ToLowerInvariant();
        if (failMode is not ("safe" or "open" or "grace"))
            failMode = "safe";

        return new ServiceConfig
        {
            ManagedUsers = users,
            DeviceId = Topics.SanitizeDeviceId(Str(root, "device_id") ?? Environment.MachineName),
            DeviceFriendlyName = string.IsNullOrWhiteSpace(Str(root, "device_friendly_name")) ? null : Str(root, "device_friendly_name")!.Trim(),
            MqttHost = Str(root, "mqtt_host") ?? throw new InvalidDataException("config.json is missing mqtt_host."),
            MqttPort = Int(root, "mqtt_port") ?? 1883,
            MqttUsername = NullIfBlank(Str(root, "mqtt_username")),
            MqttPassword = NullIfBlank(Str(root, "mqtt_password")),
            MqttTls = Bool(root, "mqtt_tls") ?? false,
            FailMode = failMode,
            FailGraceMinutes = Num(root, "root_daemon_fail_grace_minutes") ?? 120,
            RapidReloginShutdownEnabled = Bool(root, "rapid_relogin_shutdown_enabled") ?? true,
            RapidReloginWindowSeconds = Num(root, "rapid_relogin_window_seconds") ?? 60,
            RapidReloginMaxAttempts = Int(root, "root_daemon_rapid_relogin_max_attempts") ?? Int(root, "rapid_relogin_max_attempts") ?? 4,
            RapidReloginWarnAttempt = Int(root, "root_daemon_rapid_relogin_warn_attempt") ?? Int(root, "rapid_relogin_warn_attempt") ?? 3,
            RapidReloginWarnVoice = Bool(root, "rapid_relogin_warn_voice") ?? true,
            DryRun = Bool(root, "root_daemon_dry_run") ?? false,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d) ? d : null;
    }

    private static int? Int(JsonElement e, string name) => Num(e, name) is double d ? (int)d : null;

    private static bool? Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}

/// <summary>
/// One managed_users entry. WindowsAccount is compared case-insensitively
/// against the session's user name (what `whoami` prints after the
/// backslash). For a Microsoft account that is the local profile name
/// Windows made at setup (often the first five letters of the email), not
/// the email address.
/// </summary>
public sealed record ManagedUser(string WindowsAccount, string Child, string TopicPrefix);

public sealed class ManagedUserRegistry
{
    private readonly Dictionary<string, ManagedUser> _byAccount = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ManagedUser> _byChild = new();

    public ManagedUserRegistry(IEnumerable<ManagedUser> users)
    {
        foreach (var user in users)
        {
            _byAccount[user.WindowsAccount] = user;
            _byChild[user.Child] = user;
            // A prefix that doesn't match the kid's name still "works" but
            // talks on topics HA never uses, so the kid silently goes
            // unenforced (hit for real on the Mac side).
            if (!(user.TopicPrefix == $"screen/{user.Child}" || user.TopicPrefix.StartsWith($"screen/{user.Child}/")))
                Log.Warn($"topic_prefix '{user.TopicPrefix}' for '{user.Child}' doesn't start with 'screen/{user.Child}' — this kid's topics won't match HA's. Fix it in config.json.");
        }
        Log.Info($"Loaded {_byAccount.Count} managed user mapping(s): {string.Join(", ", _byAccount.Keys)}");
    }

    public IEnumerable<string> AllChildren => _byChild.Keys;

    public string? ChildFor(string? account) =>
        account != null && _byAccount.TryGetValue(account, out var u) ? u.Child : null;

    public string PrefixFor(string child) => _byChild[child].TopicPrefix;

    public string AccountFor(string child) => _byChild[child].WindowsAccount;
}
