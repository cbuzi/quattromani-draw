using System.Text.Json;

namespace Quattromani.App;

/// <summary>Ultimi documenti aperti o salvati, in ~/.quattromani/recent.json (il più recente per primo).</summary>
static class RecentFiles
{
    const int Max = 8;
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quattromani", "recent.json");

    /// <summary>I documenti ricordati che esistono ancora.</summary>
    public static List<string> Load()
    {
        try { return (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? []).Where(File.Exists).Take(Max).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public static void Add(string path)
    {
        var list = Load();
        list.RemoveAll(p => p.Equals(path, StringComparison.Ordinal));
        list.Insert(0, path);
        Save(list);
    }

    public static void Remove(string path) => Save(Load().Where(p => p != path).ToList());

    static void Save(List<string> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list.Take(Max)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // l'elenco dei recenti non vale un errore
    }
}
