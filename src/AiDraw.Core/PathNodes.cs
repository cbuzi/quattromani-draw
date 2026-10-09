using System.Numerics;

namespace AiDraw.Core;

/// <summary>Nodo di un path: In/Out sono le maniglie della curva in ingresso e in uscita (assenti = segmento dritto).</summary>
public sealed class PathNode(Vector2 p)
{
    public Vector2 P = p;
    public Vector2? In, Out;

    public PathNode Clone() => new(P) { In = In, Out = Out };

    /// <summary>Nodo liscio: le due maniglie esistono e sono allineate in direzioni opposte.</summary>
    public bool IsSmooth
    {
        get
        {
            if (In is not { } i || Out is not { } o) return false;
            Vector2 a = i - P, b = o - P;
            var la = a.Length(); var lb = b.Length();
            if (la < 1e-6f || lb < 1e-6f) return false;
            return Math.Abs(a.X * b.Y - a.Y * b.X) <= 1e-3f * la * lb && Vector2.Dot(a, b) < 0;
        }
    }
}

public sealed class SubPath
{
    public bool Closed;
    public List<PathNode> Nodes = [];

    public SubPath Clone() => new() { Closed = Closed, Nodes = Nodes.Select(n => n.Clone()).ToList() };

    public int SegmentCount => Closed ? Nodes.Count : Nodes.Count - 1;
    public (PathNode A, PathNode B) Segment(int i) => (Nodes[i], Nodes[(i + 1) % Nodes.Count]);
}

/// <summary>Modello a nodi di un path, per la modifica con lo strumento Nodi. Le coordinate sono quelle dei segmenti.</summary>
public static class PathNodes
{
    static Vector2 V(double x, double y) => new((float)x, (float)y);

    public static List<SubPath> FromSegments(IEnumerable<PathSeg> segs)
    {
        var result = new List<SubPath>();
        SubPath? cur = null;
        foreach (var s in segs)
            switch (s.Op)
            {
                case 'M':
                    cur = new SubPath();
                    cur.Nodes.Add(new(V(s.X, s.Y)));
                    result.Add(cur);
                    break;
                case 'L':
                    cur?.Nodes.Add(new(V(s.X, s.Y)));
                    break;
                case 'C':
                    if (cur == null) break;
                    cur.Nodes[^1].Out = V(s.X1, s.Y1);
                    cur.Nodes.Add(new(V(s.X, s.Y)) { In = V(s.X2, s.Y2) });
                    break;
                case 'Z':
                    if (cur == null) break;
                    cur.Closed = true;
                    // Se l'ultimo segmento torna esattamente al primo nodo, i due nodi sono lo stesso.
                    if (cur.Nodes.Count > 2 && Vector2.Distance(cur.Nodes[^1].P, cur.Nodes[0].P) < 1e-4f)
                    {
                        cur.Nodes[0].In = cur.Nodes[^1].In;
                        cur.Nodes.RemoveAt(cur.Nodes.Count - 1);
                    }
                    break;
            }
        result.RemoveAll(sp => sp.Nodes.Count < 2);
        return result;
    }

    public static List<PathSeg> ToSegments(IEnumerable<SubPath> subs)
    {
        var segs = new List<PathSeg>();
        void Seg(PathNode a, PathNode b)
        {
            if (a.Out is null && b.In is null) segs.Add(new('L', X: b.P.X, Y: b.P.Y));
            else
            {
                var c1 = a.Out ?? a.P; var c2 = b.In ?? b.P;
                segs.Add(new('C', c1.X, c1.Y, c2.X, c2.Y, b.P.X, b.P.Y));
            }
        }

        foreach (var sp in subs)
        {
            segs.Add(new('M', X: sp.Nodes[0].P.X, Y: sp.Nodes[0].P.Y));
            for (var i = 1; i < sp.Nodes.Count; i++) Seg(sp.Nodes[i - 1], sp.Nodes[i]);
            if (sp.Closed)
            {
                // Il segmento di chiusura è esplicito solo se è una curva; altrimenti lo fa Z.
                if (sp.Nodes[^1].Out != null || sp.Nodes[0].In != null) Seg(sp.Nodes[^1], sp.Nodes[0]);
                segs.Add(new('Z'));
            }
        }
        return segs;
    }

    public static List<SubPath> Clone(IEnumerable<SubPath> subs) => subs.Select(s => s.Clone()).ToList();

    // ---- Geometria dei segmenti ---------------------------------------------------------------

    static Vector2 Eval(PathNode a, PathNode b, float t)
    {
        if (a.Out is null && b.In is null) return Vector2.Lerp(a.P, b.P, t);
        Vector2 p0 = a.P, p1 = a.Out ?? a.P, p2 = b.In ?? b.P, p3 = b.P;
        var u = 1 - t;
        return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
    }

    /// <summary>Punto del path più vicino a <paramref name="p"/>: (sottopath, segmento, parametro t, distanza).</summary>
    public static (int Sub, int Seg, float T, float Dist)? Nearest(List<SubPath> subs, Vector2 p)
    {
        (int, int, float, float)? best = null;
        for (var si = 0; si < subs.Count; si++)
            for (var gi = 0; gi < subs[si].SegmentCount; gi++)
            {
                var (a, b) = subs[si].Segment(gi);
                const int steps = 48;
                for (var k = 0; k <= steps; k++)
                {
                    var t = (float)k / steps;
                    var d = Vector2.Distance(Eval(a, b, t), p);
                    if (best is null || d < best.Value.Item4) best = (si, gi, t, d);
                }
            }
        return best;
    }

    /// <summary>Inserisce un nodo sul segmento senza cambiare la forma (de Casteljau per le curve). Restituisce l'indice del nuovo nodo.</summary>
    public static int InsertNode(SubPath sp, int seg, float t)
    {
        var (a, b) = sp.Segment(seg);
        PathNode n;
        if (a.Out is null && b.In is null) n = new(Vector2.Lerp(a.P, b.P, t));
        else
        {
            Vector2 p0 = a.P, p1 = a.Out ?? a.P, p2 = b.In ?? b.P, p3 = b.P;
            Vector2 q0 = Vector2.Lerp(p0, p1, t), q1 = Vector2.Lerp(p1, p2, t), q2 = Vector2.Lerp(p2, p3, t);
            Vector2 r0 = Vector2.Lerp(q0, q1, t), r1 = Vector2.Lerp(q1, q2, t);
            n = new(Vector2.Lerp(r0, r1, t)) { In = r0, Out = r1 };
            a.Out = q0 == a.P ? null : q0;
            b.In = q2 == b.P ? null : q2;
        }
        var index = seg + 1;
        sp.Nodes.Insert(index, n);
        return index;
    }

    /// <summary>Toglie un nodo (la forma si richiude tra i vicini). False se resterebbero troppo pochi nodi.</summary>
    public static bool RemoveNode(SubPath sp, int index)
    {
        if (sp.Nodes.Count <= (sp.Closed ? 3 : 2)) return false;
        sp.Nodes.RemoveAt(index);
        return true;
    }

    // ---- Operazioni sui nodi selezionati ------------------------------------------------------

    /// <summary>Segmenti tra due nodi selezionati; con un solo nodo selezionato, i due segmenti adiacenti.</summary>
    static IEnumerable<(SubPath Sp, int Seg)> SelectedSegments(List<SubPath> subs, ISet<(int Sub, int Idx)> sel)
    {
        for (var si = 0; si < subs.Count; si++)
        {
            var sp = subs[si];
            for (var gi = 0; gi < sp.SegmentCount; gi++)
            {
                int ia = gi, ib = (gi + 1) % sp.Nodes.Count;
                bool a = sel.Contains((si, ia)), b = sel.Contains((si, ib));
                var single = sel.Count(s => s.Sub == si) == 1;
                if (a && b || single && (a || b)) yield return (sp, gi);
            }
        }
    }

    public static void SegmentsToLine(List<SubPath> subs, ISet<(int Sub, int Idx)> sel)
    {
        foreach (var (sp, gi) in SelectedSegments(subs, sel))
        {
            var (a, b) = sp.Segment(gi);
            a.Out = null; b.In = null;
        }
    }

    public static void SegmentsToCurve(List<SubPath> subs, ISet<(int Sub, int Idx)> sel)
    {
        foreach (var (sp, gi) in SelectedSegments(subs, sel))
        {
            var (a, b) = sp.Segment(gi);
            if (a.Out is null && b.In is null)
            {
                a.Out = a.P + (b.P - a.P) / 3;
                b.In = b.P - (b.P - a.P) / 3;
            }
        }
    }

    /// <summary>Rende lisci i nodi selezionati: maniglie allineate (create dai vicini se mancano), lunghezze conservate.</summary>
    public static void MakeSmooth(List<SubPath> subs, ISet<(int Sub, int Idx)> sel)
    {
        foreach (var (si, ni) in sel)
        {
            if (si >= subs.Count || ni >= subs[si].Nodes.Count) continue;
            var sp = subs[si]; var n = sp.Nodes[ni]; var cnt = sp.Nodes.Count;
            var prev = ni > 0 ? sp.Nodes[ni - 1] : sp.Closed ? sp.Nodes[cnt - 1] : null;
            var next = ni < cnt - 1 ? sp.Nodes[ni + 1] : sp.Closed ? sp.Nodes[0] : null;

            // Direzione della tangente: dalle maniglie esistenti, altrimenti dai vicini.
            Vector2 dir;
            if (n.In is { } i0 && n.Out is { } o0) dir = o0 - i0;
            else if (n.Out is { } o1) dir = o1 - n.P;
            else if (n.In is { } i1) dir = n.P - i1;
            else dir = (next?.P ?? n.P) - (prev?.P ?? n.P);
            if (dir.LengthSquared() < 1e-12f) continue;
            dir = Vector2.Normalize(dir);

            var lenIn = n.In is { } a ? Vector2.Distance(a, n.P) : prev is null ? 0 : Vector2.Distance(prev.P, n.P) / 3;
            var lenOut = n.Out is { } b ? Vector2.Distance(b, n.P) : next is null ? 0 : Vector2.Distance(next.P, n.P) / 3;
            if (prev != null && lenIn > 0) n.In = n.P - dir * lenIn;
            if (next != null && lenOut > 0) n.Out = n.P + dir * lenOut;
        }
    }

    /// <summary>
    /// Sposta una maniglia. Se il nodo era liscio (e non si chiede l'indipendenza) l'altra maniglia resta allineata,
    /// conservando la sua lunghezza.
    /// </summary>
    public static void MoveHandle(PathNode n, bool isOut, Vector2 pos, bool independent)
    {
        var wasSmooth = n.IsSmooth;
        var other = isOut ? n.In : n.Out;
        if (isOut) n.Out = pos; else n.In = pos;
        if (wasSmooth && !independent && other is { } o)
        {
            var d = pos - n.P;
            if (d.LengthSquared() > 1e-12f)
            {
                var opposite = n.P - Vector2.Normalize(d) * Vector2.Distance(o, n.P);
                if (isOut) n.In = opposite; else n.Out = opposite;
            }
        }
    }
}
