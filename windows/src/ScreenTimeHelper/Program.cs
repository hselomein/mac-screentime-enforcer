using System.Runtime.InteropServices;
using System.Speech.Synthesis;

namespace ScreenTimeHelper;

/// <summary>
/// Runs inside a kid's session, started by ScreenTimeService for one
/// command and then exits:
///   ScreenTimeHelper.exe speak "text"   say it with the kid's default voice
///   ScreenTimeHelper.exe lock           lock the workstation (Windows+L)
/// Both have to happen inside the session: a service in session 0 has no
/// speakers to talk through, and LockWorkStation only locks the session of
/// the process that calls it. It takes no network access or secrets.
/// </summary>
internal static class Program
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTimeEnforcer", "helper.log");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
            return 2;
        try
        {
            switch (args[0])
            {
                case "speak" when args.Length > 1:
                    using (var synth = new SpeechSynthesizer())
                    {
                        synth.SetOutputToDefaultAudioDevice();
                        synth.Speak(string.Join(" ", args.Skip(1)));
                    }
                    return 0;
                case "lock":
                    return Lock();
                default:
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Write($"'{args[0]}' failed: {ex}");
            return 1;
        }
    }

    /// <summary>Like the Mac helper: never lock unless this session is the one at the console.</summary>
    private static int Lock()
    {
        var mine = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
        if (mine != WTSGetActiveConsoleSessionId())
        {
            Write("Lock requested but this session isn't at the console; ignoring.");
            return 1;
        }
        if (!LockWorkStation())
        {
            Write($"LockWorkStation failed (error {Marshal.GetLastWin32Error()}).");
            return 1;
        }
        Write("Locked the workstation.");
        return 0;
    }

    private static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
