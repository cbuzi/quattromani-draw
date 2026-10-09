using AiDraw.Core;
using SkiaSharp;

namespace AiDraw.App;

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
        using var font = CreateFont(s);
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
                if (current.Length == 0 || font.MeasureText(candidate) <= limit) current = candidate;
                else { lines.Add(current); current = word; }
            }
            lines.Add(current);
        }

        var widths = lines.Select(l => (double)font.MeasureText(l)).ToList();
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
        using var font = CreateFont(s);
        var segs = new List<PathSeg>();
        foreach (var line in lines)
        {
            if (line.Text.Length == 0) continue;
            using var path = font.GetTextPath(line.Text, new SKPoint((float)line.X, (float)line.Y));
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
