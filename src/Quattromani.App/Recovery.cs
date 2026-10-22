using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using Quattromani.Core;

namespace Quattromani.App;

/// <summary>
/// Copia di sicurezza del lavoro non salvato: ogni pochi secondi, se il documento è cambiato, ne scrive una copia in
/// ~/.quattromani/recovery. Si cancella quando il documento viene salvato o chiuso di proposito; se l'app si interrompe
/// (errore, blocco, spegnimento) resta lì e all'avvio successivo si offre di ripristinarla.
/// </summary>
sealed class Recovery
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quattromani", "recovery");
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan KeepStale = TimeSpan.FromDays(14);

    sealed record Meta(string? Path, DateTime SavedAt);

    readonly CommandBus _bus;
    readonly FileSession _files;
    readonly object _write = new();
    long _writtenVersion = -1;
    string CopyFile => Path.Combine(Dir, $"{Environment.ProcessId}.quattromani");
    string MetaFile => Path.Combine(Dir, $"{Environment.ProcessId}.json");

    public Recovery(CommandBus bus, FileSession files)
    {
        _bus = bus;
        _files = files;
    }

    public void Start()
    {
        // Salvato, aperto o nuovo: la copia di sicurezza non serve più.
        _files.StateChanged += () => { if (!_files.IsDirty) Delete(); };
        new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => Tick()).Start();
    }

    void Tick()
    {
        if (!_files.IsDirty || _bus.Version == _writtenVersion) return;
        _ = Task.Run(Write); // la serializzazione di un documento con immagini non deve fermare l'interfaccia
    }

    /// <summary>Scrive subito la copia; false se non c'era nulla da salvare o la scrittura è fallita.</summary>
    public bool Write()
    {
        lock (_write)
        {
            try
            {
                if (!_files.IsDirty) return false;
                var (json, version) = _bus.Read(d => (DocumentFile.Save(d), d.Version));
                Directory.CreateDirectory(Dir);
                var tmp = CopyFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, CopyFile, overwrite: true);
                File.WriteAllText(MetaFile, JsonSerializer.Serialize(new Meta(_files.CurrentPath, DateTime.Now)));
                _writtenVersion = version;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false; // è un salvataggio di cortesia: se non riesce, si riprova al giro dopo
            }
        }
    }

    public void Delete()
    {
        lock (_write)
        {
            _writtenVersion = -1;
            try { File.Delete(CopyFile); File.Delete(MetaFile); File.Delete(CopyFile + ".tmp"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>La copia più recente lasciata da un'istanza che non c'è più (null se non ce ne sono).</summary>
    public static (string File, string? OriginalPath, DateTime SavedAt)? FindStale()
    {
        if (!Directory.Exists(Dir)) return null;
        (string File, string? Path, DateTime At)? best = null;
        foreach (var file in Directory.EnumerateFiles(Dir, "*.quattromani"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid) || IsRunning(pid)) continue;
            var at = File.GetLastWriteTime(file);
            if (DateTime.Now - at > KeepStale) { TryDelete(file); continue; }
            string? path = null;
            try { path = JsonSerializer.Deserialize<Meta>(File.ReadAllText(Path.ChangeExtension(file, ".json")), JsonOptions)?.Path; }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            if (best is null || at > best.Value.At) best = (file, path, at);
        }
        return best is { } b ? (b.File, b.Path, b.At) : null;
    }

    /// <summary>Toglie una copia ripristinata o scartata.</summary>
    public static void Discard(string file)
    {
        TryDelete(file);
        TryDelete(Path.ChangeExtension(file, ".json"));
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static bool IsRunning(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName.Contains("Quattromani", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

/// <summary>Registro degli errori imprevisti: ~/.quattromani/crash.log (tenuto sotto 1 MB).</summary>
static class CrashLog
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quattromani", "crash.log");

    public static void Write(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where} (v{typeof(CrashLog).Assembly.GetName().Version?.ToString(3)}, {Environment.OSVersion})\n{ex}\n\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Console.Error.WriteLine($"{where}: {ex}");
    }
}
