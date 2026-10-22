namespace Quattromani.Mcp;

/// <summary>File e documento corrente: lo implementa l'app (che sa dov'è il documento, se ha modifiche non salvate e come esportare).</summary>
public interface IFileService
{
    string? CurrentPath { get; }
    /// <summary>Il documento ha modifiche non ancora salvate.</summary>
    bool IsDirty { get; }
    /// <summary>Salva in <paramref name="path"/> (o nel file corrente se null). Restituisce il percorso usato.</summary>
    string Save(string? path);
    void Open(string path);
    /// <summary>Nuovo documento vuoto.</summary>
    void New();
    void ExportPdf(string path);
    void ExportSvg(string path);
    /// <summary>PNG della pagina: <paramref name="scale"/> pixel per unità documento (1 = 96 dpi); sfondo "#RRGGBB", "#RRGGBBAA" o "transparent" (null = trasparente).</summary>
    /// <returns>Dimensioni dell'immagine in pixel.</returns>
    (int Width, int Height) ExportPng(string path, double scale, string? background);
}
