using System.Runtime.InteropServices;
using System.Text;

namespace ScreenTimeService;

/// <summary>Win32 declarations: Remote Desktop Services (WTS) session APIs and process creation.</summary>
internal static class Native
{
    public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
    public const uint NoConsoleSession = 0xFFFFFFFF;

    public enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit,
    }

    public enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
        WTSSessionInfoEx = 25,
    }

    // WTSINFOEX_LEVEL1.SessionFlags. Correct on Windows 10/11; Windows 7 /
    // Server 2008 R2 reported them inverted, which doesn't matter here.
    public const int WTS_SESSIONSTATE_LOCK = 0;
    public const int WTS_SESSIONSTATE_UNLOCK = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WTS_SESSION_INFOW
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WTSINFOEX_LEVEL1_W
    {
        public int SessionId;
        public WTS_CONNECTSTATE_CLASS SessionState;
        public int SessionFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string DomainName;
        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
    }

    // The marshaler aligns Data to 8 bytes (it holds longs), matching the C
    // layout where the union starts at offset 8.
    [StructLayout(LayoutKind.Sequential)]
    public struct WTSINFOEXW
    {
        public int Level;
        public WTSINFOEX_LEVEL1_W Data;
    }

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, int sessionId, WTS_INFO_CLASS infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WTSEnumerateSessionsW(
        IntPtr hServer, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSDisconnectSession(IntPtr hServer, int sessionId, bool wait);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessAsUserW(
        IntPtr token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFOW startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
