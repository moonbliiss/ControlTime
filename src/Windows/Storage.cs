using System.Text.Json;

namespace ControlTime;

internal static class Storage
{
    private static readonly string DirectoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

    public static Settings? ReadSettings() => Read<Settings>("settings.json");
    public static Usage? ReadUsage() => Read<Usage>("usage.json");
    public static void Save(Settings settings) => Write("settings.json", settings);
    public static void Save(Usage usage) => Write("usage.json", usage);

    private static T? Read<T>(string file) where T : class
    {
        try
        {
            using var stream = new FileStream(Path.Combine(DirectoryPath, file), FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream)
                ?? throw new InvalidDataException("Пустой файл ControlTime.");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static void Write<T>(string file, T value)
    {
        string destination = Path.Combine(DirectoryPath, file);
        string temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            Sessions.ReplaceFile(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
