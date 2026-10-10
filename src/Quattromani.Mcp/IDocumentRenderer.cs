using Quattromani.Core;

namespace Quattromani.Mcp;

/// <param name="Area">Zona del documento mostrata, in unità documento.</param>
public sealed record RenderedImage(byte[] Png, int PixelWidth, int PixelHeight, Box Area);

/// <summary>Disegna il documento in un PNG. Lo implementa l'app (che ha già Skia), così Mcp non dipende da librerie grafiche.</summary>
public interface IDocumentRenderer
{
    /// <param name="area">Zona da mostrare; null = la pagina e tutto il contenuto.</param>
    /// <param name="maxSize">Lato maggiore dell'immagine, in pixel.</param>
    /// <param name="background">"#RRGGBB", "#RRGGBBAA" oppure "transparent"; null = bianco.</param>
    /// <param name="labels">Scrive id e nome sopra ogni oggetto.</param>
    /// <param name="showPage">Disegna il contorno della pagina.</param>
    RenderedImage Render(Box? area, int maxSize, string? background, bool labels, bool showPage);
}
