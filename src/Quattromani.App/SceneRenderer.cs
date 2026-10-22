using System.Numerics;
using Quattromani.Core;
using Quattromani.Mcp;
using SkiaSharp;

namespace Quattromani.App;

enum Kind { Rectangle, Ellipse, Path, Group, Text, Image }

sealed record TextInfo(List<TextLine> Lines, TextStyle Style);

// Immutable copy of the objects: rendering runs on another thread.
sealed record Item(string Id, Kind Kind, double X, double Y, double W, double H, Matrix3x2 M,
    string? Fill, string? Stroke, double StrokeWidth, Box Source, List<PathSeg>? Segments, List<Item>? Children,
    string? NodeKinds = null, TextInfo? Text = null, byte[]? Image = null, double Opacity = 1, double[]? Dash = null, string? Cap = null, string? Join = null, double Radius = 0)
{
    public Box Bounds => SceneObject.BoundsOf(X, Y, W, H, M);
    public Matrix3x2 Fit => SceneObject.FitMatrix(Source, new(X, Y, W, H));

    public static Item From(SceneObject o) => Make(o) with { Opacity = o.Opacity, Dash = o.StrokeDash, Cap = o.StrokeCap, Join = o.StrokeJoin, Radius = (o as RectangleObject)?.EffectiveRadius ?? 0 };

    static Item Make(SceneObject o) => o switch
    {
        PathObject p => new(o.Id, Kind.Path, o.X, o.Y, o.Width, o.Height, o.Transform, o.Fill, o.Stroke, o.StrokeWidth,
            p.Source, p.Segments, null, p.NodeKinds),
        // Text on a path: the glyphs are already outlines, it is drawn as a path (with all its fills).
        TextObject { PathGlyphs: { } glyphs } t => new(o.Id, Kind.Path, o.X, o.Y, o.Width, o.Height, o.Transform, o.Fill, o.Stroke, o.StrokeWidth,
            t.Source, glyphs, null),
        TextObject t => new(o.Id, Kind.Text, o.X, o.Y, o.Width, o.Height, o.Transform, o.Fill, o.Stroke, o.StrokeWidth,
            t.Source, null, null, Text: new TextInfo(t.Lines, t.Style)),
        GroupObject g => new(o.Id, Kind.Group, o.X, o.Y, o.Width, o.Height, o.Transform, o.Fill, o.Stroke, o.StrokeWidth,
            g.Source, null, g.Children.Where(c => !c.Hidden).Select(From).ToList()),
        ImageObject im => new(o.Id, Kind.Image, o.X, o.Y, o.Width, o.Height, o.Transform, o.Fill, o.Stroke, o.StrokeWidth,
            default, null, null, Image: im.Data),
        _ => new(o.Id, o is EllipseObject ? Kind.Ellipse : Kind.Rectangle, o.X, o.Y, o.Width, o.Height, o.Transform,
            o.Fill, o.Stroke, o.StrokeWidth, default, null, null),
    };
}

/// <summary>Drawing of the objects on a Skia canvas: used by both the window and the MCP render tool.</summary>
static class SceneRenderer
{
    static SKMatrix ToSk(Matrix3x2 m) => new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);

    // Skia path from the segments, already stretched with `fit` (so the stroke stays uniform).
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

    /// <param name="vectorPatterns">For the PDF: patterns are drawn as vector geometry (the image shader would be rasterized at low resolution).</param>
    public static void DrawItem(SKCanvas canvas, SKPaint paint, Item it, bool vectorPatterns = false)
    {
        // Opacity applies to the object as a whole (fill and stroke together; a group's content together): it is drawn on a layer.
        using var layerPaint = it.Opacity < 1 ? new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Round(Math.Clamp(it.Opacity, 0, 1) * 255)) } : null;
        if (layerPaint != null) canvas.SaveLayer(layerPaint);
        canvas.Save();
        var m = ToSk(it.M);
        canvas.Concat(in m);
        if (it.Kind == Kind.Group)
        {
            var fit = ToSk(it.Fit);
            canvas.Concat(in fit);
            foreach (var child in it.Children!) DrawItem(canvas, paint, child, vectorPatterns);
        }
        else
        {
            // The gradient refers to the object's box (for text, that of the laid-out text).
            var box = it.Kind == Kind.Text ? it.Source : new Box(it.X, it.Y, it.W, it.H);
            if (vectorPatterns && it.Kind != Kind.Text && it.Fill != null && Pattern.TryParse(it.Fill, out var pat) && DrawVectorPattern(canvas, paint, it, pat, box))
            {
                // drawn as geometry
            }
            else if (Prepare(paint, it.Fill, box))
            {
                paint.Style = SKPaintStyle.Fill;
                DrawShape(canvas, paint, it);
            }
            // The background (fill) sits under the image, the frame (stroke) above.
            if (it.Kind == Kind.Image && Decode(it.Image!) is { } img)
            {
                using var imgPaint = new SKPaint { IsAntialias = true };
                canvas.DrawImage(img, SKRect.Create((float)it.X, (float)it.Y, (float)it.W, (float)it.H), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), imgPaint);
            }
            if (it.StrokeWidth > 0 && Prepare(paint, it.Stroke, box))
            {
                paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = (float)it.StrokeWidth;
                paint.StrokeCap = it.Cap switch { "round" => SKStrokeCap.Round, "square" => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
                paint.StrokeJoin = it.Join switch { "round" => SKStrokeJoin.Round, "bevel" => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
                if (it.Dash is { Length: > 0 } dash)
                    paint.PathEffect = SKPathEffect.CreateDash((dash.Length % 2 == 0 ? dash : [.. dash, .. dash]).Select(v => (float)v).ToArray(), 0); // an odd list repeats, like SVG
                DrawShape(canvas, paint, it);
                // the paint is shared by all the items: back to the defaults
                paint.PathEffect?.Dispose();
                paint.PathEffect = null;
                paint.StrokeCap = SKStrokeCap.Butt;
                paint.StrokeJoin = SKStrokeJoin.Miter;
            }
            paint.Shader?.Dispose();
            paint.Shader = null;
        }
        canvas.Restore();
        if (layerPaint != null) canvas.Restore();
    }

    /// <summary>Sets color or gradient on the paint; false if there is nothing to draw.</summary>
    static bool Prepare(SKPaint paint, string? spec, Box box)
    {
        paint.Shader?.Dispose();
        paint.Shader = null;
        if (spec == null) return false;
        if (Pattern.IsPattern(spec))
        {
            if (!Pattern.TryParse(spec, out var pat)) return false;
            paint.Shader = MakePatternShader(pat, box);
            paint.Color = SKColors.White;
            return paint.Shader != null;
        }
        if (Gradient.IsGradient(spec))
        {
            if (!Gradient.TryParse(spec, out var g) || MakeShader(g, box) is not { } shader) return false;
            paint.Shader = shader;
            paint.Color = SKColors.White; // the paint's color modulates the shader: white = unchanged
            return true;
        }
        if (ParseColor(spec) is not { } c) return false;
        paint.Color = c;
        return true;
    }

    static SKShader? MakeShader(GradientSpec g, Box b)
    {
        var colors = g.Stops.Select(s => ParseColor(s.Color) ?? SKColors.Transparent).ToArray();
        var pos = g.Stops.Select(s => (float)s.Position).ToArray();
        if (!g.Radial)
        {
            var (x1, y1, x2, y2) = Gradient.LinearPoints(g, b);
            SKPoint p1 = new((float)(b.X + x1 * b.Width), (float)(b.Y + y1 * b.Height)), p2 = new((float)(b.X + x2 * b.Width), (float)(b.Y + y2 * b.Height));
            if (Math.Abs(p1.X - p2.X) + Math.Abs(p1.Y - p2.Y) < 1e-6) return null;
            return SKShader.CreateLinearGradient(p1, p2, colors, pos, SKShaderTileMode.Clamp);
        }
        var (fx, fy) = Gradient.RadialRadii(g, b);
        double cx = b.X + g.CenterX * b.Width, cy = b.Y + g.CenterY * b.Height, rx = fx * b.Width, ry = fy * b.Height;
        if (rx < 1e-6 || ry < 1e-6) return null;
        // The ellipse is a circle of radius rx stretched vertically around the center.
        var m = SKMatrix.CreateScale(1, (float)(ry / rx), (float)cx, (float)cy);
        return SKShader.CreateRadialGradient(new SKPoint((float)cx, (float)cy), (float)rx, colors, pos, SKShaderTileMode.Clamp, m);
    }

    // Pattern: a unit drawn once as a vector image and repeated; it starts at the object's top-left corner.
    static SKShader? MakePatternShader(PatternSpec p, Box b)
    {
        var size = (float)p.Size;
        var tile = p.Kind == "checker" ? 2 * size : size; // checkers repeat every two squares
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, tile, tile));
        using var paint = new SKPaint { IsAntialias = true, Color = ParseColor(p.Bg) ?? SKColors.Transparent };
        canvas.DrawRect(0, 0, tile, tile, paint);
        paint.Color = ParseColor(p.Fg) ?? SKColors.Black;
        switch (p.Kind)
        {
            case "stripes": canvas.DrawRect(0, 0, size / 2, tile, paint); break;
            case "dots": canvas.DrawCircle(size / 2, size / 2, size * 0.3f, paint); break;
            case "checker": canvas.DrawRect(0, 0, size, size, paint); canvas.DrawRect(size, size, size, size, paint); break;
            case "grid":
                var t = Math.Max(size * 0.12f, 0.5f);
                canvas.DrawRect(0, 0, tile, t, paint);
                canvas.DrawRect(0, 0, t, tile, paint);
                break;
        }
        using var picture = recorder.EndRecording();
        var local = SKMatrix.Concat(SKMatrix.CreateTranslation((float)b.X, (float)b.Y), SKMatrix.CreateRotationDegrees((float)p.Angle));
        return picture.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKFilterMode.Linear, local, new SKRect(0, 0, tile, tile));
    }

    // Pattern as geometry: background, then the pattern elements clipped to the shape. False if there would be too many.
    static bool DrawVectorPattern(SKCanvas canvas, SKPaint paint, Item it, PatternSpec p, Box b)
    {
        // Area to cover in the pattern's space (origin at the top-left corner of the box, rotated by the angle).
        var rad = -p.Angle * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        var us = new List<double>(); var vs = new List<double>();
        foreach (var (cx, cy) in new[] { (0.0, 0.0), (b.Width, 0), (0, b.Height), (b.Width, b.Height) })
        {
            us.Add(cx * cos - cy * sin);
            vs.Add(cx * sin + cy * cos);
        }
        double minU = us.Min(), maxU = us.Max(), minV = vs.Min(), maxV = vs.Max(), s = p.Size;
        var cells = Math.Ceiling((maxU - minU) / s) * Math.Ceiling((maxV - minV) / s);
        if (cells > 40000) return false;

        using var clip = new SKPath();
        var r = SKRect.Create((float)it.X, (float)it.Y, (float)it.W, (float)it.H);
        switch (it.Kind)
        {
            case Kind.Ellipse: clip.AddOval(r); break;
            case Kind.Path: clip.AddPath(BuildPath(it.Segments!, it.Fit)); break;
            default:
                if (it.Radius > 0) clip.AddRoundRect(r, (float)it.Radius, (float)it.Radius);
                else clip.AddRect(r);
                break;
        }

        paint.Shader?.Dispose();
        paint.Shader = null;
        paint.Style = SKPaintStyle.Fill;
        paint.Color = ParseColor(p.Bg) ?? SKColors.Transparent;
        canvas.DrawPath(clip, paint);

        canvas.Save();
        canvas.ClipPath(clip, antialias: true);
        canvas.Translate((float)b.X, (float)b.Y);
        canvas.RotateDegrees((float)p.Angle);
        paint.Color = ParseColor(p.Fg) ?? SKColors.Black;
        int i0 = (int)Math.Floor(minU / s), i1 = (int)Math.Ceiling(maxU / s), j0 = (int)Math.Floor(minV / s), j1 = (int)Math.Ceiling(maxV / s);
        var size = (float)s;
        switch (p.Kind)
        {
            case "stripes":
                for (var i = i0; i < i1; i++) canvas.DrawRect(i * size, (float)minV, size / 2, (float)(maxV - minV), paint);
                break;
            case "dots":
                for (var i = i0; i < i1; i++)
                    for (var j = j0; j < j1; j++) canvas.DrawCircle((i + 0.5f) * size, (j + 0.5f) * size, size * 0.3f, paint);
                break;
            case "checker":
                for (var i = i0; i < i1; i++)
                    for (var j = j0; j < j1; j++)
                        if (((i + j) & 1) == 0) canvas.DrawRect(i * size, j * size, size, size, paint);
                break;
            case "grid":
                var t = Math.Max(size * 0.12f, 0.5f);
                for (var i = i0; i < i1; i++) canvas.DrawRect(i * size, (float)minV, t, (float)(maxV - minV), paint);
                for (var j = j0; j < j1; j++) canvas.DrawRect((float)minU, j * size, (float)(maxU - minU), t, paint);
                break;
        }
        canvas.Restore();
        return true;
    }

    static void DrawShape(SKCanvas canvas, SKPaint paint, Item it)
    {
        if (it.Kind == Kind.Text)
        {
            // Lines laid out in Source's space, stretched onto the current box.
            var fit = ToSk(it.Fit);
            canvas.Save();
            canvas.Concat(in fit);
            using var fonts = new GlyphFonts(it.Text!.Style);
            foreach (var line in it.Text.Lines)
                fonts.Draw(canvas, line.Text, (float)(it.Source.X + line.X), (float)(it.Source.Y + line.Y), paint);
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
        if (it.Kind == Kind.Ellipse) canvas.DrawOval(r, paint);
        else if (it.Radius > 0) canvas.DrawRoundRect(r, (float)it.Radius, (float)it.Radius, paint);
        else canvas.DrawRect(r, paint);
    }

    // Item.From runs on every frame: the image is decoded only once for each byte array.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], SKImage?> Decoded = new();

    public static SKImage? Decode(byte[] data)
    {
        lock (Decoded)
        {
            if (!Decoded.TryGetValue(data, out var img)) Decoded.Add(data, img = SKImage.FromEncodedData(data));
            return img;
        }
    }

    // Non-premultiplied RGBA pixels, downscaled (if needed) to maxSide on the longest side.
    public static (int Width, int Height, byte[] Rgba)? Pixels(byte[] data, int maxSide)
    {
        using var src = SKBitmap.Decode(data);
        if (src is null) return null;
        var k = Math.Min(1.0, (double)maxSide / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)Math.Round(src.Width * k)), h = Math.Max(1, (int)Math.Round(src.Height * k));
        using var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        return src.ScalePixels(dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) ? (w, h, dst.GetPixelSpan().ToArray()) : null;
    }

    public static (int Width, int Height)? Probe(byte[] data)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data));
        return codec is null ? null : (codec.Info.Width, codec.Info.Height);
    }

    // Document format: "#RRGGBB" or "#RRGGBBAA" (different from Skia's ARGB).
    public static SKColor? ParseColor(string? s)
    {
        if (s is not { Length: 7 or 9 } || s[0] != '#' || !s[1..].All(Uri.IsHexDigit)) return null;
        var v = Convert.ToUInt32(s[1..], 16);
        return s.Length == 7
            ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new SKColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

/// <summary>The PNG the LLM sees: same drawing as the window, on an off-screen surface.</summary>
sealed class SkiaDocumentRenderer(CommandBus bus) : IDocumentRenderer
{
    public RenderedImage Render(Box? area, int maxSize, string? background, bool labels, bool showPage)
    {
        var (items, names, pageSize) = bus.Read(d => (d.RenderObjects.Select(Item.From).ToList(), d.Objects.ToDictionary(o => o.Id, o => o.Name), (d.Pages[0].Width, d.Pages[0].Height)));
        var page = new Box(0, 0, pageSize.Width, pageSize.Height);

        // Without an area: the page and all the content (even if it goes beyond the page), with a small margin.
        Box box;
        if (area is { } a) box = a;
        else
        {
            double x0 = page.X, y0 = page.Y, x1 = page.Width, y1 = page.Height;
            if (items.Count > 0)
            {
                x0 = Math.Min(x0, items.Min(i => i.Bounds.X)); y0 = Math.Min(y0, items.Min(i => i.Bounds.Y));
                x1 = Math.Max(x1, items.Max(i => i.Bounds.X + i.Bounds.Width)); y1 = Math.Max(y1, items.Max(i => i.Bounds.Y + i.Bounds.Height));
            }
            var m = Math.Max(10, Math.Max(x1 - x0, y1 - y0) * 0.03);
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
        if (showPage)
        {
            // The page outline, so whoever looks at the image knows where the page ends.
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)Math.Max(1, 1.2 / scale);
            paint.Color = new SKColor(0x94, 0xA3, 0xB8);
            canvas.DrawRect(SKRect.Create(0, 0, (float)page.Width, (float)page.Height), paint);
        }
        foreach (var it in items) SceneRenderer.DrawItem(canvas, paint, it);

        if (labels)
        {
            // Labels of constant size in pixels: id and (if any) name, at the top-left of each object.
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
