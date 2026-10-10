using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Quattromani.Core;

/// <param name="Position">0..1 lungo la linea del gradiente (o dal centro verso il raggio, per i radiali).</param>
public sealed record GradientStop(string Color, double Position);

/// <summary>
/// Gradiente in stile CSS, relativo al riquadro dell'oggetto. Lineare: <c>Angle</c> in gradi come in CSS (0 = verso l'alto,
/// 90 = verso destra, 180 = verso il basso). Radiale: centro in frazioni del riquadro, raggio fino all'angolo più lontano.
/// </summary>
/// <remarks>
/// Linear: di default la linea è definita dall'angolo (stile CSS); con X1,Y1,X2,Y2 (frazioni del riquadro, anche fuori da 0..1)
/// ha punti espliciti, che è quello che si trascina con lo strumento di riempimento. Radial: di default il raggio arriva
/// all'angolo più lontano; con Rx,Ry (frazioni di larghezza e altezza) l'ellisse è esplicita.
/// </remarks>
public sealed record GradientSpec(bool Radial, double Angle, bool Circle, double CenterX, double CenterY, List<GradientStop> Stops,
    double? X1 = null, double? Y1 = null, double? X2 = null, double? Y2 = null, double? Rx = null, double? Ry = null)
{
    public bool HasLinearPoints => X1 != null;
}

public static partial class Gradient
{
    public static bool IsGradient(string s) =>
        s.StartsWith("linear-gradient(", StringComparison.Ordinal) || s.StartsWith("radial-gradient(", StringComparison.Ordinal);

    [GeneratedRegex(@"^(#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?)(?:\s+(-?\d+(?:\.\d+)?)%)?$")]
    private static partial Regex StopRegex();

    [GeneratedRegex(@"^(-?\d+(?:\.\d+)?)(?:deg)?$")]
    private static partial Regex AngleRegex();

    [GeneratedRegex(@"^(?:(circle|ellipse)\s*)?(?:(\d+(?:\.\d+)?)%\s+(\d+(?:\.\d+)?)%\s*)?(?:at\s+(-?\d+(?:\.\d+)?)%\s+(-?\d+(?:\.\d+)?)%)?$")]
    private static partial Regex RadialHeadRegex();

    [GeneratedRegex(@"^from\s+(-?\d+(?:\.\d+)?)%\s+(-?\d+(?:\.\d+)?)%\s+to\s+(-?\d+(?:\.\d+)?)%\s+(-?\d+(?:\.\d+)?)%$")]
    private static partial Regex LinearPointsRegex();

    const string Syntax =
        "Gradienti: \"linear-gradient(90deg, #FF0000, #0000FF)\" (angolo in gradi: 0 = verso l'alto, 90 = destra, 180 = basso; oppure \"to right\", \"to bottom\"...) " +
        "e \"radial-gradient(#FFFFFF, #000000)\" (opzionale \"circle\" o \"ellipse\", raggi \"60% 40%\" e centro \"at 30% 40%\"); i colori possono avere una posizione, es. \"#FF0000 25%\". " +
        "Per punti espliciti: \"linear-gradient(from 10% 20% to 90% 80%, ...)\" (percentuali del riquadro dell'oggetto).";

    static CommandException Error(string field, string message) => new($"'{field}': {message} {Syntax}");

    /// <summary>Interpreta un gradiente; se non è valido solleva un errore che spiega la sintassi.</summary>
    public static GradientSpec Parse(string value, string field)
    {
        var s = value.Trim();
        var radial = s.StartsWith("radial-gradient(", StringComparison.Ordinal);
        if (!IsGradient(s) || !s.EndsWith(')')) throw Error(field, $"gradiente '{value}' non valido.");
        var inner = s[(s.IndexOf('(') + 1)..^1];
        var parts = inner.Split(',').Select(p => p.Trim()).ToList();

        double angle = 180; // come in CSS: dall'alto verso il basso
        bool circle = false;
        double cx = 0.5, cy = 0.5;
        double? x1 = null, y1 = null, x2 = null, y2 = null, rx = null, ry = null;

        if (!radial && parts.Count > 0 && !parts[0].StartsWith('#'))
        {
            var head = parts[0].ToLowerInvariant();
            if (LinearPointsRegex().Match(head) is { Success: true } pts)
                (x1, y1, x2, y2) = (P(pts, 1), P(pts, 2), P(pts, 3), P(pts, 4));
            else if (AngleRegex().Match(head) is { Success: true } a) angle = double.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (head.StartsWith("to ")) angle = DirectionAngle(head[3..].Trim()) ?? throw Error(field, $"direzione '{parts[0]}' non valida (usa to top, to right, to bottom, to left, o con due lati: to top right).");
            else throw Error(field, $"'{parts[0]}' non e' un angolo (es. 90deg) ne' una direzione (es. to right).");
            parts.RemoveAt(0);
        }
        else if (radial && parts.Count > 0 && !parts[0].StartsWith('#'))
        {
            var m = RadialHeadRegex().Match(parts[0].ToLowerInvariant());
            if (!m.Success) throw Error(field, $"'{parts[0]}' non e' valido: dopo 'radial-gradient(' vanno forma e centro (es. \"circle at 30% 40%\") oppure subito i colori.");
            circle = m.Groups[1].Value == "circle";
            if (m.Groups[2].Success)
            {
                if (circle) throw Error(field, "un cerchio non ha due raggi: usa \"ellipse 60% 40% at ...\" oppure togli i raggi.");
                rx = P(m, 2) ; ry = P(m, 3);
                if (rx <= 0 || ry <= 0) throw Error(field, "i raggi devono essere maggiori di 0.");
            }
            if (m.Groups[4].Success) { cx = P(m, 4); cy = P(m, 5); }
            parts.RemoveAt(0);
        }

        if (parts.Count < 2) throw Error(field, "servono almeno due colori.");
        var colors = new List<string>();
        var positions = new List<double?>();
        foreach (var p in parts)
        {
            var m = StopRegex().Match(p);
            if (!m.Success) throw Error(field, $"colore '{p}' non valido: usa #RRGGBB (o #RRGGBBAA) seguito da una posizione opzionale in %.");
            colors.Add(m.Groups[1].Value.ToUpperInvariant());
            positions.Add(m.Groups[2].Success ? Math.Clamp(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 100, 0, 1) : null);
        }

        // Posizioni mancanti come in CSS: la prima 0, l'ultima 100%, le altre distribuite tra le vicine; mai decrescenti.
        positions[0] ??= 0;
        positions[^1] ??= 1;
        for (var i = 1; i < positions.Count; i++)
            if (positions[i] is { } cur && positions[i - 1] is { } prev && cur < prev) positions[i] = prev;
        for (var i = 1; i < positions.Count - 1; i++)
        {
            if (positions[i] != null) continue;
            var j = i;
            while (positions[j] == null) j++;
            var from = positions[i - 1]!.Value; var to = positions[j]!.Value;
            for (var k = i; k < j; k++) positions[k] = from + (to - from) * (k - i + 1) / (j - i + 1);
            i = j;
        }

        return new GradientSpec(radial, radial ? 0 : angle, circle, cx, cy,
            colors.Select((c, i) => new GradientStop(c, positions[i]!.Value)).ToList(), x1, y1, x2, y2, rx, ry);
    }

    // Percentuale di un gruppo della regex come frazione (50% -> 0.5).
    static double P(Match m, int group) => double.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) / 100;

    public static bool TryParse(string value, out GradientSpec spec)
    {
        try { spec = Parse(value, "fill"); return true; }
        catch (CommandException) { spec = null!; return false; }
    }

    static double? DirectionAngle(string d) => string.Join(' ', d.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order()) switch
    {
        "top" => 0, "right" => 90, "bottom" => 180, "left" => 270,
        "right top" => 45, "bottom right" => 135, "bottom left" => 225, "left top" => 315,
        _ => null,
    };

    /// <summary>Estremi della linea di un gradiente lineare, come frazioni del riquadro (espliciti o ricavati dall'angolo, come CSS).</summary>
    public static (double X1, double Y1, double X2, double Y2) LinearPoints(GradientSpec g, Box b)
    {
        if (g.HasLinearPoints) return (g.X1!.Value, g.Y1!.Value, g.X2!.Value, g.Y2!.Value);
        if (b.Width <= 0 || b.Height <= 0) return (0, 0, 1, 1);
        // La linea passa dal centro e la sua lunghezza fa toccare gli angoli a 0% e 100%.
        double th = g.Angle * Math.PI / 180, ux = Math.Sin(th), uy = -Math.Cos(th);
        var half = (Math.Abs(b.Width * ux) + Math.Abs(b.Height * uy)) / 2;
        double dx = ux * half / b.Width, dy = uy * half / b.Height;
        return (0.5 - dx, 0.5 - dy, 0.5 + dx, 0.5 + dy);
    }

    /// <summary>Raggi di un gradiente radiale, come frazioni di larghezza e altezza (espliciti o fino all'angolo più lontano).</summary>
    public static (double Rx, double Ry) RadialRadii(GradientSpec g, Box b)
    {
        if (g.Rx is { } rx && g.Ry is { } ry) return (rx, ry);
        double fx = Math.Max(g.CenterX, 1 - g.CenterX), fy = Math.Max(g.CenterY, 1 - g.CenterY);
        if (!g.Circle) return (fx * Math.Sqrt(2), fy * Math.Sqrt(2));
        if (b.Width <= 0 || b.Height <= 0) return (fx * Math.Sqrt(2), fy * Math.Sqrt(2));
        var r = Math.Sqrt(fx * b.Width * fx * b.Width + fy * b.Height * fy * b.Height);
        return (r / b.Width, r / b.Height);
    }

    /// <summary>Colore del gradiente alla posizione t (0..1), interpolando tra le soste vicine; "#RRGGBB" o "#RRGGBBAA" se c'è trasparenza.</summary>
    public static string ColorAt(IReadOnlyList<GradientStop> stops, double t)
    {
        static (int R, int G, int B, int A) C(string c)
        {
            var v = Convert.ToUInt32(c[1..], 16);
            return c.Length == 7 ? ((int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255, 255)
                                 : ((int)(v >> 24) & 255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255);
        }

        var lo = stops.LastOrDefault(s => s.Position <= t) ?? stops[0];
        var hi = stops.FirstOrDefault(s => s.Position >= t) ?? stops[^1];
        var k = hi.Position - lo.Position < 1e-9 ? 0 : Math.Clamp((t - lo.Position) / (hi.Position - lo.Position), 0, 1);
        var (a, b) = (C(lo.Color), C(hi.Color));
        int L(int x, int y) => (int)Math.Round(x + (y - x) * k);
        var (r, g, bl, al) = (L(a.R, b.R), L(a.G, b.G), L(a.B, b.B), L(a.A, b.A));
        return al == 255 ? $"#{r:X2}{g:X2}{bl:X2}" : $"#{r:X2}{g:X2}{bl:X2}{al:X2}";
    }

    /// <summary>Forma canonica, con tutte le posizioni esplicite.</summary>
    public static string Format(GradientSpec g)
    {
        static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(g.Radial ? "radial-gradient(" : "linear-gradient(");
        if (g.Radial)
            sb.Append(g.Circle ? "circle" : "ellipse")
              .Append(g.Rx is { } rx && g.Ry is { } ry ? $" {N(rx * 100)}% {N(ry * 100)}%" : "")
              .Append($" at {N(g.CenterX * 100)}% {N(g.CenterY * 100)}%");
        else if (g.HasLinearPoints)
            sb.Append($"from {N(g.X1!.Value * 100)}% {N(g.Y1!.Value * 100)}% to {N(g.X2!.Value * 100)}% {N(g.Y2!.Value * 100)}%");
        else sb.Append($"{N(g.Angle)}deg");
        foreach (var s in g.Stops) sb.Append(", ").Append(s.Color).Append(' ').Append(N(s.Position * 100)).Append('%');
        return sb.Append(')').ToString();
    }
}
