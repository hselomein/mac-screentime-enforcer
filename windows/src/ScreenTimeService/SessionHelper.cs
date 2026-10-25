using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using static ScreenTimeService.Native;

namespace ScreenTimeService;

/// <summary>
/// Runs ScreenTimeHelper.exe inside a kid's own session for the two things
/// a service in session 0 can't do itself: speak through their speakers and
/// lock their workstation (LockWorkStation only works from inside the
/// session it locks).
///
/// Unlike the Mac's user_voice_helper.py, the helper isn't a long-running
/// login item listening on MQTT: the service starts a fresh copy as the kid
/// for each command (WTSQueryUserToken + CreateProcessAsUser, which only
/// LocalSystem may call). So there's nothing for a kid to quit, and the
/// helper never needs the MQTT password.
/// </summary>
public sealed class SessionHelper : IDisposable
{
    private readonly BlockingCollection<(int SessionId, string Text)> _speech = new();
    private readonly Thread _speechThread;

    public SessionHelper()
    {
        // One phrase at a time, in order, like `say` on the Mac helper.
        _speechThread = new Thread(SpeechLoop) { IsBackground = true, Name = "speech" };
        _speechThread.Start();
    }

    public void Speak(int sessionId, string text) => _speech.Add((sessionId, text));

    /// <summary>Starts the helper's lock command; the caller checks whether the session actually locked.</summary>
    public bool RequestLock(int sessionId) => Run(sessionId, "lock", waitMs: 0);

    private void SpeechLoop()
    {
        foreach (var (sessionId, text) in _speech.GetConsumingEnumerable())
        {
            Log.Info($"Speaking in session {sessionId}: {text}");
            // Quotes are the only character that could break the command line.
            Run(sessionId, $"speak \"{text.Replace("\"", "")}\"", waitMs: 60_000);
        }
    }

    private static bool Run(int sessionId, string arguments, uint waitMs)
    {
        if (!File.Exists(Paths.HelperExe))
        {
            Log.Error($"Helper not found at {Paths.HelperExe}.");
            return false;
        }
        if (!WTSQueryUserToken(sessionId, out var token))
        {
            // Error 1314 here means the service isn't running as LocalSystem
            // (e.g. started from an admin console for testing).
            Log.Warn($"WTSQueryUserToken failed for session {sessionId} (error {Marshal.GetLastWin32Error()}).");
            return false;
        }
        var environment = IntPtr.Zero;
        try
        {
            CreateEnvironmentBlock(out environment, token, false);
            var startup = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                lpDesktop = @"winsta0\default",
            };
            var commandLine = new StringBuilder($"\"{Paths.HelperExe}\" {arguments}");
            if (!CreateProcessAsUserW(token, Paths.HelperExe, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, environment, AppContext.BaseDirectory,
                    ref startup, out var process))
            {
                Log.Warn($"Couldn't start the helper in session {sessionId} (error {Marshal.GetLastWin32Error()}).");
                return false;
            }
            if (waitMs > 0)
                WaitForSingleObject(process.hProcess, waitMs);
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
            CloseHandle(token);
        }
    }

    public void Dispose()
    {
        _speech.CompleteAdding();
        _speechThread.Join(TimeSpan.FromSeconds(2));
        _speech.Dispose();
    }
}
