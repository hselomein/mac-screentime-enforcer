using System.Text.Json;

namespace ScreenTimeService;

/// <summary>
/// One kid's minutes on this PC today, persisted so a service restart or
/// reboot doesn't reset them, and reset at local midnight. Same file format
/// and atomic-write pattern as the Mac daemon's UsageState:
/// {"date": "YYYY-MM-DD", "seconds_today": N}.
/// </summary>
public sealed class UsageState
{
    private readonly string _path;
    private string _date;
    private double _secondsToday;

    public UsageState(string path)
    {
        _path = path;
        _date = Today();
        Load();
    }

    public string Date => _date;

    public int MinutesToday => (int)(_secondsToday / 60);

    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    private void Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            if (root.TryGetProperty("date", out var d) && d.GetString() == Today()
                && root.TryGetProperty("seconds_today", out var s) && s.ValueKind == JsonValueKind.Number)
            {
                _secondsToday = Math.Max(0, s.GetDouble());
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            Log.Warn($"Failed to read state file {_path}, starting fresh: {ex.Message}");
        }
    }

    public void AddSeconds(double seconds) => _secondsToday += Math.Max(0, seconds);

    /// <summary>Called every tick for every kid, so a kid who isn't on this PC still resets at midnight.</summary>
    public void EnsureToday()
    {
        var today = Today();
        if (_date == today) return;
        _date = today;
        _secondsToday = 0;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["date"] = _date,
                ["seconds_today"] = _secondsToday,
            }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Unable to persist usage state to {_path}.", ex);
        }
    }
}
