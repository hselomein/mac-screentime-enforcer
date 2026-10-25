using System.Text.Json;

namespace ScreenTimeService;

/// <summary>
/// The last Offline Mode / Offline Grace Minutes received from HA for one
/// kid, saved to disk. Same as the Mac daemon's KidSettings: these settings
/// matter most right after a reboot with the broker unreachable, which is
/// exactly when they can't be fetched, so the saved copy is what applies
/// then. Null = never set in HA, use config.json.
/// </summary>
public sealed class KidSettings
{
    /// <summary>The HA select's options (what a parent sees) -> fail mode.</summary>
    public static readonly IReadOnlyDictionary<string, string> OfflineModeOptions = new Dictionary<string, string>
    {
        ["Block"] = "safe",
        ["Allow"] = "open",
        ["Allow for a while"] = "grace",
    };

    private readonly string _path;
    private readonly object _gate = new();

    public KidSettings(string path)
    {
        _path = path;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("fail_mode", out var m) && m.GetString() is "safe" or "open" or "grace")
                FailMode = m.GetString();
            if (root.TryGetProperty("fail_grace_minutes", out var g) && g.ValueKind == JsonValueKind.Number)
                FailGraceMinutes = g.GetDouble();
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            Log.Warn($"Failed to read {path}, using config.json defaults: {ex.Message}");
        }
    }

    public string? FailMode { get; private set; }
    public double? FailGraceMinutes { get; private set; }

    /// <summary>Accepts the select's labels or the raw safe/open/grace values.</summary>
    public static string? ParseOfflineMode(string payload)
    {
        var text = payload.Trim();
        if (OfflineModeOptions.TryGetValue(text, out var mode))
            return mode;
        return text.ToLowerInvariant() is "safe" or "open" or "grace" ? text.ToLowerInvariant() : null;
    }

    public bool SetFailMode(string mode) => Update(mode, FailGraceMinutes);

    public bool SetFailGraceMinutes(double minutes) => Update(FailMode, minutes);

    /// <summary>Saves and returns true only if something changed. Called from MQTTnet's thread.</summary>
    private bool Update(string? mode, double? grace)
    {
        lock (_gate)
        {
            if (mode == FailMode && grace == FailGraceMinutes)
                return false;
            FailMode = mode;
            FailGraceMinutes = grace;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["fail_mode"] = mode,
                    ["fail_grace_minutes"] = grace,
                }));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Unable to save {_path}.", ex);
            }
            return true;
        }
    }
}
