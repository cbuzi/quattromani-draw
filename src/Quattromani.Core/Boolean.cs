using System.Text.Json.Serialization;

namespace Quattromani.Core;

/// <summary>
/// Boolean operations on polygonal regions (flattened curves), without dependencies.
/// Coordinates on an integer grid of 1/1024: geometric predicates are exact, so coincident edges,
/// tangencies and intersections at the same points produce no rounding errors.
/// Algorithm: (1) all segments are split at the intersections; (2) each piece is classified
/// inside/outside on both sides with a ray against the original outlines (nonzero rule, like rendering);
/// (3) the boundary pieces are chained into rings with the interior on the left.
/// </summary>
internal static class PolyBoolean
{
    public const double Scale = 1024;
    const double Tolerance = 0.05; // maximum error of the curve flattening, in document units
    const double MaxCoord = 1e5;   // beyond this the long products could overflow

    public readonly record struct P(long X, long Y);

    // ---- Flattening ------------------------------------------------------------------------

    /// <summary>Outlines of the object (in the space of its container), with flattened curves.</summary>
    public static List<List<P>> Flatten(SceneObject o)
    {
        List<PathSeg> segs;
        switch (o)
        {
            case RectangleObject:
                segs = PathData.RectangleSegments(o.X, o.Y, o.Width, o.Height);
                break;
            case EllipseObject:
                segs = PathData.EllipseSegments(o.X, o.Y, o.Width, o.Height);
                break;
            case PathObject p:
                segs = PathData.Transform(p.Segments, p.Fit);
                break;
            case TextObject:
                throw new CommandException($"shape.boolean: '{o.Name ?? o.Id}' is a text: convert it to curves first with shape.toPath.");
            case ImageObject:
                throw new CommandException($"shape.boolean: '{o.Name ?? o.Id}' is an image: images cannot be converted to paths and cannot take part in boolean operations.");
            default:
                throw new CommandException($"shape.boolean: '{o.Name ?? o.Id}' is a {o.Type}: boolean operations work on rectangles, ellipses and paths (ungroup groups first with object.ungroup).");
        }
        return FlattenSegments(PathData.Transform(segs, o.Transform));
    }

    static List<List<P>> FlattenSegments(List<PathSeg> segs)
    {
        var rings = new List<List<P>>();
        var pts = new List<(double X, double Y)>();
        double cx = 0, cy = 0;

        void Finish()
        {
            var ring = new List<P>();
            foreach (var (x, y) in pts)
            {
                if (Math.Abs(x) > MaxCoord || Math.Abs(y) > MaxCoord)
                    throw new CommandException($"shape.boolean: coordinates too large (over {MaxCoord:0} units).");
                var p = new P((long)Math.Round(x * Scale), (long)Math.Round(y * Scale));
                if (ring.Count == 0 || ring[^1] != p) ring.Add(p);
            }
            if (ring.Count > 1 && ring[^1] == ring[0]) ring.RemoveAt(ring.Count - 1);
            if (ring.Count >= 3) rings.Add(ring);
            pts.Clear();
        }

        foreach (var s in segs)
        {
            switch (s.Op)
            {
                case 'M':
                    Finish();
                    pts.Add((s.X, s.Y));
                    break;
                case 'L':
                    pts.Add((s.X, s.Y));
                    break;
                case 'C':
                {
                    double ddx = Math.Max(Math.Abs(cx - 2 * s.X1 + s.X2), Math.Abs(s.X1 - 2 * s.X2 + s.X));
                    double ddy = Math.Max(Math.Abs(cy - 2 * s.Y1 + s.Y2), Math.Abs(s.Y1 - 2 * s.Y2 + s.Y));
                    var n = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(0.75 * Math.Sqrt(ddx * ddx + ddy * ddy) / Tolerance)), 1, 256);
                    for (var i = 1; i <= n; i++)
                    {
                        double t = (double)i / n, u = 1 - t;
                        pts.Add((u * u * u * cx + 3 * u * u * t * s.X1 + 3 * u * t * t * s.X2 + t * t * t * s.X,
                                 u * u * u * cy + 3 * u * u * t * s.Y1 + 3 * u * t * t * s.Y2 + t * t * t * s.Y));
                    }
                    break;
                }
                case 'Z':
                    // A closed subpath restarts, if it continues, from its first point (as in the parser).
                    var first = pts.Count > 0 ? pts[0] : (cx, cy);
                    Finish();
                    cx = first.Item1; cy = first.Item2;
                    continue;
            }
            cx = s.X; cy = s.Y;
        }
        Finish();
        return rings;
    }

    // ---- Operation ---------------------------------------------------------------------------

    sealed class Edge(long ax, long ay, long bx, long by, int op)
    {
        public readonly long AX = ax, AY = ay, BX = bx, BY = by;
        public readonly int Op = op;
        public List<(double T, long X, long Y)>? Splits;

        public void AddSplit(double t, long x, long y) => (Splits ??= []).Add((t, x, y));
    }

    /// <summary>
    /// Resulting region: <paramref name="inside"/> receives for each operand whether the point is inside (nonzero).
    /// Returns rings with the interior on the left (outer rings and holes have opposite orientations).
    /// </summary>
    public static List<List<P>> Compute(List<List<List<P>>> operands, Func<bool[], bool> inside)
    {
        // 1. Original segments.
        var edges = new List<Edge>();
        for (var k = 0; k < operands.Count; k++)
            foreach (var ring in operands[k])
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    if (a != b) edges.Add(new Edge(a.X, a.Y, b.X, b.Y, k));
                }

        // 2. Split the segments at the intersections (sweep on the x axis).
        var order = Enumerable.Range(0, edges.Count).OrderBy(i => Math.Min(edges[i].AX, edges[i].BX)).ToArray();
        for (var ii = 0; ii < order.Length; ii++)
        {
            var e = edges[order[ii]];
            long maxX = Math.Max(e.AX, e.BX), minY = Math.Min(e.AY, e.BY), maxY = Math.Max(e.AY, e.BY);
            for (var jj = ii + 1; jj < order.Length; jj++)
            {
                var f = edges[order[jj]];
                if (Math.Min(f.AX, f.BX) > maxX) break;
                if (Math.Max(f.AY, f.BY) < minY || Math.Min(f.AY, f.BY) > maxY) continue;
                Intersect(e, f);
            }
        }

        // 3. Unique pieces (overlapping stretches of different operands become a single piece).
        // For each piece we remember the original segments it derives from: rounding of the intersections can put
        // its midpoint just outside the segment, so a geometric check is not enough to recognize them.
        var pieces = new Dictionary<(long, long, long, long), List<Edge>>();
        foreach (var e in edges)
        {
            var pts = new List<P> { new(e.AX, e.AY) };
            if (e.Splits != null)
                foreach (var s in e.Splits.OrderBy(s => s.T))
                {
                    var p = new P(s.X, s.Y);
                    if (p != pts[^1]) pts.Add(p);
                }
            var end = new P(e.BX, e.BY);
            if (pts[^1] != end) pts.Add(end);
            for (var i = 0; i + 1 < pts.Count; i++)
            {
                var (p, q) = (pts[i], pts[i + 1]);
                if (p == q) continue;
                var key = (p.X, p.Y, q.X, q.Y).CompareTo((q.X, q.Y, p.X, p.Y)) <= 0 ? (p.X, p.Y, q.X, q.Y) : (q.X, q.Y, p.X, p.Y);
                if (!pieces.TryGetValue(key, out var sources)) pieces[key] = sources = [];
                if (!sources.Contains(e)) sources.Add(e);
            }
        }

        if ((long)pieces.Count * edges.Count > 1_000_000_000)
            throw new CommandException("shape.boolean: the shapes are too complex (too many segments and crossings). Simplify the paths.");

        // 4. Classify each piece on both sides; keep those on the boundary, oriented with the interior on the left.
        var byOp = Enumerable.Range(0, operands.Count).Select(k => edges.Where(e => e.Op == k).ToArray()).ToArray();
        var left = new bool[operands.Count];
        var right = new bool[operands.Count];
        var boundary = new List<(P From, P To)>();
        const double tilt = 0.0031415926; // the ray is slightly tilted to avoid passing through vertices

        foreach (var ((x1, y1, x2, y2), sources) in pieces)
        {
            double ex = x2 - x1, ey = y2 - y1, len = Math.Sqrt(ex * ex + ey * ey);
            double nx = -ey / len, ny = ex / len; // left normal
            double mx = (x1 + x2) / 2.0, my = (y1 + y2) / 2.0;
            long m2x = x1 + x2, m2y = y1 + y2;
            double cs = Math.Cos(tilt), sn = Math.Sin(tilt);
            for (var k = 0; k < operands.Count; k++)
            {
                left[k] = Winding(byOp[k], sources, mx, my, nx * cs - ny * sn, nx * sn + ny * cs, m2x, m2y) != 0;
                right[k] = Winding(byOp[k], sources, mx, my, -nx * cs + ny * sn, -nx * sn - ny * cs, m2x, m2y) != 0;
            }
            bool inL = inside(left), inR = inside(right);
            if (inL == inR) continue;
            boundary.Add(inL ? (new P(x1, y1), new P(x2, y2)) : (new P(x2, y2), new P(x1, y1)));
        }

        // 5. Chain the pieces into rings.
        var outgoing = new Dictionary<P, List<int>>();
        for (var i = 0; i < boundary.Count; i++)
        {
            if (!outgoing.TryGetValue(boundary[i].From, out var l)) outgoing[boundary[i].From] = l = [];
            l.Add(i);
        }
        var used = new bool[boundary.Count];
        var result = new List<List<P>>();
        for (var i = 0; i < boundary.Count; i++)
        {
            if (used[i]) continue;
            var ring = new List<P> { boundary[i].From };
            used[i] = true;
            var cur = boundary[i].To;
            while (cur != ring[0])
            {
                ring.Add(cur);
                var next = outgoing.TryGetValue(cur, out var l) ? l.FirstOrDefault(j => !used[j], -1) : -1;
                if (next < 0) break; // chain not closed (numerical noise): the ring is closed with a segment
                used[next] = true;
                cur = boundary[next].To;
            }
            var simple = Simplify(ring);
            if (simple.Count >= 3 && Area2(simple) != 0) result.Add(simple);
        }
        return result;
    }

    static void Intersect(Edge e, Edge f)
    {
        long rx = e.BX - e.AX, ry = e.BY - e.AY, sx = f.BX - f.AX, sy = f.BY - f.AY;
        long qx = f.AX - e.AX, qy = f.AY - e.AY;
        var den = rx * sy - ry * sx;

        if (den == 0)
        {
            if (qx * ry - qy * rx != 0) return; // parallel, not aligned
            // Aligned: the ends of one that fall inside the other split it.
            long rr = rx * rx + ry * ry, ss = sx * sx + sy * sy;
            foreach (var (px, py) in new[] { (f.AX, f.AY), (f.BX, f.BY) })
            {
                var d = (px - e.AX) * rx + (py - e.AY) * ry;
                if (d > 0 && d < rr) e.AddSplit((double)d / rr, px, py);
            }
            foreach (var (px, py) in new[] { (e.AX, e.AY), (e.BX, e.BY) })
            {
                var d = (px - f.AX) * sx + (py - f.AY) * sy;
                if (d > 0 && d < ss) f.AddSplit((double)d / ss, px, py);
            }
            return;
        }

        var tNum = qx * sy - qy * sx;
        var uNum = qx * ry - qy * rx;
        if (den < 0) { den = -den; tNum = -tNum; uNum = -uNum; }
        if (tNum < 0 || tNum > den || uNum < 0 || uNum > den) return;

        // If the crossing falls on an endpoint the exact one is used, so both segments share the same vertex.
        long x, y;
        if (tNum == 0) (x, y) = (e.AX, e.AY);
        else if (tNum == den) (x, y) = (e.BX, e.BY);
        else if (uNum == 0) (x, y) = (f.AX, f.AY);
        else if (uNum == den) (x, y) = (f.BX, f.BY);
        else
        {
            x = e.AX + (long)Math.Round((double)rx * tNum / den);
            y = e.AY + (long)Math.Round((double)ry * tNum / den);
        }
        if (tNum > 0 && tNum < den) e.AddSplit((double)tNum / den, x, y);
        if (uNum > 0 && uNum < den) f.AddSplit((double)uNum / den, x, y);
    }

    // Winding number of the point (ray origin) with respect to the outlines of an operand.
    // The segments the piece being classified derives from (and those that contain it exactly) are excluded.
    static int Winding(Edge[] edges, List<Edge> sources, double mx, double my, double dx, double dy, long m2x, long m2y)
    {
        var w = 0;
        foreach (var e in edges)
        {
            if (sources.Contains(e)) continue;
            long a2x = 2 * e.AX, a2y = 2 * e.AY, b2x = 2 * e.BX, b2y = 2 * e.BY;
            if ((b2x - a2x) * (m2y - a2y) - (b2y - a2y) * (m2x - a2x) == 0 &&
                m2x >= Math.Min(a2x, b2x) && m2x <= Math.Max(a2x, b2x) &&
                m2y >= Math.Min(a2y, b2y) && m2y <= Math.Max(a2y, b2y)) continue;

            double ax = e.AX - mx, ay = e.AY - my, bx = e.BX - mx, by = e.BY - my;
            double sa = dx * ay - dy * ax, sb = dx * by - dy * bx;
            if ((sa > 0) == (sb > 0)) continue;
            double ex = bx - ax, ey = by - ay, den = dx * ey - dy * ex;
            if (den == 0) continue;
            if ((ax * ey - ay * ex) / den <= 0) continue; // behind the origin
            w += den > 0 ? 1 : -1;
        }
        return w;
    }

    // Removes duplicate, collinear and "spike" vertices (exact, in integers).
    static List<P> Simplify(List<P> ring)
    {
        var r = ring;
        bool changed;
        do
        {
            changed = false;
            var res = new List<P>();
            foreach (var p in r)
            {
                if (res.Count > 0 && res[^1] == p) { changed = true; continue; }
                while (res.Count >= 2 && Cross(res[^2], res[^1], p) == 0) { res.RemoveAt(res.Count - 1); changed = true; }
                res.Add(p);
            }
            while (res.Count >= 3 && (res[^1] == res[0] || Cross(res[^2], res[^1], res[0]) == 0)) { res.RemoveAt(res.Count - 1); changed = true; }
            while (res.Count >= 3 && Cross(res[^1], res[0], res[1]) == 0) { res.RemoveAt(0); changed = true; }
            r = res;
        } while (changed && r.Count >= 3);
        return r;
    }

    static long Cross(P a, P b, P c) => (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);

    static double Area2(List<P> r)
    {
        double a = 0;
        for (var i = 0; i < r.Count; i++)
        {
            var (p, q) = (r[i], r[(i + 1) % r.Count]);
            a += (double)p.X * q.Y - (double)q.X * p.Y;
        }
        return a;
    }

    public static List<PathSeg> ToSegments(List<List<P>> rings)
    {
        var segs = new List<PathSeg>();
        foreach (var ring in rings)
        {
            for (var i = 0; i < ring.Count; i++)
                segs.Add(new(i == 0 ? 'M' : 'L', X: ring[i].X / Scale, Y: ring[i].Y / Scale));
            segs.Add(new('Z'));
        }
        return segs;
    }
}

/// <summary>
/// Boolean operation between shapes at the same level. The result is a path with the style of the lowest object (z-order).
/// weld = union; intersect = intersection; subtract = the lowest minus all the others (the others disappear);
/// trim = like subtract but the cutting objects stay. With keepOriginals the originals stay (except the one cut by trim).
/// </summary>
public sealed class BooleanOp : Command
{
    public string Op { get; set; } = "";
    public List<string> Targets { get; set; } = [];
    public bool KeepOriginals { get; set; }
    public string? Name { get; set; }

    PathObject? _result;
    List<SceneObject>? _container;
    List<(int Index, SceneObject Obj)> _removed = [];
    int _insertIndex;

    /// <summary>Output only: id of the resulting path.</summary>
    public string? Id => _result?.Id;

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _result is null ? [] : [_result.Id];

    public override void Apply(Document doc)
    {
        if (Op is not ("weld" or "intersect" or "subtract" or "trim"))
            throw new CommandException($"shape.boolean: op '{Op}' is not valid. Values: \"weld\", \"intersect\", \"subtract\", \"trim\".");
        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        if (objs.Count < 2) throw new CommandException("shape.boolean: at least 2 objects are needed in 'targets'.");
        var container = doc.ContainerOf(objs[0])!;
        if (objs.Any(o => doc.ContainerOf(o) != container))
            throw new CommandException("shape.boolean: the objects must be at the same level (same layer or same group).");

        var entries = objs.Select(o => (Index: container.IndexOf(o), Obj: o)).OrderBy(e => e.Index).ToList();
        if (_result == null)
        {
            var operands = entries.Select(e => PolyBoolean.Flatten(e.Obj)).ToList();
            Func<bool[], bool> inside = Op switch
            {
                "weld" => f => f.Any(x => x),
                "intersect" => f => f.All(x => x),
                _ => f => f[0] && !f.Skip(1).Any(x => x), // subtract and trim: the lowest minus the others
            };
            var rings = PolyBoolean.Compute(operands, inside);
            if (rings.Count == 0)
                throw new CommandException($"shape.boolean: the result of '{Op}' is empty (do the objects overlap?).");

            var segs = PolyBoolean.ToSegments(rings);
            var box = PathData.Bounds(segs)!.Value;
            var back = entries[0].Obj;
            _result = new PathObject
            {
                Id = doc.NewId(), Name = Name, Segments = segs, Source = box,
                X = box.X, Y = box.Y, Width = box.Width, Height = box.Height,
                Fill = back.Fill, Stroke = back.Stroke, StrokeWidth = back.StrokeWidth, StrokeDash = back.StrokeDash, StrokeCap = back.StrokeCap, StrokeJoin = back.StrokeJoin, Opacity = back.Opacity,
            };
        }

        // Trim only removes the cut object; the other operators remove all (unless keepOriginals).
        _removed = Op == "trim" ? [entries[0]] : KeepOriginals ? [] : entries;
        foreach (var (_, obj) in _removed) container.Remove(obj);
        // The result takes the place of the lowest; if nothing is removed it goes on top of all.
        _insertIndex = _removed.Count == 0 ? entries[^1].Index + 1 : entries[0].Index;
        container.Insert(_insertIndex, _result);
        _container = container;
    }

    public override void Undo(Document doc)
    {
        _container!.Remove(_result!);
        foreach (var (index, obj) in _removed) _container.Insert(index, obj);
    }
}
