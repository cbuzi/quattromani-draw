using AiDraw.Core;
using SkiaSharp;

namespace AiDraw.App;

/// <summary>
/// Il font di uno stile più i font di riserva per i caratteri che il font scelto non ha (frecce, simboli, emoji...):
/// il testo si divide in "run" e ognuno si misura e si disegna col font che ha i suoi glifi.
/// </summary>
sealed class GlyphFonts(TextStyle style) : IDisposable
{
    readonly SKFont _primary = SkiaTextEngine.CreateFont(style);
    readonly Dictionary<int, SKFont?> _fallback = [];
    readonly SKFontStyle _fontStyle = new(style.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
        style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    public SKFont Primary => _primary;

    SKFont FontFor(int codepoint)
    {
        if (_primary.Typeface.ContainsGlyph(codepoint)) return _primary;
        if (!_fallback.TryGetValue(codepoint, out var f))
        {
            var tf = SKFontManager.Default.MatchCharacter(style.Family, _fontStyle, null, codepoint);
            f = tf == null ? null : new SKFont(tf, _primary.Size) { Subpixel = true, LinearMetrics = true, Hinting = SKFontHinting.None };
            _fallback[codepoint] = f;
        }
        return f ?? _primary;
    }

    public List<(string Text, SKFont Font)> Runs(string text)
    {
        var runs = new List<(string, SKFont)>();
        var sb = new System.Text.StringBuilder();
        SKFont? current = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var font = char.IsWhiteSpace((char)Math.Min(rune.Value, 0xFFFF)) && current != null ? current : FontFor(rune.Value);
            if (current != null && !ReferenceEquals(font, current)) { runs.Add((sb.ToString(), current)); sb.Clear(); }
            current = font;
            sb.Append(rune.ToString());
        }
        if (current != null) runs.Add((sb.ToString(), current));
        return runs;
    }

    public double Measure(string text) => Runs(text).Sum(r => (double)r.Font.MeasureText(r.Text));

    public void Draw(SKCanvas canvas, string text, float x, float y, SKPaint paint)
    {
        foreach (var (t, font) in Runs(text))
        {
            canvas.DrawText(t, x, y, font, paint);
            x += font.MeasureText(t);
        }
    }

    public SKPath Outline(string text, float x, float y)
    {
        var path = new SKPath();
        foreach (var (t, font) in Runs(text))
        {
            using var p = font.GetTextPath(t, new SKPoint(x, y));
            path.AddPath(p);
            x += font.MeasureText(t);
        }
        return path;
    }

    public void Dispose()
    {
        _primary.Dispose();
        foreach (var f in _fallback.Values) f?.Dispose();
    }
}

/// <summary>Misura, impaginazione e contorni del testo con i font di sistema, via Skia.</summary>
sealed class SkiaTextEngine : ITextEngine
{
    readonly Lazy<List<string>> _families = new(() =>
        SKFontManager.Default.GetFontFamilies().Where(f => !string.IsNullOrWhiteSpace(f)).Distinct()
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList());

    public IReadOnlyList<string> Families => _families.Value;

    public string DefaultFamily
    {
        get
        {
            string[] preferred = ["Inter", "Helvetica Neue", "Segoe UI", "Arial", "Helvetica", "DejaVu Sans", "Noto Sans"];
            return preferred.FirstOrDefault(p => Families.Contains(p, StringComparer.OrdinalIgnoreCase))
                   ?? Families.FirstOrDefault() ?? "Sans";
        }
    }

    public static SKFont CreateFont(TextStyle s)
    {
        var style = new SKFontStyle(s.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            s.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var typeface = SKTypeface.FromFamilyName(s.Family, style) ?? SKTypeface.Default;
        // Senza hinting né arrotondamenti: il testo vive in unità documento e viene scalato dallo zoom.
        return new SKFont(typeface, (float)s.Size) { Subpixel = true, LinearMetrics = true, Hinting = SKFontHinting.None };
    }

    public TextLayout Layout(TextStyle s, string text, double? wrapWidth)
    {
        using var fonts = new GlyphFonts(s);
        var font = fonts.Primary;
        font.GetFontMetrics(out var m);
        double asc = -m.Ascent, desc = m.Descent, advance = (asc + desc + m.Leading) * s.LineHeight;

        // Righe: ogni '\n' è un a capo; con wrapWidth si va a capo anche tra le parole.
        var lines = new List<string>();
        foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (wrapWidth is not { } limit) { lines.Add(para); continue; }
            var current = "";
            foreach (var word in para.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length == 0 || fonts.Measure(candidate) <= limit) current = candidate;
                else { lines.Add(current); current = word; }
            }
            lines.Add(current);
        }

        var widths = lines.Select(fonts.Measure).ToList();
        var width = wrapWidth ?? widths.Max();
        var result = lines.Select((l, i) =>
        {
            var x = s.Align switch { "center" => (width - widths[i]) / 2, "right" => width - widths[i], _ => 0 };
            return new TextLine(l, x, asc + i * advance);
        }).ToList();
        return new TextLayout(result, width, (lines.Count - 1) * advance + asc + desc);
    }

    public List<PathSeg> Outline(TextStyle s, IReadOnlyList<TextLine> lines)
    {
        using var fonts = new GlyphFonts(s);
        var segs = new List<PathSeg>();
        foreach (var line in lines)
        {
            if (line.Text.Length == 0) continue;
            using var path = fonts.Outline(line.Text, (float)line.X, (float)line.Y);
            using var it = path.CreateRawIterator();
            var p = new SKPoint[4];
            SKPoint cur = default;
            SKPathVerb verb;
            while ((verb = it.Next(p)) != SKPathVerb.Done)
                switch (verb)
                {
                    case SKPathVerb.Move: segs.Add(new('M', X: p[0].X, Y: p[0].Y)); cur = p[0]; break;
                    case SKPathVerb.Line: segs.Add(new('L', X: p[1].X, Y: p[1].Y)); cur = p[1]; break;
                    case SKPathVerb.Quad: // quadratica -> cubica
                    case SKPathVerb.Conic: // (peso ignorato: nei glifi è quasi sempre 1)
                        segs.Add(new('C', cur.X + 2f / 3 * (p[1].X - cur.X), cur.Y + 2f / 3 * (p[1].Y - cur.Y),
                            p[2].X + 2f / 3 * (p[1].X - p[2].X), p[2].Y + 2f / 3 * (p[1].Y - p[2].Y), p[2].X, p[2].Y));
                        cur = p[2];
                        break;
                    case SKPathVerb.Cubic: segs.Add(new('C', p[1].X, p[1].Y, p[2].X, p[2].Y, p[3].X, p[3].Y)); cur = p[3]; break;
                    case SKPathVerb.Close: segs.Add(new('Z')); break;
                }
        }
        return segs;
    }
}
