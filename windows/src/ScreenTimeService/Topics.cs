using System.Text.Json;

namespace ScreenTimeService;

/// <summary>
/// The MQTT topic scheme, kept identical to the Mac root daemon's except
/// that per-device topics live under {prefix}/win/{device} instead of
/// {prefix}/mac/{device}. Per-kid topics (allowed, budget, bonus, override,
/// total) are shared with the Macs, so a kid has one budget across every
/// machine.
/// </summary>
public static class Topics
{
    public const string Platform = "win";

    /// <summary>The machine types whose minutes_today count toward a kid's total.</summary>
    public static readonly string[] SiblingPlatforms = ["mac", "win"];

    /// <summary>
    /// Same transform as the Mac daemon's _sanitize_device_id. Device IDs
    /// must be unique across every Mac and PC: the combined total keys each
    /// machine's minutes by device ID alone.
    /// </summary>
    public static string SanitizeDeviceId(string value)
    {
        var chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        var sanitized = new string(chars);
        return sanitized.Length == 0 ? "pc" : sanitized;
    }

    private static string Device(string prefix, string deviceId) => $"{prefix}/{Platform}/{deviceId}";

    public static string Minutes(string prefix, string deviceId) => $"{Device(prefix, deviceId)}/minutes_today";
    public static string Active(string prefix, string deviceId) => $"{Device(prefix, deviceId)}/active";
    public static string Availability(string prefix, string deviceId) => $"{Device(prefix, deviceId)}/availability";
    public static string SessionState(string prefix, string deviceId) => $"{Device(prefix, deviceId)}/session_state";

    public static string Allowed(string prefix) => $"{prefix}/allowed";
    public static string TotalMinutes(string prefix) => $"{prefix}/total_minutes_today";

    public static string SharedBaseId(string child) => $"{child}_shared";
    public static string DailyBudget(string child) => $"homeassistant/{SharedBaseId(child)}/daily_budget/state";
    public static string BonusMinutes(string child) => $"homeassistant/{SharedBaseId(child)}/bonus_minutes/state";
    public static string MaxBonusMinutes(string child) => $"homeassistant/{SharedBaseId(child)}/max_bonus_minutes/state";
    public static string OverrideState(string child) => $"homeassistant/{SharedBaseId(child)}/override/state";

    // Offline Mode / Offline Grace Minutes: fail_mode and the grace window,
    // set per kid from HA. config.json's values are only the default.
    public static string OfflineMode(string child) => $"homeassistant/{SharedBaseId(child)}/offline_mode/state";
    public static string OfflineGraceMinutes(string child) => $"homeassistant/{SharedBaseId(child)}/offline_grace_minutes/state";

    public static IEnumerable<string> MinutesWildcards(string prefix) =>
        SiblingPlatforms.Select(p => $"{prefix}/{p}/+/minutes_today");

    /// <summary>
    /// Splits {prefix}/{mac|win}/{device}/minutes_today. The prefix may
    /// itself contain slashes, so the known levels are peeled off the end.
    /// </summary>
    public static bool TryParseSiblingMinutesTopic(string topic, out string prefix, out string device)
    {
        prefix = device = "";
        const string suffix = "/minutes_today";
        if (!topic.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var rest = topic[..^suffix.Length];
        var slash = rest.LastIndexOf('/');
        if (slash <= 0) return false;
        device = rest[(slash + 1)..];
        rest = rest[..slash];
        slash = rest.LastIndexOf('/');
        if (slash <= 0) return false;
        var platform = rest[(slash + 1)..];
        prefix = rest[..slash];
        return device.Length > 0 && SiblingPlatforms.Contains(platform);
    }

    /// <summary>
    /// minutes_today is JSON {"minutes": N, "date": "YYYY-MM-DD"}: the date
    /// lets other machines skip a value left over from yesterday by a
    /// machine that was asleep or off at midnight.
    /// </summary>
    public static string MinutesPayload(int minutes, string date) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["minutes"] = minutes, ["date"] = date });

    public static bool TryParseMinutesPayload(string text, out int minutes, out string? date)
    {
        minutes = 0;
        date = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Number)
            {
                minutes = (int)root.GetDouble(); // bare number from an older Mac install: no date, never counted
                return true;
            }
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number)
                minutes = (int)m.GetDouble();
            if (root.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } s)
                date = s;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Same accepted spellings as the Mac daemon's _as_bool.</summary>
    public static bool? AsBool(string payload) => payload.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "on" or "yes" => true,
        "0" or "false" or "off" or "no" => false,
        _ => null,
    };
}
