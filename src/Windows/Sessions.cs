using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ControlTime;

internal static class Sessions
{
    internal sealed record Session(int Id, bool Active);

    private const int WtsActive = 0;
    private const int WtsConnected = 1;
    private const int WtsDisconnected = 4;
    private const int WtsUserName = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo
    {
        public int Id;
        public IntPtr Station;
        public int State;
    }

    public static List<Session> List()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr buffer, out int count))
            throw new Win32Exception();

        var result = new List<Session>();
        try
        {
            int size = Marshal.SizeOf<SessionInfo>();
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<SessionInfo>(IntPtr.Add(buffer, i * size));
                if (entry.Id == 0 || entry.State is not (WtsActive or WtsConnected or WtsDisconnected))
                    continue;

                if (!WTSQuerySessionInformationW(IntPtr.Zero, entry.Id, WtsUserName, out var user, out var bytes))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is 7022 or 1312) // The session ended while enumerating.
                        continue;
                    throw new Win32Exception(error, "Не удалось прочитать сеанс Windows (код " + error + ").");
                }

                try
                {
                    if (bytes > sizeof(char) && !string.IsNullOrEmpty(Marshal.PtrToStringUni(user)))
                        result.Add(new Session(entry.Id, entry.State == WtsActive));
                }
                finally
                {
                    WTSFreeMemory(user);
                }
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return result;
    }

    public static void LogOff(int sessionId)
    {
        if (!WTSLogoffSession(IntPtr.Zero, sessionId, false))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != 7022)
                throw new Win32Exception(error);
        }
    }

    public static void ShutDown()
    {
        using var process = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/s /f /t 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Не удалось запустить выключение.");
        if (!process.WaitForExit(5000) || process.ExitCode != 0)
            throw new InvalidOperationException("Windows не подтвердила выключение.");
    }

    public static void ReplaceFile(string source, string destination)
    {
        const int replaceExistingAndWriteThrough = 0x1 | 0x8;
        if (!MoveFileExW(source, destination, replaceExistingAndWriteThrough))
            throw new Win32Exception();
    }

    public static int ActiveConsole => unchecked((int)WTSGetActiveConsoleSessionId());

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version,
        out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass,
        out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WTSLogoffSession(IntPtr server, int sessionId, bool wait);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool MoveFileExW(string source, string destination, int flags);
}
