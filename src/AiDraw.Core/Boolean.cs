using System.Text.Json.Serialization;

namespace AiDraw.Core;

/// <summary>
/// Operazioni booleane su regioni poligonali (curve appiattite), senza dipendenze.
/// Coordinate su una griglia intera di 1/1024: i predicati geometrici sono esatti, quindi bordi coincidenti,
/// tangenze e intersezioni negli stessi punti non danno errori di arrotondamento.
/// Algoritmo: (1) tutti i segmenti vengono spezzati nelle intersezioni; (2) ogni pezzo viene classificato
/// dentro/fuori sui due lati con un raggio contro i contorni originali (regola nonzero, come il rendering);
/// (3) i pezzi di confine vengono concatenati in anelli con l'interno a sinistra.
/// </summary>
internal static class PolyBoolean
{
    public const double Scale = 1024;
    const double Tolerance = 0.05; // errore massimo dell'appiattimento delle curve, in unità documento
    const double MaxCoord = 1e5;   // oltre questo i prodotti in long potrebbero andare in overflow

    public readonly record struct P(long X, long Y);

    // ---- Appiattimento ------------------------------------------------------------------------

    /// <summary>Contorni dell'oggetto (nello spazio del suo contenitore), con le curve appiattite.</summary>
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
            default:
                throw new CommandException($"shape.boolean: '{o.Name ?? o.Id}' e' un {o.Type}: le operazioni booleane funzionano su rettangoli, ellissi e path (separa prima i gruppi con object.ungroup).");
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
                    throw new CommandException($"shape.boolean: coordinate troppo grandi (oltre {MaxCoord:0} unita').");
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
                    // Un subpath chiuso riparte, se continua, dal suo primo punto (come nel parser).
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

    // ---- Operazione ---------------------------------------------------------------------------

    sealed class Edge(long ax, long ay, long bx, long by, int op)
    {
        public readonly long AX = ax, AY = ay, BX = bx, BY = by;
        public readonly int Op = op;
        public List<(double T, long X, long Y)>? Splits;

        public void AddSplit(double t, long x, long y) => (Splits ??= []).Add((t, x, y));
    }

    /// <summary>
    /// Regione risultante: <paramref name="inside"/> riceve per ogni operando se il punto è dentro (nonzero).
    /// Restituisce anelli con l'interno a sinistra (esterni e buchi hanno orientamenti opposti).
    /// </summary>
    public static List<List<P>> Compute(List<List<List<P>>> operands, Func<bool[], bool> inside)
    {
        // 1. Segmenti originali.
        var edges = new List<Edge>();
        for (var k = 0; k < operands.Count; k++)
            foreach (var ring in operands[k])
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    if (a != b) edges.Add(new Edge(a.X, a.Y, b.X, b.Y, k));
                }

        // 2. Spezza i segmenti nelle intersezioni (sweep sull'asse x).
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

        // 3. Pezzi unici (i tratti sovrapposti di operandi diversi diventano un solo pezzo).
        // Per ogni pezzo si ricordano i segmenti originali da cui deriva: l'arrotondamento delle intersezioni può portare
        // il suo punto medio appena fuori dal segmento, quindi non basta un controllo geometrico per riconoscerli.
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
            throw new CommandException("shape.boolean: le forme sono troppo complesse (troppi segmenti e incroci). Semplifica i path.");

        // 4. Classifica ogni pezzo dai due lati; tiene quelli sul confine, orientati con l'interno a sinistra.
        var byOp = Enumerable.Range(0, operands.Count).Select(k => edges.Where(e => e.Op == k).ToArray()).ToArray();
        var left = new bool[operands.Count];
        var right = new bool[operands.Count];
        var boundary = new List<(P From, P To)>();
        const double tilt = 0.0031415926; // il raggio è leggermente inclinato per evitare di passare dai vertici

        foreach (var ((x1, y1, x2, y2), sources) in pieces)
        {
            double ex = x2 - x1, ey = y2 - y1, len = Math.Sqrt(ex * ex + ey * ey);
            double nx = -ey / len, ny = ex / len; // normale sinistra
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

        // 5. Concatena i pezzi in anelli.
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
                if (next < 0) break; // catena non chiusa (rumore numerico): l'anello si chiude con un segmento
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
            if (qx * ry - qy * rx != 0) return; // paralleli, non allineati
            // Allineati: gli estremi di uno che cadono dentro l'altro lo spezzano.
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

        // Se l'incrocio cade su un estremo si usa quello esatto, così entrambi i segmenti condividono lo stesso vertice.
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

    // Numero di avvolgimento del punto (origine del raggio) rispetto ai contorni di un operando.
    // Sono esclusi i segmenti da cui deriva il pezzo che stiamo classificando (e quelli che lo contengono esattamente).
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
            if ((ax * ey - ay * ex) / den <= 0) continue; // dietro l'origine
            w += den > 0 ? 1 : -1;
        }
        return w;
    }

    // Toglie vertici duplicati, allineati e "spike" (esatto, in interi).
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
/// Operazione booleana tra forme dello stesso livello. Il risultato è un path con lo stile dell'oggetto più in basso (z-order).
/// weld = unione; intersect = intersezione; subtract = il più basso meno tutti gli altri (gli altri spariscono);
/// trim = come subtract ma gli oggetti che tagliano restano. Con keepOriginals gli originali restano (tranne il tagliato da trim).
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

    /// <summary>Solo in uscita: id del path risultante.</summary>
    public string? Id => _result?.Id;

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _result is null ? [] : [_result.Id];

    public override void Apply(Document doc)
    {
        if (Op is not ("weld" or "intersect" or "subtract" or "trim"))
            throw new CommandException($"shape.boolean: op '{Op}' non valida. Valori: \"weld\", \"intersect\", \"subtract\", \"trim\".");
        var objs = Targets.Select(t => doc.Find(t)).Distinct().ToList();
        if (objs.Count < 2) throw new CommandException("shape.boolean: servono almeno 2 oggetti in 'targets'.");
        var container = doc.ContainerOf(objs[0])!;
        if (objs.Any(o => doc.ContainerOf(o) != container))
            throw new CommandException("shape.boolean: gli oggetti devono stare allo stesso livello (stesso layer o stesso gruppo).");

        var entries = objs.Select(o => (Index: container.IndexOf(o), Obj: o)).OrderBy(e => e.Index).ToList();
        if (_result == null)
        {
            var operands = entries.Select(e => PolyBoolean.Flatten(e.Obj)).ToList();
            Func<bool[], bool> inside = Op switch
            {
                "weld" => f => f.Any(x => x),
                "intersect" => f => f.All(x => x),
                _ => f => f[0] && !f.Skip(1).Any(x => x), // subtract e trim: il più basso meno gli altri
            };
            var rings = PolyBoolean.Compute(operands, inside);
            if (rings.Count == 0)
                throw new CommandException($"shape.boolean: il risultato di '{Op}' e' vuoto (gli oggetti non si sovrappongono?).");

            var segs = PolyBoolean.ToSegments(rings);
            var box = PathData.Bounds(segs)!.Value;
            var back = entries[0].Obj;
            _result = new PathObject
            {
                Id = doc.NewId(), Name = Name, Segments = segs, Source = box,
                X = box.X, Y = box.Y, Width = box.Width, Height = box.Height,
                Fill = back.Fill, Stroke = back.Stroke, StrokeWidth = back.StrokeWidth,
            };
        }

        // Trim toglie solo l'oggetto tagliato; gli altri operatori tolgono tutti (salvo keepOriginals).
        _removed = Op == "trim" ? [entries[0]] : KeepOriginals ? [] : entries;
        foreach (var (_, obj) in _removed) container.Remove(obj);
        // Il risultato prende il posto del più basso; se non si toglie nulla va sopra tutti.
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
