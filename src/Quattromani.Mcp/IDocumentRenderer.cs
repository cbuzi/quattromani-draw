using Quattromani.Core;

namespace Quattromani.Mcp;

/// <param name="Area">Part of the document shown, in document units.</param>
public sealed record RenderedImage(byte[] Png, int PixelWidth, int PixelHeight, Box Area);

/// <summary>Draws the document into a PNG. Implemented by the app (which already has Skia), so Mcp does not depend on graphics libraries.</summary>
public interface IDocumentRenderer
{
    /// <param name="area">Region to show; null = the page and all the content.</param>
    /// <param name="maxSize">Longest side of the image, in pixels.</param>
    /// <param name="background">"#RRGGBB", "#RRGGBBAA" or "transparent"; null = white.</param>
    /// <param name="labels">Writes id and name above each object.</param>
    /// <param name="showPage">Draws the outline of the page.</param>
    RenderedImage Render(Box? area, int maxSize, string? background, bool labels, bool showPage);
}
