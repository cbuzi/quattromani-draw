namespace Quattromani.Core;

/// <param name="Kind">"do", "undo" oppure "redo".</param>
public sealed record Change(long Version, string Author, string Kind, Command Command);

public sealed record BatchResult(long Version, List<string> CreatedIds);

/// <summary>
/// Unico punto di modifica del documento, usato da umano e LLM. Thread-safe: ogni operazione prende lo stesso lock.
/// </summary>
public sealed class CommandBus(Document document)
{
    readonly object _gate = new();
    readonly List<Change> _log = [];
    readonly List<List<Command>> _undo = []; // l'ultimo è il prossimo da annullare
    readonly Stack<List<Command>> _redo = new();
    long _logFloor;

    /// <summary>Passi di undo conservati: oltre, i più vecchi vengono dimenticati (i comandi tengono in memoria anche immagini e oggetti eliminati).</summary>
    public const int MaxUndoSteps = 200;
    /// <summary>Voci del registro delle modifiche conservate per get_changes.</summary>
    public const int MaxLogEntries = 5000;

    public Document Document => document;

    /// <summary>Sollevato (fuori dal lock, su thread arbitrario) dopo ogni modifica.</summary>
    public event Action? Changed;

    public long Version { get { lock (_gate) return document.Version; } }

    /// <summary>Legge il documento sotto lock.</summary>
    public T Read<T>(Func<Document, T> read)
    {
        lock (_gate) return read(document);
    }

    /// <summary>Applica un batch come transazione: un solo step di undo; se un comando fallisce, nulla cambia.</summary>
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
    /// Sostituisce l'intero documento (apri o nuovo). Non è annullabile: l'undo ricomincia da zero; la versione però
    /// continua a crescere e nel registro resta una voce "reset", così chi legge get_changes sa che deve ricaricare.
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
    /// Sceglie il layer dove finiscono gli oggetti nuovi. Non è una modifica del contenuto: niente versione, niente undo, il documento non risulta "modificato".
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
    /// Le modifiche con versione fino a questa (compresa) sono state dimenticate per limitare la memoria: chi chiede le modifiche
    /// da una versione più vecchia non le riceverebbe tutte e deve rileggere il documento.
    /// </summary>
    public long LogFloor { get { lock (_gate) return _logFloor; } }

    /// <summary>Modifiche con versione &gt; sinceVersion, in ordine.</summary>
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
            var drop = _log.Count - MaxLogEntries * 4 / 5; // si taglia un po' di più, così non si ricompatta a ogni comando
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
