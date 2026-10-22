using System.Numerics;
using System.Text.Json.Serialization;

namespace Quattromani.Core;

/// <summary>A glyph: advance and outline with the origin on the baseline, at the left of the glyph.</summary>
public sealed record GlyphShape(double Advance, List<PathSeg> Outline);

/// <summary>
/// Text on a path: the glyphs follow a path, a rectangle or an ellipse (<c>PathId</c>). Offset = distance along the path
/// from the anchor point (px); Align = anchoring ("start", "center", "end"); Distance = offset from the path towards
/// the "upper" side with respect to the direction of travel (negative = from the opposite side); Reverse runs the path backwards.
/// </summary>
public sealed record TextOnPath(string PathId, double Offset, string Align, double Distance, bool Reverse);

/// <summary>Computation of the glyphs along the path. The result (outlines in the text's local space) stays in the document.</summary>
static class TextOnPathLayout
{
    /// <summary>The path in page coordinates (path, rectangle or ellipse), or null if the object cannot serve as a path.</summary>
    internal static List<PathSeg>? PageSegments(Document doc, SceneObject o)
    {
        var world = o.Transform * doc.ParentMatrix(o);
        return o switch
        {
            PathObject p => PathData.Transform(p.Segments, p.Fit * world),
            RectangleObject rect => PathData.Transform(rect.Outline(), world),
            EllipseObject => PathData.Transform(PathData.EllipseSegments(o.X, o.Y, o.Width, o.Height), world),
            _ => null,
        };
    }

    // Everything the result depends on: if it does not change, nothing is recomputed.
    static string KeyOf(Document doc, TextObject t, List<PathSeg>? pageSegs) =>
        string.Join('|', t.Text, t.Style, t.OnPath, t.Transform, doc.ParentMatrix(t), t.X, t.Y, t.Width, t.Height,
            pageSegs == null ? "-" : PathData.ToString(pageSegs, 3));

    /// <summary>Recomputes the text's glyphs if something changed. If the path is gone the text goes back to horizontal (and reattaches if the path returns).</summary>
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
            // Without a path (deleted, or too short): normal, straight text, until the path returns.
            t.PathGlyphs = null;
            t.Relayout(doc);
        }
        else
        {
            // The outlines are in page space: I bring them into the text's local space (container and transform included).
            Matrix3x2.Invert(t.Transform * doc.ParentMatrix(t), out var inv);
            var local = PathData.Transform(glyphs, inv);
            var box = PathData.Bounds(local) ?? new Box(t.X, t.Y, 1, 1);
            t.PathGlyphs = local;
            t.Source = box;
            (t.X, t.Y, t.Width, t.Height) = (box.X, box.Y, Math.Max(box.Width, 0.01), Math.Max(box.Height, 0.01));
        }
        t.PathKey = KeyOf(doc, t, pageSegs);
    }

    // ---- Geometry ----

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
        // Remove duplicate points.
        for (var i = pts.Count - 1; i > 0; i--)
            if (Vector2.DistanceSquared(pts[i], pts[i - 1]) < 1e-10f) pts.RemoveAt(i);
        return (pts, closed);
    }

    /// <summary>
    /// Text parameters after a move by <paramref name="delta"/> (in page space): the center of the text is projected onto the path
    /// before and after, the difference along the path becomes offset and the perpendicular one becomes distance. Null if the path is gone.
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

        // Position along the path and distance (positive on the "upper" side) of the closest point.
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
        if (closed) { ds = ((ds % length) + length) % length; if (ds > length / 2) ds -= length; } // on a closed loop, the shortest way
        return op with { Offset = op.Offset + ds, Distance = op.Distance + (d1 - d0) };
    }

    /// <summary>Outlines of the glyphs laid out on the path, in page coordinates; null if not possible (path too short or text without glyphs).</summary>
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

        // Point at distance s from the start (wrapping around if closed); null beyond an open path.
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
            if (centre is null) continue; // the glyph falls outside the path
            // The slope is that of the chord under the glyph: it also works well on tight curves.
            var a = At(mid - g.Advance / 2) ?? centre.Value;
            var b = At(mid + g.Advance / 2) ?? centre.Value;
            var dir = b - a;
            if (dir.LengthSquared() < 1e-10f) continue;
            dir = Vector2.Normalize(dir);
            var up = new Vector2(dir.Y, -dir.X); // "above" with respect to the direction of travel
            var origin = centre.Value + up * (float)op.Distance;
            var angle = Math.Atan2(dir.Y, dir.X);
            var m = Matrix3x2.CreateTranslation((float)(-g.Advance / 2), 0) * Matrix3x2.CreateRotation((float)angle) * Matrix3x2.CreateTranslation(origin);
            result.AddRange(PathData.Transform(g.Outline, m));
        }
        return result.Any(s => s.Op is 'L' or 'C') ? result : null;
    }
}

/// <summary>
/// Puts a text on a path: {"cmd":"text.onPath","targets":["title"],"path":"arc","align":"center","distance":4}.
/// With "detach":true the text goes back to horizontal. Omitted fields stay unchanged (or take the default value the first time).
/// </summary>
public sealed class SetTextOnPath : Command
{
    public List<string> Targets { get; set; } = [];
    /// <summary>Id or name of a path, rectangle or ellipse.</summary>
    public string? Path { get; set; }
    public double? Offset { get; set; }
    /// <summary>"start" (default), "center" or "end".</summary>
    public string? Align { get; set; }
    public double? Distance { get; set; }
    public bool? Reverse { get; set; }
    public bool Detach { get; set; }

    record Snapshot(TextObject Obj, TextOnPath? OnPath, List<PathSeg>? Glyphs, string? Key, List<TextLine> Lines, Box Source, Box Rect);

    List<Snapshot> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(s => s.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("text.onPath: 'targets' is empty. Pass the ids or names of texts, e.g. [\"o1\"].");
        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        if (objs.FirstOrDefault(o => o is not TextObject) is { } other)
            throw new CommandException($"text.onPath: '{other.Name ?? other.Id}' is a {other.Type}, not a text.");
        if (Align is not (null or "start" or "center" or "end"))
            throw new CommandException($"text.onPath: align '{Align}' is not valid. Values: \"start\", \"center\", \"end\".");
        foreach (var v in new[] { Offset, Distance })
            if (v is { } d && (!double.IsFinite(d) || Math.Abs(d) > 1e6)) throw new CommandException($"text.onPath: invalid value ({d}).");

        SceneObject? path = null;
        if (!Detach && Path != null)
        {
            path = doc.Find(Path);
            if (TextOnPathLayout.PageSegments(doc, path) is null)
                throw new CommandException($"text.onPath: '{path.Name ?? path.Id}' is a {path.Type}: the path must be a path, a rectangle or an ellipse.");
            if (objs.Contains(path)) throw new CommandException("text.onPath: a text cannot sit on its own path.");
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
                throw new CommandException($"text.onPath: give 'path' (id or name of the path) for '{t.Name ?? t.Id}', or \"detach\":true to detach it. Example: {{\"cmd\":\"text.onPath\",\"targets\":[\"titolo\"],\"path\":\"arco\"}}.");
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
