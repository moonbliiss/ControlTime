using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ControlTime;

internal sealed record Request(string Command, string? Password = null, Settings? Settings = null,
    bool ResetToday = false);
internal sealed record Status(bool Configured, bool ParentMode, bool Blocked, string Message,
    Settings? Settings = null, string? Error = null);

internal static class ServiceChannel
{
    private const string PipeName = "ControlTime.Parent.v2";
    private const PipeAccessRights ClientRights =
        PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    public static NamedPipeServerStream Listen()
    {
        var acl = new PipeSecurity();
        acl.SetOwner(SystemSid);
        acl.AddAccessRule(new PipeAccessRule(SystemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            ClientRights, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, acl);
    }

    public static async Task<Status> Send(Request request)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var token = cancellation.Token;
        using var pipe = new NamedPipeClientStream(".", PipeName, ClientRights, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Anonymous, HandleInheritability.None);
        await pipe.ConnectAsync(token).ConfigureAwait(false);

        // The local user could create a pipe with the same name if the service is stopped.
        if (!SystemSid.Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("Не удалось подтвердить подлинность службы ControlTime.");

        await Write(pipe, request, token).ConfigureAwait(false);
        return await Read<Status>(pipe, token).ConfigureAwait(false);
    }

    public static int ClientSession(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out int session))
            throw new Win32Exception();
        return session;
    }

    public static async Task<T> Read<T>(PipeStream pipe, CancellationToken token)
    {
        byte[] header = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int size = BitConverter.ToInt32(header);
        if (size is < 1 or > 65536)
            throw new InvalidDataException("Недопустимый размер запроса.");

        byte[] data = new byte[size];
        try
        {
            await pipe.ReadExactlyAsync(data, token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public static async Task Write<T>(PipeStream pipe, T value, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            await pipe.WriteAsync(BitConverter.GetBytes(data.Length), token).ConfigureAwait(false);
            await pipe.WriteAsync(data, token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out int session);
}
