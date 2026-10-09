using AiDraw.Core;
using AiDraw.Mcp;
using Avalonia.Threading;
using SkiaSharp;

namespace AiDraw.App;

/// <summary>
/// Il file del documento aperto: percorso corrente, modifiche non salvate, salvataggio, apertura ed export.
/// Lo usano sia la finestra (per l'utente) sia i tool MCP (per l'agente), quindi può essere chiamato da qualsiasi thread.
/// </summary>
sealed class FileSession(CommandBus bus) : IFileService
{
    long _savedVersion;

    public string? CurrentPath { get; private set; }
    public bool IsDirty => bus.Version != _savedVersion;

    /// <summary>Cambiato il file corrente o lo stato "modificato" (sempre sul thread dell'interfaccia).</summary>
    public event Action? StateChanged;

    void Notify() => Dispatcher.UIThread.Post(() => StateChanged?.Invoke());

    // La scrittura passa da un file temporaneo: se qualcosa va storto il file esistente non si rovina.
    static void WriteAtomic(string path, Action<string> write)
    {
        var tmp = path + ".tmp";
        try
        {
            write(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch { /* il file temporaneo non è importante */ }
            throw new CommandException($"Non riesco a scrivere '{path}': {ex.Message}");
        }
    }

    public string Save(string? path)
    {
        path ??= CurrentPath ?? throw new CommandException("Il documento non e' ancora stato salvato: indica un percorso.");
        var (json, version) = bus.Read(d => (DocumentFile.Save(d), d.Version));
        WriteAtomic(path, tmp => File.WriteAllText(tmp, json));
        CurrentPath = path;
        _savedVersion = version;
        Notify();
        return path;
    }

    public void Open(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommandException($"Non riesco a leggere '{path}': {ex.Message}");
        }
        var doc = DocumentFile.Load(text); // se il file non è valido, il documento attuale non viene toccato
        bus.Replace(doc, "open", path);
        CurrentPath = path;
        _savedVersion = bus.Version;
        Notify();
    }

    public void New()
    {
        bus.Replace(new Document(), "new", null);
        CurrentPath = null;
        _savedVersion = bus.Version;
        Notify();
    }

    public void ExportPdf(string path) => WriteAtomic(path, tmp => PdfExporter.Export(bus, tmp, CurrentPath));
}

/// <summary>PDF vettoriale: testi (font incorporati), curve, gradienti e motivi restano vettoriali; la pagina ha la sua misura reale.</summary>
static class PdfExporter
{
    public static void Export(CommandBus bus, string path, string? sourceFile)
    {
        var (items, page) = bus.Read(d => (d.Objects.Select(Item.From).ToList(), (d.Pages[0].Width, d.Pages[0].Height)));
        const float PtPerPx = 72f / 96f; // il documento è in px a 96 dpi, il PDF in punti (1/72 di pollice)

        using var stream = File.Create(path);
        using var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Title = sourceFile is null ? "Untitled" : Path.GetFileNameWithoutExtension(sourceFile),
            Creator = "AiDraw",
            Producer = "AiDraw (Skia/PDF)",
            Creation = DateTime.Now,
            Modified = DateTime.Now,
        });
        var canvas = pdf.BeginPage((float)page.Width * PtPerPx, (float)page.Height * PtPerPx);
        canvas.Scale(PtPerPx);
        canvas.ClipRect(new SKRect(0, 0, (float)page.Width, (float)page.Height)); // quello che esce dalla pagina non si stampa
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var it in items) SceneRenderer.DrawItem(canvas, paint, it, vectorPatterns: true);
        pdf.EndPage();
        pdf.Close();
    }
}
