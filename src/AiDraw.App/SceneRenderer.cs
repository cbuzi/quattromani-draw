using System.Numerics;
using AiDraw.Core;
using AiDraw.Mcp;
using SkiaSharp;

namespace AiDraw.App;

enum Kind { Rectangle, Ellipse, Path, Group, Text }

sealed record TextInfo(List<TextLine> Lines, TextStyle Style);

// Copia immutabile degli oggetti: il rendering gira su un altro thread.
sealed record Item(string Id, Kind Kind, double X, double Y, double W, double H, Matrix3x2 M,
    string? Fill, string? Stroke, double StrokeWidth, Box Source, List<PathSeg>? Segments, List<Item>? Children,
    string? NodeKinds = null, TextInfo? Text = null)
{
    public Box Bounds => SceneObject.BoundsOf(X, Y, W, H, M);
    public Matrix3x2 Fit => SceneObject.FitMatrix(Source, new(X, Y, W, H));

    public static Item From(SceneObject o) => o switch
    {
        PathObject p => new(o.Id, Kind.Path, o.X, o.Y, o.Width, o.Height, o.Transform, o.Fill, o.Stroke, o.StrokeWidth,
            p.Source, p.Segments, null, p.NodeKinds),
        TextObject t => new(o.Id, Kind.Text, o.X, o.Y, o.Width, o.Height, o.Transform, o.Fill, o.Stroke, o.StrokeWidth,
            t.Source, null, null, Text: new TextInfo(t.Lines, t.Style)),
        GroupObject g => new(o.Id, Kind.Group, o.X, o.Y, o.Width, o.Height, o.Transform, o.Fill, o.Stroke, o.StrokeWidth,
            g.Source, null, g.Children.Select(From).ToList()),
        _ => new(o.Id, o is EllipseObject ? Kind.Ellipse : Kind.Rectangle, o.X, o.Y, o.Width, o.Height, o.Transform,
            o.Fill, o.Stroke, o.StrokeWidth, default, null, null),
    };
}

/// <summary>Disegno degli oggetti su un canvas Skia: lo usano sia la finestra sia il tool MCP render.</summary>
static class SceneRenderer
{
    static SKMatrix ToSk(Matrix3x2 m) => new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);

    // Path Skia dai segmenti, già stirati con `fit` (così il tratto resta uniforme).
    public static SKPath BuildPath(List<PathSeg> segs, Matrix3x2 fit)
    {
        static SKPoint P(double x, double y, Matrix3x2 m)
        {
            var v = Vector2.Transform(new((float)x, (float)y), m);
            return new(v.X, v.Y);
        }

        var path = new SKPath();
        foreach (var s in segs)
            switch (s.Op)
            {
                case 'M': path.MoveTo(P(s.X, s.Y, fit)); break;
                case 'L': path.LineTo(P(s.X, s.Y, fit)); break;
                case 'C': path.CubicTo(P(s.X1, s.Y1, fit), P(s.X2, s.Y2, fit), P(s.X, s.Y, fit)); break;
                case 'Z': path.Close(); break;
            }
        return path;
    }

    public static void DrawItem(SKCanvas canvas, SKPaint paint, Item it)
    {
        canvas.Save();
        var m = ToSk(it.M);
        canvas.Concat(in m);
        if (it.Kind == Kind.Group)
        {
            var fit = ToSk(it.Fit);
            canvas.Concat(in fit);
            foreach (var child in it.Children!) DrawItem(canvas, paint, child);
        }
        else
        {
            if (ParseColor(it.Fill) is { } fill)
            {
                paint.Style = SKPaintStyle.Fill; paint.Color = fill;
                DrawShape(canvas, paint, it);
            }
            if (ParseColor(it.Stroke) is { } stroke && it.StrokeWidth > 0)
            {
                paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = (float)it.StrokeWidth; paint.Color = stroke;
                DrawShape(canvas, paint, it);
            }
        }
        canvas.Restore();
    }

    static void DrawShape(SKCanvas canvas, SKPaint paint, Item it)
    {
        if (it.Kind == Kind.Text)
        {
            // Righe impaginate nello spazio di Source, stirate sul riquadro attuale.
            var fit = ToSk(it.Fit);
            canvas.Save();
            canvas.Concat(in fit);
            using var font = SkiaTextEngine.CreateFont(it.Text!.Style);
            foreach (var line in it.Text.Lines)
                canvas.DrawText(line.Text, (float)(it.Source.X + line.X), (float)(it.Source.Y + line.Y), font, paint);
            canvas.Restore();
            return;
        }
        if (it.Kind == Kind.Path)
        {
            using var path = BuildPath(it.Segments!, it.Fit);
            canvas.DrawPath(path, paint);
            return;
        }
        var r = SKRect.Create((float)it.X, (float)it.Y, (float)it.W, (float)it.H);
        if (it.Kind == Kind.Ellipse) canvas.DrawOval(r, paint); else canvas.DrawRect(r, paint);
    }

    // Formato del documento: "#RRGGBB" o "#RRGGBBAA" (diverso da quello ARGB di Skia).
    public static SKColor? ParseColor(string? s)
    {
        if (s is not { Length: 7 or 9 } || s[0] != '#' || !s[1..].All(Uri.IsHexDigit)) return null;
        var v = Convert.ToUInt32(s[1..], 16);
        return s.Length == 7
            ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new SKColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

/// <summary>Il PNG che vede l'LLM: stesso disegno della finestra, su una superficie fuori schermo.</summary>
sealed class SkiaDocumentRenderer(CommandBus bus) : IDocumentRenderer
{
    public RenderedImage Render(Box? area, int maxSize, string? background, bool labels)
    {
        var (items, names) = bus.Read(d => (d.Objects.Select(Item.From).ToList(), d.Objects.ToDictionary(o => o.Id, o => o.Name)));

        // Senza area: tutto il contenuto con un po' di margine (o una pagina vuota se non c'è nulla).
        Box box;
        if (area is { } a) box = a;
        else if (items.Count == 0) box = new(0, 0, 800, 600);
        else
        {
            var x0 = items.Min(i => i.Bounds.X); var y0 = items.Min(i => i.Bounds.Y);
            var x1 = items.Max(i => i.Bounds.X + i.Bounds.Width); var y1 = items.Max(i => i.Bounds.Y + i.Bounds.Height);
            var m = Math.Max(10, Math.Max(x1 - x0, y1 - y0) * 0.05);
            box = new(x0 - m, y0 - m, x1 - x0 + 2 * m, y1 - y0 + 2 * m);
        }

        var scale = maxSize / Math.Max(box.Width, box.Height);
        int pw = Math.Max(1, (int)Math.Round(box.Width * scale)), ph = Math.Max(1, (int)Math.Round(box.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(background == "transparent" ? SKColors.Transparent : SceneRenderer.ParseColor(background) ?? SKColors.White);
        canvas.Scale((float)scale);
        canvas.Translate((float)-box.X, (float)-box.Y);

        using var paint = new SKPaint { IsAntialias = true };
        foreach (var it in items) SceneRenderer.DrawItem(canvas, paint, it);

        if (labels)
        {
            // Etichette di dimensione costante in pixel: id e (se c'è) nome, in alto a sinistra di ogni oggetto.
            using var font = new SKFont(SKTypeface.Default, (float)(12 / scale));
            using var bg = new SKPaint { Color = new SKColor(255, 255, 255, 215), IsAntialias = true };
            using var fg = new SKPaint { Color = new SKColor(0x1F, 0x29, 0x37), IsAntialias = true };
            var pad = (float)(2 / scale);
            foreach (var it in items)
            {
                var text = names.TryGetValue(it.Id, out var n) && n != null ? $"{it.Id} {n}" : it.Id;
                var w = font.MeasureText(text);
                var (x, y) = ((float)it.Bounds.X, (float)it.Bounds.Y);
                canvas.DrawRect(SKRect.Create(x, y, w + 2 * pad, font.Size + 2 * pad), bg);
                canvas.DrawText(text, x + pad, y + font.Size, font, fg);
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new RenderedImage(data.ToArray(), pw, ph, box);
    }
}
