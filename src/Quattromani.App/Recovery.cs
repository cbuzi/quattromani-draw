using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using Quattromani.Core;

namespace Quattromani.App;

/// <summary>
/// Backup of unsaved work: every few seconds, if the document changed, it writes a copy in
/// ~/.quattromani/recovery. It is deleted when the document is saved or closed on purpose; if the app is interrupted
/// (error, freeze, shutdown) it stays there and at the next start the user is offered to restore it.
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
        // Saved, opened or new: the backup is no longer needed.
        _files.StateChanged += () => { if (!_files.IsDirty) Delete(); };
        new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => Tick()).Start();
    }

    void Tick()
    {
        if (!_files.IsDirty || _bus.Version == _writtenVersion) return;
        _ = Task.Run(Write); // serializing a document with images must not freeze the interface
    }

    /// <summary>Writes the copy now; false if there was nothing to save or the write failed.</summary>
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
                return false; // this is a courtesy save: if it fails, try again next round
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

    /// <summary>The most recent copy left by an instance that no longer exists (null if there are none).</summary>
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

    /// <summary>Removes a restored or discarded copy.</summary>
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

/// <summary>Log of unexpected errors: ~/.quattromani/crash.log (kept under 1 MB).</summary>
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
