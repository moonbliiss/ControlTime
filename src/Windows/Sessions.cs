using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ControlTime;

internal static class Sessions
{
    internal sealed record Session(int Id, string Sid, bool Active);

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
                if (entry.Id == 0)
                    continue;

                if (!WTSQueryUserToken(entry.Id, out var token))
                {
                    int error = Marshal.GetLastPInvokeError();
                    token?.Dispose();
                    if (error is 1008 or 7022) // No user token, or the session ended while enumerating.
                        continue;
                    throw new Win32Exception(error);
                }

                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                {
                    if (identity.User is { } sid)
                        result.Add(new Session(entry.Id, sid.Value, entry.State == 0));
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
    private static extern bool WTSQueryUserToken(int sessionId, out SafeAccessTokenHandle token);

    [DllImport("wtsapi32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool WTSLogoffSession(IntPtr server, int sessionId, bool wait);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool MoveFileExW(string source, string destination, int flags);
}
