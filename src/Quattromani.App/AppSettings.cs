using System.Text.Json;

namespace Quattromani.App;

/// <summary>Small user preferences, in ~/.quattromani/settings.json.</summary>
static class AppSettings
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quattromani", "settings.json");

    sealed class Data
    {
        /// <summary>Id of the terminal used by "Launch agent" (see <see cref="AgentLauncher.Terminals"/>); null = system default.</summary>
        public string? Terminal { get; set; }
    }

    static Data Load()
    {
        try { return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public static string? Terminal
    {
        get => Load().Terminal;
        set
        {
            var data = Load();
            data.Terminal = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // a preference is not worth an error
        }
    }
}
