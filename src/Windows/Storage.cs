using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace ControlTime;

internal sealed record Profile(Settings Settings, ParentPassword Password);

internal static class Storage
{
    private static readonly string DirectoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private static readonly string PrivatePath = Path.Combine(DirectoryPath, "private");

    public static Profile? ReadProfile() => Read<Profile>(PrivatePath, "profile.json");
    public static Settings? ReadLegacySettings() => Read<Settings>(DirectoryPath, "settings.json");
    public static Usage? ReadUsage() => Read<Usage>(DirectoryPath, "usage.json");
    public static Attempts ReadAttempts() => Read<Attempts>(PrivatePath, "attempts.json") ?? Attempts.Empty;
    public static void Save(Usage value) => Write(DirectoryPath, "usage.json", value);
    public static void Save(Attempts value) => Write(PrivatePath, "attempts.json", value);
    public static void Save(Profile value) => Write(PrivatePath, "profile.json", value);

    public static void InitializePrivateDirectory()
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var type in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(type, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        var directory = new DirectoryInfo(PrivatePath);
        if (directory.Exists)
            directory.SetAccessControl(acl);
        else
            directory.Create(acl);
    }

    private static T? Read<T>(string directory, string file) where T : class
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, file), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream)
                ?? throw new InvalidDataException("Пустой файл ControlTime.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void Write<T>(string directory, string file, T value)
    {
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            Sessions.ReplaceFile(temporary, Path.Combine(directory, file));
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
