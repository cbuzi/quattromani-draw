namespace Quattromani.Mcp;

/// <summary>Files and current document: implemented by the app (which knows where the document is, whether it has unsaved changes and how to export).</summary>
public interface IFileService
{
    string? CurrentPath { get; }
    /// <summary>The document has changes not yet saved.</summary>
    bool IsDirty { get; }
    /// <summary>Saves to <paramref name="path"/> (or to the current file if null). Returns the path used.</summary>
    string Save(string? path);
    void Open(string path);
    /// <summary>New empty document.</summary>
    void New();
    void ExportPdf(string path);
    void ExportSvg(string path);
    /// <summary>PNG of the page: <paramref name="scale"/> pixels per document unit (1 = 96 dpi); background "#RRGGBB", "#RRGGBBAA" or "transparent" (null = transparent).</summary>
    /// <returns>Size of the image in pixels.</returns>
    (int Width, int Height) ExportPng(string path, double scale, string? background);
}
