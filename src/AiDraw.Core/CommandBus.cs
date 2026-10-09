namespace AiDraw.Core;

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
    readonly Stack<List<Command>> _undo = new();
    readonly Stack<List<Command>> _redo = new();

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
                    applied.Add(cmd);
                }
            }
            catch
            {
                for (var i = applied.Count - 1; i >= 0; i--) applied[i].Undo(document);
                throw;
            }

            if (applied.Count == 0) return new(document.Version, []);
            Record(applied, author, "do");
            _undo.Push(applied);
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
            _undo.Clear();
            _redo.Clear();
            _log.Add(new(++document.Version, author, "reset", new DocumentReset { Reason = reason, File = file, Author = author }));
        }
        Changed?.Invoke();
    }

    public bool Undo(string author = "human")
    {
        lock (_gate)
        {
            if (!_undo.TryPop(out var batch)) return false;
            for (var i = batch.Count - 1; i >= 0; i--) batch[i].Undo(document);
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
            foreach (var cmd in batch) cmd.Apply(document);
            Record(batch, author, "redo");
            _undo.Push(batch);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Modifiche con versione &gt; sinceVersion, in ordine.</summary>
    public (long Version, List<Change> Changes) GetChanges(long sinceVersion)
    {
        lock (_gate) return (document.Version, _log.Where(c => c.Version > sinceVersion).ToList());
    }

    void Record(IEnumerable<Command> commands, string author, string kind)
    {
        foreach (var cmd in commands)
            _log.Add(new(++document.Version, author, kind, cmd));
    }
}
