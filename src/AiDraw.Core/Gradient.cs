using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiDraw.Core;

/// <param name="Position">0..1 lungo la linea del gradiente.</param>
public sealed record GradientStop(string Color, double Position);

/// <summary>
/// Gradiente in stile CSS, relativo al riquadro dell'oggetto. Lineare: <c>Angle</c> in gradi come in CSS (0 = verso l'alto,
/// 90 = verso destra, 180 = verso il basso). Radiale: centro in frazioni del riquadro, raggio fino all'angolo più lontano.
/// </summary>
public sealed record GradientSpec(bool Radial, double Angle, bool Circle, double CenterX, double CenterY, List<GradientStop> Stops);

public static partial class Gradient
{
    public static bool IsGradient(string s) =>
        s.StartsWith("linear-gradient(", StringComparison.Ordinal) || s.StartsWith("radial-gradient(", StringComparison.Ordinal);

    [GeneratedRegex(@"^(#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?)(?:\s+(-?\d+(?:\.\d+)?)%)?$")]
    private static partial Regex StopRegex();

    [GeneratedRegex(@"^(-?\d+(?:\.\d+)?)(?:deg)?$")]
    private static partial Regex AngleRegex();

    [GeneratedRegex(@"^(?:(circle|ellipse)\s*)?(?:at\s+(\d+(?:\.\d+)?)%\s+(\d+(?:\.\d+)?)%)?$")]
    private static partial Regex RadialHeadRegex();

    const string Syntax =
        "Gradienti: \"linear-gradient(90deg, #FF0000, #0000FF)\" (angolo in gradi: 0 = verso l'alto, 90 = destra, 180 = basso; oppure \"to right\", \"to bottom\"...) " +
        "e \"radial-gradient(#FFFFFF, #000000)\" (opzionale \"circle\" o \"ellipse\" e \"at 30% 40%\"); i colori possono avere una posizione, es. \"#FF0000 25%\".";

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

        if (!radial && parts.Count > 0 && !parts[0].StartsWith('#'))
        {
            var head = parts[0].ToLowerInvariant();
            if (AngleRegex().Match(head) is { Success: true } a) angle = double.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture);
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
                cx = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 100;
                cy = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 100;
            }
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
            colors.Select((c, i) => new GradientStop(c, positions[i]!.Value)).ToList());
    }

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

    /// <summary>Forma canonica, con tutte le posizioni esplicite.</summary>
    public static string Format(GradientSpec g)
    {
        static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(g.Radial ? "radial-gradient(" : "linear-gradient(");
        sb.Append(g.Radial ? $"{(g.Circle ? "circle" : "ellipse")} at {N(g.CenterX * 100)}% {N(g.CenterY * 100)}%" : $"{N(g.Angle)}deg");
        foreach (var s in g.Stops) sb.Append(", ").Append(s.Color).Append(' ').Append(N(s.Position * 100)).Append('%');
        return sb.Append(')').ToString();
    }
}
