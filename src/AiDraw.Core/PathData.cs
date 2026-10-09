using System.Globalization;
using System.Numerics;
using System.Text;

namespace AiDraw.Core;

/// <summary>Segmento di path in coordinate assolute: M (X,Y), L (X,Y), C (X1,Y1,X2,Y2,X,Y), Z.</summary>
public readonly record struct PathSeg(char Op, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0, double X = 0, double Y = 0);

/// <summary>Path bezier: lettura/scrittura dei dati SVG ("d") e geometria. Tutto viene normalizzato a M, L, C, Z assoluti.</summary>
public static class PathData
{
    public static List<PathSeg> Parse(string d)
    {
        var s = new Scanner(d);
        var segs = new List<PathSeg>();
        double cx = 0, cy = 0, sx = 0, sy = 0, lx = 0, ly = 0; // punto corrente, inizio subpath, ultimo punto di controllo
        char cmd = '\0', prev = '\0';

        while (true)
        {
            s.SkipSeparators();
            if (s.End) break;
            if (s.AtLetter)
            {
                cmd = s.ReadLetter();
                if (cmd is 'Z' or 'z')
                {
                    if (segs.Count == 0) throw s.Error("il path deve iniziare con M");
                    segs.Add(new('Z'));
                    cx = sx; cy = sy; prev = 'Z';
                    continue;
                }
            }
            else if (cmd == '\0') throw s.Error("il path deve iniziare con M, es. \"M 0 0 L 100 0 L 50 80 Z\"");
            else if (cmd is 'Z' or 'z') throw s.Error("numeri inattesi dopo Z");

            var rel = char.IsLower(cmd);
            var u = char.ToUpperInvariant(cmd);
            if (segs.Count == 0 && u != 'M') throw s.Error("il path deve iniziare con M, es. \"M 0 0 L 100 0 L 50 80 Z\"");
            if (prev == 'Z' && u != 'M') segs.Add(new('M', X: sx, Y: sy)); // dopo Z si riparte dall'inizio del subpath
            double ox = rel ? cx : 0, oy = rel ? cy : 0;
            double x = 0, y = 0;

            switch (u)
            {
                case 'M':
                    x = s.Number() + ox; y = s.Number() + oy;
                    segs.Add(new('M', X: x, Y: y));
                    sx = x; sy = y;
                    cmd = rel ? 'l' : 'L'; // le coppie successive sono segmenti L
                    break;
                case 'L':
                    x = s.Number() + ox; y = s.Number() + oy;
                    segs.Add(new('L', X: x, Y: y));
                    break;
                case 'H':
                    x = s.Number() + ox; y = cy;
                    segs.Add(new('L', X: x, Y: y));
                    break;
                case 'V':
                    x = cx; y = s.Number() + oy;
                    segs.Add(new('L', X: x, Y: y));
                    break;
                case 'C':
                {
                    var x1 = s.Number() + ox; var y1 = s.Number() + oy;
                    var x2 = s.Number() + ox; var y2 = s.Number() + oy;
                    x = s.Number() + ox; y = s.Number() + oy;
                    segs.Add(new('C', x1, y1, x2, y2, x, y));
                    lx = x2; ly = y2;
                    break;
                }
                case 'S':
                {
                    var (x1, y1) = prev is 'C' or 'S' ? (2 * cx - lx, 2 * cy - ly) : (cx, cy);
                    var x2 = s.Number() + ox; var y2 = s.Number() + oy;
                    x = s.Number() + ox; y = s.Number() + oy;
                    segs.Add(new('C', x1, y1, x2, y2, x, y));
                    lx = x2; ly = y2;
                    break;
                }
                case 'Q':
                {
                    var qx = s.Number() + ox; var qy = s.Number() + oy;
                    x = s.Number() + ox; y = s.Number() + oy;
                    segs.Add(QuadToCubic(cx, cy, qx, qy, x, y));
                    lx = qx; ly = qy;
                    break;
                }
                case 'T':
                {
                    var (qx, qy) = prev is 'Q' or 'T' ? (2 * cx - lx, 2 * cy - ly) : (cx, cy);
                    x = s.Number() + ox; y = s.Number() + oy;
                    segs.Add(QuadToCubic(cx, cy, qx, qy, x, y));
                    lx = qx; ly = qy;
                    break;
                }
                case 'A':
                {
                    var rx = s.Number(); var ry = s.Number(); var rot = s.Number();
                    var large = s.Flag(); var sweep = s.Flag();
                    x = s.Number() + ox; y = s.Number() + oy;
                    ArcTo(segs, cx, cy, rx, ry, rot, large, sweep, x, y);
                    break;
                }
                default:
                    throw s.Error($"comando '{cmd}' non supportato (usa M L H V C S Q T A Z)");
            }
            cx = x; cy = y; prev = u;
        }

        if (!segs.Any(g => g.Op is 'L' or 'C'))
            throw new CommandException("path 'd': serve almeno un segmento oltre a M, es. \"M 0 0 L 100 0 L 50 80 Z\".");
        return segs;
    }

    static PathSeg QuadToCubic(double x0, double y0, double qx, double qy, double x, double y) =>
        new('C', x0 + 2.0 / 3 * (qx - x0), y0 + 2.0 / 3 * (qy - y0), x + 2.0 / 3 * (qx - x), y + 2.0 / 3 * (qy - y), x, y);

    // Arco SVG -> curve cubiche (parametrizzazione dal centro, SVG spec F.6.5), al massimo 90° per curva.
    static void ArcTo(List<PathSeg> segs, double x0, double y0, double rx, double ry, double phiDeg,
        bool large, bool sweep, double x, double y)
    {
        if (x0 == x && y0 == y) return;
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        if (rx == 0 || ry == 0) { segs.Add(new('L', X: x, Y: y)); return; }

        var phi = phiDeg * Math.PI / 180;
        double cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (x0 - x) / 2, dy = (y0 - y) / 2;
        double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;

        var lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1) { var k = Math.Sqrt(lambda); rx *= k; ry *= k; }

        double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        var coef = (large == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, num / den));
        double cxp = coef * rx * y1p / ry, cyp = -coef * ry * x1p / rx;
        double cx = cos * cxp - sin * cyp + (x0 + x) / 2, cy = sin * cxp + cos * cyp + (y0 + y) / 2;

        static double Angle(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        var theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        var dtheta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && dtheta > 0) dtheta -= 2 * Math.PI;
        else if (sweep && dtheta < 0) dtheta += 2 * Math.PI;

        var n = (int)Math.Ceiling(Math.Abs(dtheta) / (Math.PI / 2) - 1e-9);
        var delta = dtheta / n;
        var t = 4.0 / 3 * Math.Tan(delta / 4);

        (double X, double Y) Map(double u, double v) => (cx + cos * rx * u - sin * ry * v, cy + sin * rx * u + cos * ry * v);

        for (var i = 0; i < n; i++)
        {
            double a1 = theta1 + i * delta, a2 = a1 + delta;
            var p1 = Map(Math.Cos(a1) - t * Math.Sin(a1), Math.Sin(a1) + t * Math.Cos(a1));
            var p2 = Map(Math.Cos(a2) + t * Math.Sin(a2), Math.Sin(a2) - t * Math.Cos(a2));
            var p3 = i == n - 1 ? (x, y) : Map(Math.Cos(a2), Math.Sin(a2));
            segs.Add(new('C', p1.X, p1.Y, p2.X, p2.Y, p3.Item1, p3.Item2));
        }
    }

    public static string ToString(IEnumerable<PathSeg> segs)
    {
        static string F(double v)
        {
            v = Math.Round(v, 2);
            if (v == 0) v = 0; // niente "-0"
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        var sb = new StringBuilder();
        foreach (var s in segs)
        {
            if (sb.Length > 0) sb.Append(' ');
            switch (s.Op)
            {
                case 'M': case 'L': sb.Append(s.Op).Append(' ').Append(F(s.X)).Append(' ').Append(F(s.Y)); break;
                case 'C':
                    sb.Append("C ").Append(F(s.X1)).Append(' ').Append(F(s.Y1)).Append(' ')
                      .Append(F(s.X2)).Append(' ').Append(F(s.Y2)).Append(' ').Append(F(s.X)).Append(' ').Append(F(s.Y));
                    break;
                default: sb.Append('Z'); break;
            }
        }
        return sb.ToString();
    }

    public static List<PathSeg> Transform(IEnumerable<PathSeg> segs, Matrix3x2 m)
    {
        static (double, double) P(double x, double y, Matrix3x2 m)
        {
            var v = Vector2.Transform(new((float)x, (float)y), m);
            return (v.X, v.Y);
        }

        return segs.Select(s =>
        {
            if (s.Op == 'Z') return s;
            var (x, y) = P(s.X, s.Y, m);
            if (s.Op != 'C') return s with { X = x, Y = y };
            var (x1, y1) = P(s.X1, s.Y1, m);
            var (x2, y2) = P(s.X2, s.Y2, m);
            return new PathSeg('C', x1, y1, x2, y2, x, y);
        }).ToList();
    }

    /// <summary>Riquadro esatto del path (considera gli estremi delle curve, non i punti di controllo).</summary>
    public static Box? Bounds(IEnumerable<PathSeg> segs)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double cx = 0, cy = 0;
        var any = false;

        void Add(double x, double y)
        {
            any = true;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        foreach (var s in segs)
        {
            switch (s.Op)
            {
                case 'M': case 'L':
                    Add(s.X, s.Y);
                    break;
                case 'C':
                    foreach (var t in Extrema(cx, s.X1, s.X2, s.X).Concat(Extrema(cy, s.Y1, s.Y2, s.Y)))
                        Add(Cubic(cx, s.X1, s.X2, s.X, t), Cubic(cy, s.Y1, s.Y2, s.Y, t));
                    Add(s.X, s.Y);
                    break;
                default: continue;
            }
            cx = s.X; cy = s.Y;
        }
        return any ? new Box(minX, minY, maxX - minX, maxY - minY) : null;
    }

    static double Cubic(double p0, double p1, double p2, double p3, double t)
    {
        var u = 1 - t;
        return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
    }

    // Valori di t in (0,1) dove la derivata della cubica si annulla.
    static IEnumerable<double> Extrema(double p0, double p1, double p2, double p3)
    {
        double d0 = p1 - p0, d1 = p2 - p1, d2 = p3 - p2;
        double a = d0 - 2 * d1 + d2, b = 2 * (d1 - d0), c = d0;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12 && -c / b is > 0 and < 1) yield return -c / b;
            yield break;
        }
        var disc = b * b - 4 * a * c;
        if (disc < 0) yield break;
        var sq = Math.Sqrt(disc);
        foreach (var t in new[] { (-b + sq) / (2 * a), (-b - sq) / (2 * a) })
            if (t is > 0 and < 1) yield return t;
    }

    sealed class Scanner(string d)
    {
        int _i;

        public bool End => _i >= d.Length;
        public bool AtLetter => char.IsAsciiLetter(d[_i]);

        public void SkipSeparators()
        {
            while (_i < d.Length && (char.IsWhiteSpace(d[_i]) || d[_i] == ',')) _i++;
        }

        public char ReadLetter() => d[_i++];

        public CommandException Error(string msg) => new($"path 'd': {msg} (posizione {_i}).");

        public double Number()
        {
            SkipSeparators();
            var start = _i;
            if (_i < d.Length && d[_i] is '+' or '-') _i++;
            var digits = false;
            while (_i < d.Length && char.IsAsciiDigit(d[_i])) { _i++; digits = true; }
            if (_i < d.Length && d[_i] == '.')
            {
                _i++;
                while (_i < d.Length && char.IsAsciiDigit(d[_i])) { _i++; digits = true; }
            }
            if (!digits) { _i = start; throw Error("numero atteso"); }
            if (_i < d.Length && d[_i] is 'e' or 'E')
            {
                var save = _i++;
                if (_i < d.Length && d[_i] is '+' or '-') _i++;
                var expDigits = false;
                while (_i < d.Length && char.IsAsciiDigit(d[_i])) { _i++; expDigits = true; }
                if (!expDigits) _i = save;
            }
            return double.Parse(d.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // I flag degli archi sono un solo carattere e possono essere attaccati al numero successivo ("a1 1 0 011 1").
        public bool Flag()
        {
            SkipSeparators();
            if (_i < d.Length && d[_i] is '0' or '1') return d[_i++] == '1';
            throw Error("flag dell'arco atteso (0 o 1)");
        }
    }
}
