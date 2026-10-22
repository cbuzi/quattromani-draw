namespace Quattromani.Core;

/// <param name="Kind">"do", "undo" or "redo".</param>
public sealed record Change(long Version, string Author, string Kind, Command Command);

public sealed record BatchResult(long Version, List<string> CreatedIds);

/// <summary>
/// Single point of modification of the document, used by human and LLM. Thread-safe: every operation takes the same lock.
/// </summary>
public sealed class CommandBus(Document document)
{
    readonly object _gate = new();
    readonly List<Change> _log = [];
    readonly List<List<Command>> _undo = []; // the last one is the next to undo
    readonly Stack<List<Command>> _redo = new();
    long _logFloor;

    /// <summary>Undo steps kept: beyond that, the oldest are forgotten (commands also keep images and deleted objects in memory).</summary>
    public const int MaxUndoSteps = 200;
    /// <summary>Entries of the change log kept for get_changes.</summary>
    public const int MaxLogEntries = 5000;

    public Document Document => document;

    /// <summary>Raised (outside the lock, on an arbitrary thread) after every change.</summary>
    public event Action? Changed;

    public long Version { get { lock (_gate) return document.Version; } }

    /// <summary>Reads the document under lock.</summary>
    public T Read<T>(Func<Document, T> read)
    {
        lock (_gate) return read(document);
    }

    /// <summary>Applies a batch as a transaction: a single undo step; if a command fails, nothing changes.</summary>
    public BatchResult Execute(IReadOnlyList<Command> commands, string author = "human")
    {
        if (author != "human" && !author.StartsWith("llm:"))
            throw new ArgumentException("author deve essere \"human\" oppure \"llm:<nome>\".", nameof(author));

        BatchResult result;
        lock (_gate)
        {
            var applied = new List<Command>();
            try
            {
                foreach (var cmd in commands)
                {
                    cmd.Author = author;
                    cmd.Apply(document);
                    document.RefreshPathTexts();
                    applied.Add(cmd);
                }
            }
            catch
            {
                for (var i = applied.Count - 1; i >= 0; i--) applied[i].Undo(document);
                document.RefreshPathTexts();
                throw;
            }

            if (applied.Count == 0) return new(document.Version, []);
            Record(applied, author, "do");
            PushUndo(applied);
            _redo.Clear();
            result = new(document.Version, applied.SelectMany(c => c.CreatedIds).ToList());
        }
        Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// Replaces the whole document (open or new). Not undoable: undo starts over; the version however
    /// keeps growing and a "reset" entry stays in the log, so whoever reads get_changes knows it must reload.
    /// </summary>
    public void Replace(Document loaded, string reason, string? file, string author = "human")
    {
        lock (_gate)
        {
            document.ReplaceWith(loaded);
            document.RefreshPathTexts();
            _undo.Clear();
            _redo.Clear();
            _log.Add(new(++document.Version, author, "reset", new DocumentReset { Reason = reason, File = file, Author = author }));
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Chooses the layer where new objects go. It is not a content change: no version, no undo, the document does not become "modified".
    /// </summary>
    public void SetActiveLayer(string name)
    {
        lock (_gate) document.Pages[0].ActiveLayer = document.FindLayer(name);
        Changed?.Invoke();
    }

    public bool Undo(string author = "human")
    {
        lock (_gate)
        {
            if (_undo.Count == 0) return false;
            var batch = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            for (var i = batch.Count - 1; i >= 0; i--) batch[i].Undo(document);
            document.RefreshPathTexts();
            Record(Enumerable.Reverse(batch), author, "undo");
            _redo.Push(batch);
        }
        Changed?.Invoke();
        return true;
    }

    public bool Redo(string author = "human")
    {
        lock (_gate)
        {
            if (!_redo.TryPop(out var batch)) return false;
            foreach (var cmd in batch) { cmd.Apply(document); document.RefreshPathTexts(); }
            Record(batch, author, "redo");
            PushUndo(batch);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Changes with a version up to this one (included) were forgotten to limit memory: whoever asks for changes
    /// from an older version would not receive them all and must reread the document.
    /// </summary>
    public long LogFloor { get { lock (_gate) return _logFloor; } }

    /// <summary>Changes with version &gt; sinceVersion, in order.</summary>
    public (long Version, List<Change> Changes) GetChanges(long sinceVersion)
    {
        lock (_gate) return (document.Version, _log.Where(c => c.Version > sinceVersion).ToList());
    }

    void Record(IEnumerable<Command> commands, string author, string kind)
    {
        foreach (var cmd in commands)
            _log.Add(new(++document.Version, author, kind, cmd));
        if (_log.Count > MaxLogEntries)
        {
            var drop = _log.Count - MaxLogEntries * 4 / 5; // cut a bit more, so we do not compact on every command
            _logFloor = _log[drop - 1].Version;
            _log.RemoveRange(0, drop);
        }
    }

    void PushUndo(List<Command> batch)
    {
        _undo.Add(batch);
        if (_undo.Count > MaxUndoSteps) _undo.RemoveAt(0);
    }
}
