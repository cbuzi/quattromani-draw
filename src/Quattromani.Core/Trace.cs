using System.Text.Json.Serialization;

namespace Quattromani.Core;

/// <summary>
/// Vectorizes a pixel mask: outlines along pixel edges (holes have the opposite direction, so the "nonzero" fill
/// leaves them empty), then bezier curves fitted to the outlines (least-squares fit, with sharp corners where the outline turns abruptly).
/// </summary>
public static class ImageTrace
{
    readonly record struct V(double X, double Y)
    {
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y);
        public static V operator *(V a, double k) => new(a.X * k, a.Y * k);
        public double Dot(V b) => X * b.X + Y * b.Y;
        public double Len => Math.Sqrt(X * X + Y * Y);
        public V Unit() => Len < 1e-12 ? this : this * (1 / Len);
    }

    static readonly (int Dx, int Dy)[] Dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)]; // E, S, W, N

    /// <param name="mask">w*h values, true = to fill.</param>
    /// <param name="minArea">Outlines (and holes) smaller than this, in pixel², are discarded.</param>
    /// <param name="tolerance">Maximum error of the curves with respect to the outline, in pixels.</param>
    /// <returns>M/C/Z segments in pixel coordinates (0..w, 0..h); empty if there is nothing.</returns>
    public static List<PathSeg> Trace(bool[] mask, int w, int h, double minArea, double tolerance)
    {
        var result = new List<PathSeg>();
        int stride = w + 1;
        var visited = new byte[stride * (h + 1)];

        bool Fg(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x];

        // The edge starting at corner (x, y) in direction d: keeps the filled area on its left.
        bool Edge(int x, int y, int d) => d switch
        {
            0 => Fg(x, y - 1) && !Fg(x, y),         // E: bottom side of the pixel at the top right
            1 => Fg(x, y) && !Fg(x - 1, y),         // S: left side of the pixel at the bottom right
            2 => Fg(x - 1, y) && !Fg(x - 1, y - 1), // W: top side of the pixel at the bottom left
            _ => Fg(x - 1, y - 1) && !Fg(x, y - 1), // N: right side of the pixel at the top left
        };

        for (var y = 0; y <= h; y++)
            for (var x = 0; x <= w; x++)
                for (var d0 = 0; d0 < 4; d0++)
                {
                    if ((visited[y * stride + x] & (1 << d0)) != 0 || !Edge(x, y, d0)) continue;

                    // Walks the outline one step at a time; at saddle points (two exits) it turns right.
                    var ring = new List<V>();
                    int cx = x, cy = y, d = d0;
                    double area = 0;
                    while (true)
                    {
                        visited[cy * stride + cx] |= (byte)(1 << d);
                        int nx = cx + Dirs[d].Dx, ny = cy + Dirs[d].Dy;
                        // Midpoint of the step: smooths the pixel staircase.
                        ring.Add(new((cx + nx) / 2.0, (cy + ny) / 2.0));
                        area += (double)cx * ny - (double)nx * cy;
                        (cx, cy) = (nx, ny);
                        int r = (d + 1) % 4, l = (d + 3) % 4;
                        var next = Edge(cx, cy, r) ? r : Edge(cx, cy, d) ? d : Edge(cx, cy, l) ? l : -1;
                        if (next < 0 || (cx == x && cy == y && next == d0)) break;
                        d = next;
                    }

                    if (Math.Abs(area) / 2 < minArea || ring.Count < 8) continue;
                    Fit(ring, tolerance, result);
                }
        return result;
    }

    // ---- Point fitting ------------------------------------------------------------------

    const int Reach = 3; // how many points before and after to measure the curvature

    static void Fit(List<V> ring, double tolerance, List<PathSeg> output)
    {
        int n = ring.Count;
        V At(int i) => ring[((i % n) + n) % n];

        // Corners: where the outline turns abruptly (interior angle < 120°); among nearby candidates the sharpest wins.
        var interior = new double[n];
        for (var i = 0; i < n; i++)
        {
            V a = At(i - Reach) - At(i), b = At(i + Reach) - At(i);
            interior[i] = a.Len < 1e-9 || b.Len < 1e-9 ? 180 : Math.Acos(Math.Clamp(a.Dot(b) / (a.Len * b.Len), -1, 1)) * 180 / Math.PI;
        }
        var corners = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (interior[i] >= 120) continue;
            var best = true;
            for (var k = -Reach; k <= Reach && best; k++)
                if (k != 0 && (interior[((i + k) % n + n) % n] < interior[i] || (interior[((i + k) % n + n) % n] == interior[i] && k < 0))) best = false;
            if (best) corners.Add(i);
        }

        var err = Math.Max(0.1, tolerance);
        if (corners.Count == 0)
        {
            // Smooth outline: a single closed curve, with continuous tangent at the joint.
            var run = Enumerable.Range(0, n + 1).Select(At).ToList();
            var t = (At(Reach) - At(-Reach)).Unit();
            output.Add(new('M', X: run[0].X, Y: run[0].Y));
            FitCubic(run, 0, n, t, t * -1, err * err, output);
        }
        else
        {
            output.Add(new('M', X: At(corners[0]).X, Y: At(corners[0]).Y));
            for (var c = 0; c < corners.Count; c++)
            {
                int from = corners[c], to = corners[(c + 1) % corners.Count];
                if (to <= from) to += n;
                var run = Enumerable.Range(from, to - from + 1).Select(At).ToList();
                int m = Math.Min(Reach, run.Count - 1);
                FitCubic(run, 0, run.Count - 1, (run[m] - run[0]).Unit(), (run[^(m + 1)] - run[^1]).Unit(), err * err, output);
            }
        }
        output.Add(new('Z'));
    }

    /// <summary>
    /// Bezier curves through a hand-drawn polyline (open path): corners where the line turns sharply, smooth curves in between.
    /// <paramref name="tolerance"/> is the maximum distance from the points, in the points' own units. Returns M/L/C segments (no Z).
    /// </summary>
    public static List<PathSeg> FitOpen(IReadOnlyList<(double X, double Y)> points, double tolerance)
    {
        var d = new List<V>();
        foreach (var (x, y) in points)
        {
            var v = new V(x, y);
            if (d.Count == 0 || (v - d[^1]).Len > 1e-9) d.Add(v);
        }
        var output = new List<PathSeg>();
        if (d.Count < 2) return output;
        output.Add(new('M', X: d[0].X, Y: d[0].Y));
        if (d.Count == 2) { output.Add(new('L', X: d[1].X, Y: d[1].Y)); return output; }

        // Corners among the inner points (interior angle < 120°); among nearby candidates the sharpest wins.
        int n = d.Count;
        var interior = new double[n];
        for (var i = 1; i < n - 1; i++)
        {
            V a = d[Math.Max(0, i - Reach)] - d[i], b = d[Math.Min(n - 1, i + Reach)] - d[i];
            interior[i] = a.Len < 1e-9 || b.Len < 1e-9 ? 180 : Math.Acos(Math.Clamp(a.Dot(b) / (a.Len * b.Len), -1, 1)) * 180 / Math.PI;
        }
        var cuts = new List<int> { 0 };
        for (var i = 1; i < n - 1; i++)
        {
            if (interior[i] >= 120) continue;
            var best = true;
            for (var k = -Reach; k <= Reach && best; k++)
            {
                var j = i + k;
                if (k != 0 && j >= 1 && j < n - 1 && (interior[j] < interior[i] || (interior[j] == interior[i] && k < 0))) best = false;
            }
            if (best) cuts.Add(i);
        }
        cuts.Add(n - 1);

        var err = Math.Max(0.01, tolerance);
        for (var c = 0; c + 1 < cuts.Count; c++)
        {
            var run = d.GetRange(cuts[c], cuts[c + 1] - cuts[c] + 1);
            if (run.Count == 2) { output.Add(new('L', X: run[1].X, Y: run[1].Y)); continue; }
            int m = Math.Min(Reach, run.Count - 1);
            FitCubic(run, 0, run.Count - 1, (run[m] - run[0]).Unit(), (run[^(m + 1)] - run[^1]).Unit(), err * err, output);
        }
        return output;
    }

    // Schneider, "An Algorithm for Automatically Fitting Digitized Curves" (Graphics Gems, 1990).
    static void FitCubic(List<V> d, int first, int last, V t1, V t2, double errSq, List<PathSeg> output)
    {
        if (last - first == 1)
        {
            var dist = (d[last] - d[first]).Len / 3;
            Emit(d[first] + t1 * dist, d[last] + t2 * dist, d[last], output);
            return;
        }

        var u = ChordLength(d, first, last);
        var bez = Generate(d, first, last, u, t1, t2);
        var (maxErr, split) = MaxError(d, first, last, bez, u);
        if (maxErr < errSq) { Emit(bez[1], bez[2], bez[3], output); return; }

        if (maxErr < errSq * 16)
            for (var i = 0; i < 4; i++)
            {
                var u2 = u.Select((ui, k) => Reparameterize(bez, d[first + k], ui)).ToArray();
                bez = Generate(d, first, last, u2, t1, t2);
                (maxErr, split) = MaxError(d, first, last, bez, u2);
                if (maxErr < errSq) { Emit(bez[1], bez[2], bez[3], output); return; }
                u = u2;
            }

        // Split where the error is largest, with a common tangent at the cut point.
        split = Math.Clamp(split, first + 1, last - 1);
        var center = (d[split - 1] - d[split + 1]).Unit();
        FitCubic(d, first, split, t1, center, errSq, output);
        FitCubic(d, split, last, center * -1, t2, errSq, output);
    }

    static void Emit(V c1, V c2, V p, List<PathSeg> output) => output.Add(new('C', c1.X, c1.Y, c2.X, c2.Y, p.X, p.Y));

    static double[] ChordLength(List<V> d, int first, int last)
    {
        var u = new double[last - first + 1];
        for (var i = first + 1; i <= last; i++) u[i - first] = u[i - first - 1] + (d[i] - d[i - 1]).Len;
        var total = u[^1];
        for (var i = 1; i < u.Length; i++) u[i] = total > 0 ? u[i] / total : (double)i / (u.Length - 1);
        return u;
    }

    static V[] Generate(List<V> d, int first, int last, double[] u, V t1, V t2)
    {
        V p0 = d[first], p3 = d[last];
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;
        for (var i = 0; i < u.Length; i++)
        {
            double t = u[i], s = 1 - t;
            double b0 = s * s * s, b1 = 3 * s * s * t, b2 = 3 * s * t * t, b3 = t * t * t;
            V a1 = t1 * b1, a2 = t2 * b2;
            c00 += a1.Dot(a1); c01 += a1.Dot(a2); c11 += a2.Dot(a2);
            var tmp = d[first + i] - (p0 * (b0 + b1) + p3 * (b2 + b3));
            x0 += a1.Dot(tmp); x1 += a2.Dot(tmp);
        }
        double det = c00 * c11 - c01 * c01;
        double alpha1 = Math.Abs(det) > 1e-12 ? (x0 * c11 - x1 * c01) / det : 0;
        double alpha2 = Math.Abs(det) > 1e-12 ? (c00 * x1 - c01 * x0) / det : 0;
        var seg = (p3 - p0).Len;
        // If the handles are reversed or null, fall back to the heuristic of a third of the chord.
        if (alpha1 < seg * 1e-6 || alpha2 < seg * 1e-6) alpha1 = alpha2 = seg / 3;
        return [p0, p0 + t1 * alpha1, p3 + t2 * alpha2, p3];
    }

    static V Eval(V[] b, double t)
    {
        double s = 1 - t;
        return b[0] * (s * s * s) + b[1] * (3 * s * s * t) + b[2] * (3 * s * t * t) + b[3] * (t * t * t);
    }

    static double Reparameterize(V[] b, V p, double t)
    {
        V q = Eval(b, t) - p;
        double s = 1 - t;
        V d1 = (b[1] - b[0]) * (3 * s * s) + (b[2] - b[1]) * (6 * s * t) + (b[3] - b[2]) * (3 * t * t);
        V d2 = (b[2] - b[1] * 2 + b[0]) * (6 * s) + (b[3] - b[2] * 2 + b[1]) * (6 * t);
        double den = d1.Dot(d1) + q.Dot(d2);
        return Math.Abs(den) < 1e-12 ? t : Math.Clamp(t - q.Dot(d1) / den, 0, 1);
    }

    static (double Max, int Index) MaxError(List<V> d, int first, int last, V[] b, double[] u)
    {
        double max = 0;
        int index = (first + last) / 2;
        for (var i = first + 1; i < last; i++)
        {
            var e = (Eval(b, u[i - first]) - d[i]);
            var sq = e.Dot(e);
            if (sq >= max) { max = sq; index = i; }
        }
        return (max, index);
    }
}

/// <summary>
/// Vectorizes a black-and-white raster image: creates a path (all outlines, with holes) above the image.
/// The path has the same transform as the image, so it coincides with it; with keepOriginal false the image is removed.
/// </summary>
public sealed class TraceImage : Command
{
    public string Target { get; set; } = "";
    /// <summary>Brightness (0-255) below which a pixel is "filled". With invert the opposite applies.</summary>
    public int Threshold { get; set; } = 128;
    public bool Invert { get; set; }
    /// <summary>Specks and holes smaller than this (image pixels²) are ignored.</summary>
    public double MinArea { get; set; } = 4;
    /// <summary>Maximum error of the curves in image pixels: higher = fewer nodes and softer lines.</summary>
    public double Smooth { get; set; } = 1;
    public string? Fill { get; set; }
    public string? Name { get; set; }
    public bool KeepOriginal { get; set; } = true;

    const int MaxSide = 2048;

    PathObject? _obj;
    ImageObject? _image;
    List<SceneObject>? _container;
    int _index;

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _obj is null ? [] : [_obj.Id];
    [JsonIgnore] public override IEnumerable<string> AffectedIds => _obj is null ? [] : KeepOriginal ? [_obj.Id] : [_obj.Id, _image!.Id];

    public override void Apply(Document doc)
    {
        if (_obj == null)
        {
            if ((KeepOriginal ? doc.Find(Target) : doc.FindEditable(Target)) is not ImageObject img)
                throw new CommandException($"image.trace: '{Target}' is not an image. Pass the id or name of an imported image.");
            if (Threshold is < 1 or > 255) throw new CommandException("image.trace: 'threshold' must be between 1 and 255.");
            if (MinArea < 0 || !double.IsFinite(MinArea)) throw new CommandException("image.trace: 'minArea' must be >= 0.");
            if (!(Smooth >= 0.2 && Smooth <= 5)) throw new CommandException("image.trace: 'smooth' must be between 0.2 and 5.");
            var fill = Fill is null ? "#000000" : Document.ParseColor(Fill, "fill");
            if (fill is null) throw new CommandException("image.trace: 'fill' cannot be \"none\": the result would be invisible.");
            var decode = doc.ImagePixels ?? throw new CommandException("The image decoder is not available.");
            var px = decode(img.Data, MaxSide) ?? throw new CommandException("image.trace: the image cannot be read.");

            // Filled = opaque and dark (or light, with invert).
            var mask = new bool[px.Width * px.Height];
            for (var i = 0; i < mask.Length; i++)
            {
                int r = px.Rgba[i * 4], g = px.Rgba[i * 4 + 1], b = px.Rgba[i * 4 + 2], a = px.Rgba[i * 4 + 3];
                var dark = (299 * r + 587 * g + 114 * b) / 1000 < Threshold;
                mask[i] = a >= 128 && dark != Invert;
            }

            var segs = ImageTrace.Trace(mask, px.Width, px.Height, MinArea, Smooth);
            if (segs.Count == 0)
                throw new CommandException("image.trace: no shape found with this threshold. Try changing 'threshold' or using \"invert\":true.");

            // From pixels to the image's local space (which has its own transform).
            double sx = img.Width / px.Width, sy = img.Height / px.Height;
            segs = segs.Select(s => new PathSeg(s.Op, img.X + s.X1 * sx, img.Y + s.Y1 * sy, img.X + s.X2 * sx, img.Y + s.Y2 * sy, img.X + s.X * sx, img.Y + s.Y * sy)).ToList();
            var box = PathData.Bounds(segs)!.Value;
            _obj = new PathObject
            {
                Id = doc.NewId(), Name = Name, Segments = segs, Source = box, X = box.X, Y = box.Y, Width = box.Width, Height = box.Height,
                Transform = img.Transform, Fill = fill, StrokeWidth = 0,
            };
            _image = img;
            _container = doc.ContainerOf(img)!;
        }

        _index = _container!.IndexOf(_image!);
        _container.Insert(_index + 1, _obj);
        if (!KeepOriginal) _container.Remove(_image!);
    }

    public override void Undo(Document doc)
    {
        _container!.Remove(_obj!);
        if (!KeepOriginal) _container.Insert(_index, _image!);
    }
}
