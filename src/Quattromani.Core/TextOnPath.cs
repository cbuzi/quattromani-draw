using System.Numerics;
using System.Text.Json.Serialization;

namespace Quattromani.Core;

/// <summary>Un glifo: avanzamento e contorno con l'origine sulla linea di base, a sinistra del glifo.</summary>
public sealed record GlyphShape(double Advance, List<PathSeg> Outline);

/// <summary>
/// Testo su tracciato: i glifi seguono un path, un rettangolo o un'ellisse (<c>PathId</c>). Offset = distanza lungo il tracciato
/// dal punto d'ancoraggio (px); Align = ancoraggio ("start", "center", "end"); Distance = scostamento dal tracciato verso
/// il lato "alto" rispetto alla direzione di percorrenza (negativo = dal lato opposto); Reverse percorre il tracciato al contrario.
/// </summary>
public sealed record TextOnPath(string PathId, double Offset, string Align, double Distance, bool Reverse);

/// <summary>Calcolo dei glifi lungo il tracciato. Il risultato (contorni nello spazio locale del testo) resta nel documento.</summary>
static class TextOnPathLayout
{
    /// <summary>Il tracciato in coordinate pagina (path, rettangolo o ellisse), oppure null se l'oggetto non può fare da tracciato.</summary>
    internal static List<PathSeg>? PageSegments(Document doc, SceneObject o)
    {
        var world = o.Transform * doc.ParentMatrix(o);
        return o switch
        {
            PathObject p => PathData.Transform(p.Segments, p.Fit * world),
            RectangleObject => PathData.Transform(PathData.RectangleSegments(o.X, o.Y, o.Width, o.Height), world),
            EllipseObject => PathData.Transform(PathData.EllipseSegments(o.X, o.Y, o.Width, o.Height), world),
            _ => null,
        };
    }

    // Tutto ciò da cui dipende il risultato: se non cambia, non si ricalcola.
    static string KeyOf(Document doc, TextObject t, List<PathSeg>? pageSegs) =>
        string.Join('|', t.Text, t.Style, t.OnPath, t.Transform, doc.ParentMatrix(t), t.X, t.Y, t.Width, t.Height,
            pageSegs == null ? "-" : PathData.ToString(pageSegs, 3));

    /// <summary>Ricalcola i glifi del testo se qualcosa è cambiato. Se il tracciato non c'è più il testo torna orizzontale (e si riaggancia se il tracciato torna).</summary>
    internal static void Update(Document doc, TextObject t)
    {
        var op = t.OnPath!;
        var target = doc.AllObjects.FirstOrDefault(o => o.Id == op.PathId && o != t);
        var pageSegs = target == null ? null : PageSegments(doc, target);
        var key = KeyOf(doc, t, pageSegs);
        if (key == t.PathKey) return;
        var engine = doc.TextEngine;
        if (engine == null) return;

        var glyphs = pageSegs == null ? null : Place(engine, t, op, pageSegs);
        if (glyphs == null)
        {
            // Senza tracciato (cancellato, o troppo corto): testo normale, dritto, finché il tracciato non torna.
            t.PathGlyphs = null;
            t.Relayout(doc);
        }
        else
        {
            // I contorni sono in pagina: li porto nello spazio locale del testo (contenitore e trasformazione inclusi).
            Matrix3x2.Invert(t.Transform * doc.ParentMatrix(t), out var inv);
            var local = PathData.Transform(glyphs, inv);
            var box = PathData.Bounds(local) ?? new Box(t.X, t.Y, 1, 1);
            t.PathGlyphs = local;
            t.Source = box;
            (t.X, t.Y, t.Width, t.Height) = (box.X, box.Y, Math.Max(box.Width, 0.01), Math.Max(box.Height, 0.01));
        }
        t.PathKey = KeyOf(doc, t, pageSegs);
    }

    // ---- Geometria ----

    static (List<Vector2> Pts, bool Closed) Flatten(List<PathSeg> segs)
    {
        var pts = new List<Vector2>();
        var closed = false;
        Vector2 cur = default;
        foreach (var s in segs)
        {
            if (s.Op == 'M') { if (pts.Count > 0) break; cur = new((float)s.X, (float)s.Y); pts.Add(cur); }
            else if (s.Op == 'L') { cur = new((float)s.X, (float)s.Y); pts.Add(cur); }
            else if (s.Op == 'C')
            {
                Vector2 c1 = new((float)s.X1, (float)s.Y1), c2 = new((float)s.X2, (float)s.Y2), e = new((float)s.X, (float)s.Y);
                var n = (int)Math.Clamp(Math.Ceiling((Vector2.Distance(cur, c1) + Vector2.Distance(c1, c2) + Vector2.Distance(c2, e)) / 2), 8, 300);
                for (var i = 1; i <= n; i++)
                {
                    float u = (float)i / n, v = 1 - u;
                    pts.Add(v * v * v * cur + 3 * v * v * u * c1 + 3 * v * u * u * c2 + u * u * u * e);
                }
                cur = e;
            }
            else if (s.Op == 'Z')
            {
                closed = true;
                if (pts.Count > 0 && Vector2.DistanceSquared(pts[0], cur) > 1e-8f) pts.Add(pts[0]);
                break;
            }
        }
        // Via i punti doppi.
        for (var i = pts.Count - 1; i > 0; i--)
            if (Vector2.DistanceSquared(pts[i], pts[i - 1]) < 1e-10f) pts.RemoveAt(i);
        return (pts, closed);
    }

    /// <summary>
    /// Parametri del testo dopo uno spostamento di <paramref name="delta"/> (in pagina): il centro del testo viene proiettato sul tracciato
    /// prima e dopo, la differenza lungo il tracciato diventa offset e quella perpendicolare diventa distanza. Null se il tracciato non c'è.
    /// </summary>
    internal static TextOnPath? Slide(Document doc, TextObject t, Vector2 delta)
    {
        var op = t.OnPath!;
        var target = doc.AllObjects.FirstOrDefault(o => o.Id == op.PathId && o != t);
        if (target == null || PageSegments(doc, target) is not { } segs) return null;
        var (pts, closed) = Flatten(segs);
        if (op.Reverse) pts.Reverse();
        if (pts.Count < 2) return null;
        var cum = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + Vector2.Distance(pts[i - 1], pts[i]);
        var length = cum[^1];
        if (length < 1e-3) return null;

        // Posizione lungo il tracciato e distanza (positiva dal lato "alto") del punto più vicino.
        (double S, double D) Nearest(Vector2 p)
        {
            double best = double.MaxValue, bs = 0, bd = 0;
            for (var i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1]; var ab = pts[i] - a;
                var len2 = ab.LengthSquared();
                if (len2 < 1e-12f) continue;
                var u = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
                var q = a + ab * u;
                var dist = Vector2.Distance(p, q);
                if (dist >= best) continue;
                var dir = Vector2.Normalize(ab);
                best = dist;
                bs = cum[i - 1] + u * Math.Sqrt(len2);
                bd = Vector2.Dot(p - q, new Vector2(dir.Y, -dir.X));
            }
            return (bs, bd);
        }

        var b = doc.PageBounds(t);
        var c = new Vector2((float)(b.X + b.Width / 2), (float)(b.Y + b.Height / 2));
        var (s0, d0) = Nearest(c);
        var (s1, d1) = Nearest(c + delta);
        var ds = s1 - s0;
        if (closed) { ds = ((ds % length) + length) % length; if (ds > length / 2) ds -= length; } // sul giro il percorso più corto
        return op with { Offset = op.Offset + ds, Distance = op.Distance + (d1 - d0) };
    }

    /// <summary>Contorni dei glifi disposti sul tracciato, in coordinate pagina; null se non si può (tracciato troppo corto o testo senza glifi).</summary>
    static List<PathSeg>? Place(ITextEngine engine, TextObject t, TextOnPath op, List<PathSeg> pageSegs)
    {
        var (pts, closed) = Flatten(pageSegs);
        if (op.Reverse) pts.Reverse();
        if (pts.Count < 2) return null;
        var cum = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + Vector2.Distance(pts[i - 1], pts[i]);
        var length = cum[^1];
        if (length < 1e-3) return null;

        var glyphs = engine.Glyphs(t.Style, t.Text.Replace("\r", "").Replace('\n', ' '));
        var total = glyphs.Sum(g => g.Advance);
        if (glyphs.Count == 0) return null;
        var start = op.Align switch { "center" => length / 2 - total / 2, "end" => length - total, _ => 0 } + op.Offset;

        // Punto alla distanza s dall'inizio (con giro completo se chiuso); null fuori dal tracciato aperto.
        Vector2? At(double s)
        {
            if (closed) s = ((s % length) + length) % length;
            else if (s < 0 || s > length) return null;
            var i = Array.BinarySearch(cum, s);
            if (i < 0) i = ~i;
            i = Math.Clamp(i, 1, pts.Count - 1);
            var f = (s - cum[i - 1]) / Math.Max(cum[i] - cum[i - 1], 1e-9);
            return Vector2.Lerp(pts[i - 1], pts[i], (float)f);
        }

        var result = new List<PathSeg>();
        var pos = start;
        foreach (var g in glyphs)
        {
            var mid = pos + g.Advance / 2;
            pos += g.Advance;
            if (g.Outline.Count == 0) continue;
            var centre = At(mid);
            if (centre is null) continue; // il glifo cade fuori dal tracciato
            // L'inclinazione è quella della corda sotto il glifo: sta bene anche sulle curve strette.
            var a = At(mid - g.Advance / 2) ?? centre.Value;
            var b = At(mid + g.Advance / 2) ?? centre.Value;
            var dir = b - a;
            if (dir.LengthSquared() < 1e-10f) continue;
            dir = Vector2.Normalize(dir);
            var up = new Vector2(dir.Y, -dir.X); // "sopra" rispetto alla direzione di percorrenza
            var origin = centre.Value + up * (float)op.Distance;
            var angle = Math.Atan2(dir.Y, dir.X);
            var m = Matrix3x2.CreateTranslation((float)(-g.Advance / 2), 0) * Matrix3x2.CreateRotation((float)angle) * Matrix3x2.CreateTranslation(origin);
            result.AddRange(PathData.Transform(g.Outline, m));
        }
        return result.Any(s => s.Op is 'L' or 'C') ? result : null;
    }
}

/// <summary>
/// Mette un testo su un tracciato: {"cmd":"text.onPath","targets":["titolo"],"path":"arco","align":"center","distance":4}.
/// Con "detach":true il testo torna orizzontale. I campi omessi restano invariati (o prendono il valore predefinito la prima volta).
/// </summary>
public sealed class SetTextOnPath : Command
{
    public List<string> Targets { get; set; } = [];
    /// <summary>Id o nome di un path, rettangolo o ellisse.</summary>
    public string? Path { get; set; }
    public double? Offset { get; set; }
    /// <summary>"start" (predefinito), "center" o "end".</summary>
    public string? Align { get; set; }
    public double? Distance { get; set; }
    public bool? Reverse { get; set; }
    public bool Detach { get; set; }

    record Snapshot(TextObject Obj, TextOnPath? OnPath, List<PathSeg>? Glyphs, string? Key, List<TextLine> Lines, Box Source, Box Rect);

    List<Snapshot> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(s => s.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("text.onPath: 'targets' e' vuoto. Passa id o nomi di testi, es. [\"o1\"].");
        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        if (objs.FirstOrDefault(o => o is not TextObject) is { } other)
            throw new CommandException($"text.onPath: '{other.Name ?? other.Id}' e' un {other.Type}, non un testo.");
        if (Align is not (null or "start" or "center" or "end"))
            throw new CommandException($"text.onPath: align '{Align}' non valido. Valori: \"start\", \"center\", \"end\".");
        foreach (var v in new[] { Offset, Distance })
            if (v is { } d && (!double.IsFinite(d) || Math.Abs(d) > 1e6)) throw new CommandException($"text.onPath: valore non valido ({d}).");

        SceneObject? path = null;
        if (!Detach && Path != null)
        {
            path = doc.Find(Path);
            if (TextOnPathLayout.PageSegments(doc, path) is null)
                throw new CommandException($"text.onPath: '{path.Name ?? path.Id}' e' un {path.Type}: il tracciato deve essere un path, un rettangolo o un'ellisse.");
            if (objs.Contains(path)) throw new CommandException("text.onPath: un testo non puo' stare sul proprio tracciato.");
        }

        _old = objs.Cast<TextObject>().Select(t => new Snapshot(t, t.OnPath, t.PathGlyphs, t.PathKey, t.Lines, t.Source, new Box(t.X, t.Y, t.Width, t.Height))).ToList();
        foreach (var s in _old)
        {
            var t = s.Obj;
            if (Detach)
            {
                t.OnPath = null; t.PathGlyphs = null; t.PathKey = null;
                t.Relayout(doc);
                continue;
            }
            var cur = t.OnPath;
            if (path == null && cur == null)
                throw new CommandException($"text.onPath: indica 'path' (id o nome del tracciato) per '{t.Name ?? t.Id}', oppure \"detach\":true per staccarlo. Esempio: {{\"cmd\":\"text.onPath\",\"targets\":[\"titolo\"],\"path\":\"arco\"}}.");
            t.OnPath = new TextOnPath(path?.Id ?? cur!.PathId, Offset ?? cur?.Offset ?? 0, Align ?? cur?.Align ?? "start",
                Distance ?? cur?.Distance ?? 0, Reverse ?? cur?.Reverse ?? false);
            t.PathKey = null;
            TextOnPathLayout.Update(doc, t);
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var s in _old)
        {
            var t = s.Obj;
            (t.OnPath, t.PathGlyphs, t.PathKey, t.Lines, t.Source) = (s.OnPath, s.Glyphs, s.Key, s.Lines, s.Source);
            (t.X, t.Y, t.Width, t.Height) = (s.Rect.X, s.Rect.Y, s.Rect.Width, s.Rect.Height);
        }
    }
}
