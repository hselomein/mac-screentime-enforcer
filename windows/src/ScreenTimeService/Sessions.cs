using System.Runtime.InteropServices;
using static ScreenTimeService.Native;

namespace ScreenTimeService;

/// <summary>A snapshot of who is at the physical console and whether it's locked.</summary>
public readonly record struct ConsoleState(int SessionId, string? User, bool Locked);

/// <summary>
/// The Windows equivalents of the Mac daemon's get_console_user,
/// is_screen_locked and get_all_logged_in_sessions. A service running as
/// LocalSystem can read every session on the machine through the WTS APIs,
/// so none of the Mac's session-boundary workarounds (ioreg, ps for
/// loginwindow) are needed here.
/// </summary>
public static class Sessions
{
    /// <summary>
    /// Whoever owns the physical console right now, or User=null at the
    /// sign-in screen. Fast user switching moves the console to the other
    /// user's session, so this follows a switch the same way the Mac's
    /// SCDynamicStore console user does.
    /// </summary>
    public static ConsoleState GetConsole()
    {
        var id = WTSGetActiveConsoleSessionId();
        if (id == NoConsoleSession)
            return new ConsoleState(-1, null, true);
        var sessionId = (int)id;
        var user = UserName(sessionId);
        if (string.IsNullOrEmpty(user))
            return new ConsoleState(sessionId, null, true);
        return new ConsoleState(sessionId, user, IsLocked(sessionId));
    }

    public static string? ConsoleUser() => GetConsole().User;

    /// <summary>
    /// SessionFlags from WTSSessionInfoEx is the lock state Windows itself
    /// tracks. If it reports "unknown", fall back to the connect state:
    /// treating unknown as locked (the Mac's choice when ioreg fails) would
    /// pause accounting AND enforcement indefinitely if it stuck.
    /// </summary>
    public static bool IsLocked(int sessionId)
    {
        if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, sessionId, WTS_INFO_CLASS.WTSSessionInfoEx, out var buffer, out _))
            return true;
        try
        {
            var info = Marshal.PtrToStructure<WTSINFOEXW>(buffer);
            if (info.Level != 1)
                return true;
            return info.Data.SessionFlags switch
            {
                WTS_SESSIONSTATE_LOCK => true,
                WTS_SESSIONSTATE_UNLOCK => false,
                _ => info.Data.SessionState != WTS_CONNECTSTATE_CLASS.WTSActive,
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// {user name: session id} for every signed-in user, including sessions
    /// left running in the background by fast user switching
    /// (WTSDisconnected). Like the Mac version, this is for session_state
    /// reporting and logging, not enforcement.
    /// </summary>
    public static Dictionary<string, int> AllSignedIn()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!WTSEnumerateSessionsW(WTS_CURRENT_SERVER_HANDLE, 0, 1, out var buffer, out var count))
            return result;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFOW>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFOW>(buffer + i * size);
                if (info.State is not (WTS_CONNECTSTATE_CLASS.WTSActive or WTS_CONNECTSTATE_CLASS.WTSDisconnected))
                    continue;
                var user = UserName(info.SessionId);
                if (!string.IsNullOrEmpty(user))
                    result[user] = info.SessionId;
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
        return result;
    }

    private static string? UserName(int sessionId)
    {
        if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, sessionId, WTS_INFO_CLASS.WTSUserName, out var buffer, out _))
            return null;
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// The lock fallback: disconnecting the console session sends it to the
    /// sign-in screen, which needs the kid's password (or PIN) to get back
    /// in. Coarser than a lock (it looks like a user switch), but it works
    /// from the service without anything running in the kid's session.
    /// </summary>
    public static bool Disconnect(int sessionId) =>
        WTSDisconnectSession(WTS_CURRENT_SERVER_HANDLE, sessionId, false);
}
