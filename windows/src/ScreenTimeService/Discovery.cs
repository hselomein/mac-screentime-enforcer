using System.Reflection;
using System.Text.Json;

namespace ScreenTimeService;

/// <summary>
/// Home Assistant MQTT discovery, matching the Mac daemon's _publish_discovery:
///  - per PC (only this PC knows them): Minutes, Active, Online, Session State,
///    under a device named "&lt;child&gt; pc (&lt;name&gt;)";
///  - per kid (one across every Mac and PC): Allowed, Daily Budget, Bonus
///    Minutes, Max Bonus Minutes, Parent Override, Total Minutes Today,
///    under the kid's shared device. These must carry the same fields and
///    values the Macs send, or the shared entities flip back and forth
///    depending on which machine published last.
/// </summary>
public static class Discovery
{
    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static IEnumerable<(string Topic, string Payload)> Messages(ServiceConfig config, string child, string prefix)
    {
        var deviceId = config.DeviceId;
        // A machine-specific name keeps HA from inventing unpredictable
        // entity_ids when two devices for the same kid share a display name
        // (seen for real on the Macs).
        var pcDevice = new Dictionary<string, object>
        {
            ["identifiers"] = new[] { $"{child}_{deviceId}_win" },
            ["name"] = $"{child} pc ({config.DeviceFriendlyName ?? deviceId})",
            ["manufacturer"] = "Screen Time Agent",
            ["model"] = "Windows agent",
            ["sw_version"] = Version,
        };
        var sharedDevice = new Dictionary<string, object>
        {
            ["identifiers"] = new[] { Topics.SharedBaseId(child) },
            ["name"] = child,
            ["manufacturer"] = "Screen Time Agent",
            ["model"] = "Kid (all devices)",
        };
        var pcBase = $"{child}_{deviceId}";
        var sb = Topics.SharedBaseId(child);

        Dictionary<string, object> Availability() => new()
        {
            ["availability_topic"] = Topics.Availability(prefix, deviceId),
            ["payload_available"] = "online",
            ["payload_not_available"] = "offline",
        };

        Dictionary<string, object> MinutesNumber(string name, string topic, int max, string icon, int step = 5) => new()
        {
            ["name"] = name,
            ["state_topic"] = topic,
            ["command_topic"] = topic,
            ["max"] = max,
            ["min"] = 0,
            ["step"] = step,
            ["mode"] = "box",
            ["retain"] = true,
            ["unit_of_measurement"] = "min",
            ["icon"] = icon,
            ["device"] = sharedDevice,
        };

        var entities = new List<(string Domain, string UniqueId, Dictionary<string, object> Payload)>
        {
            // --- per PC ---
            ("sensor", $"{pcBase}_minutes", new()
            {
                ["name"] = "Minutes",
                ["state_topic"] = Topics.Minutes(prefix, deviceId),
                ["value_template"] = "{{ value_json.minutes }}",
                ["device_class"] = "duration",
                ["state_class"] = "total_increasing",
                ["unit_of_measurement"] = "min",
                ["icon"] = "mdi:timer-outline",
                ["device"] = pcDevice,
            }),
            ("binary_sensor", $"{pcBase}_active", Merge(new()
            {
                ["name"] = "Active",
                ["state_topic"] = Topics.Active(prefix, deviceId),
                ["payload_on"] = "1",
                ["payload_off"] = "0",
                ["device_class"] = "running",
                ["icon"] = "mdi:laptop",
                ["device"] = pcDevice,
            }, Availability())),
            ("binary_sensor", $"{pcBase}_online", new()
            {
                ["name"] = "Online",
                ["state_topic"] = Topics.Availability(prefix, deviceId),
                ["payload_on"] = "online",
                ["payload_off"] = "offline",
                ["device_class"] = "connectivity",
                ["icon"] = "mdi:lan-connect",
                ["device"] = pcDevice,
            }),
            ("sensor", $"{pcBase}_session_state", new()
            {
                ["name"] = "Session State",
                ["state_topic"] = Topics.SessionState(prefix, deviceId),
                ["icon"] = "mdi:account-clock",
                ["device"] = pcDevice,
            }),
            // --- per kid, shared with every Mac and PC ---
            ("switch", $"{sb}_allowed", new()
            {
                ["name"] = "Allowed",
                ["state_topic"] = Topics.Allowed(prefix),
                ["command_topic"] = Topics.Allowed(prefix),
                ["payload_on"] = "1",
                ["payload_off"] = "0",
                ["retain"] = true,
                ["icon"] = "mdi:shield-check",
                ["device"] = sharedDevice,
            }),
            ("number", $"{sb}_daily_budget", MinutesNumber("Daily Budget", Topics.DailyBudget(child), 600, "mdi:timer-sand")),
            ("number", $"{sb}_bonus_minutes", MinutesNumber("Bonus Minutes", Topics.BonusMinutes(child), 240, "mdi:timer-plus")),
            ("number", $"{sb}_max_bonus_minutes", MinutesNumber("Max Bonus Minutes", Topics.MaxBonusMinutes(child), 240, "mdi:timer-lock")),
            ("select", $"{sb}_offline_mode", new()
            {
                ["name"] = "Offline Mode",
                ["state_topic"] = Topics.OfflineMode(child),
                ["command_topic"] = Topics.OfflineMode(child),
                ["options"] = KidSettings.OfflineModeOptions.Keys.ToArray(),
                ["retain"] = true,
                ["icon"] = "mdi:cloud-off-outline",
                ["device"] = sharedDevice,
            }),
            ("number", $"{sb}_offline_grace_minutes", MinutesNumber("Offline Grace Minutes", Topics.OfflineGraceMinutes(child), 1440, "mdi:timer-cog-outline", step: 15)),
            ("switch", $"{sb}_parent_override", new()
            {
                ["name"] = "Parent Override",
                ["state_topic"] = Topics.OverrideState(child),
                ["command_topic"] = Topics.OverrideState(child),
                ["payload_on"] = "ON",
                ["payload_off"] = "OFF",
                ["retain"] = true,
                ["icon"] = "mdi:shield-star",
                ["device"] = sharedDevice,
            }),
            ("sensor", $"{sb}_total_minutes_today", new()
            {
                ["name"] = "Total Minutes Today",
                ["state_topic"] = Topics.TotalMinutes(prefix),
                ["device_class"] = "duration",
                ["state_class"] = "total_increasing",
                ["unit_of_measurement"] = "min",
                ["icon"] = "mdi:timer-sand-complete",
                ["device"] = sharedDevice,
            }),
        };

        foreach (var (domain, uniqueId, payload) in entities)
        {
            payload["unique_id"] = uniqueId;
            yield return ($"homeassistant/{domain}/{uniqueId}/config", JsonSerializer.Serialize(payload));
        }
    }

    private static Dictionary<string, object> Merge(Dictionary<string, object> a, Dictionary<string, object> b)
    {
        foreach (var (k, v) in b)
            a[k] = v;
        return a;
    }
}
