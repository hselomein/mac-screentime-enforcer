namespace ScreenTimeService;

/// <summary>Where the service keeps its files. ProgramData, locked down to SYSTEM and Administrators by install.ps1.</summary>
public static class Paths
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ScreenTimeEnforcer");

    public static readonly string ConfigFile = Path.Combine(DataDir, "config.json");
    public static readonly string StateDir = Path.Combine(DataDir, "state");
    public static readonly string LogFile = Path.Combine(DataDir, "logs", "service.log");

    /// <summary>The per-session helper ships next to the service exe.</summary>
    public static readonly string HelperExe = Path.Combine(AppContext.BaseDirectory, "ScreenTimeHelper.exe");
}

/// <summary>
/// Plain file + console logging in the same "time LEVEL message" shape as
/// the Mac daemon's /var/log/screentime-enforcer.log. Rolls over to
/// service.log.1 at 5 MB so a long-running PC can't fill its disk.
/// </summary>
public static class Log
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARNING", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}\n{ex}");
    public static void Critical(string message) => Write("CRITICAL", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Paths.LogFile)!);
                var info = new FileInfo(Paths.LogFile);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(Paths.LogFile, Paths.LogFile + ".1", overwrite: true);
                File.AppendAllText(Paths.LogFile, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never take the service down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
