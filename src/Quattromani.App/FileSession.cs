using Quattromani.Core;
using Quattromani.Mcp;
using Avalonia.Threading;
using SkiaSharp;

namespace Quattromani.App;

/// <summary>
/// The file of the open document: current path, unsaved changes, saving, opening and export.
/// Used by both the window (for the user) and the MCP tools (for the agent), so it can be called from any thread.
/// </summary>
sealed class FileSession(CommandBus bus) : IFileService
{
    long _savedVersion;

    public string? CurrentPath { get; private set; }
    public bool IsDirty => bus.Version != _savedVersion;

    /// <summary>The current file or the "modified" state changed (always on the UI thread).</summary>
    public event Action? StateChanged;

    void Notify() => Dispatcher.UIThread.Post(() => StateChanged?.Invoke());

    // Writing goes through a temporary file: if something goes wrong the existing file is not damaged.
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
            try { File.Delete(tmp); } catch { /* the temporary file is not important */ }
            throw new CommandException($"Cannot write '{path}': {ex.Message}");
        }
    }

    public string Save(string? path)
    {
        path ??= CurrentPath ?? throw new CommandException("The document has not been saved yet: give a path.");
        var (json, version) = bus.Read(d => (DocumentFile.Save(d), d.Version));
        WriteAtomic(path, tmp => File.WriteAllText(tmp, json));
        CurrentPath = path;
        _savedVersion = version;
        RecentFiles.Add(path);
        Notify();
        return path;
    }

    public void Open(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommandException($"Cannot read '{path}': {ex.Message}");
        }
        var doc = DocumentFile.Load(text); // if the file is not valid, the current document is not touched
        bus.Replace(doc, "open", path);
        CurrentPath = path;
        _savedVersion = bus.Version;
        RecentFiles.Add(path);
        Notify();
    }

    /// <summary>Reopens a backup copy: the document is marked modified (not saved) and, if it had a file, finds it again.</summary>
    public void Recover(string recoveryFile, string? originalPath)
    {
        string text;
        try { text = File.ReadAllText(recoveryFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommandException($"Cannot read the recovery copy: {ex.Message}");
        }
        var doc = DocumentFile.Load(text);
        bus.Replace(doc, "recover", originalPath);
        CurrentPath = originalPath;
        _savedVersion = -1;
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

    public const int MaxPngSide = 8192;

    public (int Width, int Height) ExportPng(string path, double scale, string? background)
    {
        var (w, h) = bus.Read(d => (d.Pages[0].Width, d.Pages[0].Height));
        if (!(scale > 0 && double.IsFinite(scale))) throw new CommandException("scale must be a number greater than 0.");
        var side = Math.Max(w, h) * scale;
        if (side > MaxPngSide)
            throw new CommandException($"The image would be {Math.Round(w * scale)} x {Math.Round(h * scale)} pixels: the longest side may not exceed {MaxPngSide}. Use a lower scale (at most {Math.Round(MaxPngSide / Math.Max(w, h), 2)} for this page).");
        // The page and nothing else: what goes beyond the edges does not enter the image.
        var img = new SkiaDocumentRenderer(bus).Render(new Box(0, 0, w, h), (int)Math.Max(1, Math.Round(side)), background ?? "transparent", labels: false, showPage: false);
        WriteAtomic(path, tmp => File.WriteAllBytes(tmp, img.Png));
        return (img.PixelWidth, img.PixelHeight);
    }

    public void ExportSvg(string path)
    {
        var svg = bus.Read(SvgFile.Export);
        WriteAtomic(path, tmp => File.WriteAllText(tmp, svg));
    }
}

/// <summary>Vector PDF: texts (embedded fonts), curves, gradients and patterns stay vector; the page has its real size.</summary>
static class PdfExporter
{
    public static void Export(CommandBus bus, string path, string? sourceFile)
    {
        var (items, page) = bus.Read(d => (d.RenderObjects.Select(Item.From).ToList(), (d.Pages[0].Width, d.Pages[0].Height)));
        const float PtPerPx = 72f / 96f; // the document is in px at 96 dpi, the PDF in points (1/72 of an inch)

        using var stream = File.Create(path);
        using var pdf = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Title = sourceFile is null ? "Untitled" : Path.GetFileNameWithoutExtension(sourceFile),
            Creator = "Quattromani Draw",
            Producer = "Quattromani Draw (Skia/PDF)",
            Creation = DateTime.Now,
            Modified = DateTime.Now,
        });
        var canvas = pdf.BeginPage((float)page.Width * PtPerPx, (float)page.Height * PtPerPx);
        canvas.Scale(PtPerPx);
        canvas.ClipRect(new SKRect(0, 0, (float)page.Width, (float)page.Height)); // what goes beyond the page is not printed
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var it in items) SceneRenderer.DrawItem(canvas, paint, it, vectorPatterns: true);
        pdf.EndPage();
        pdf.Close();
    }
}
