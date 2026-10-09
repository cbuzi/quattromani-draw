using System.Globalization;
using System.Text.RegularExpressions;

namespace AiDraw.Core;

/// <summary>
/// Riempimento a motivo: <c>pattern(stripes, #FF0000, #FFFFFF, 8, 45)</c> = tipo, colore del motivo, colore dello sfondo,
/// passo in unità documento e angolo opzionale. Il motivo parte dall'angolo in alto a sinistra dell'oggetto e lo segue.
/// </summary>
public sealed record PatternSpec(string Kind, string Fg, string Bg, double Size, double Angle);

public static partial class Pattern
{
    /// <summary>stripes = righe, dots = pois, checker = scacchi, grid = griglia.</summary>
    public static readonly string[] Kinds = ["stripes", "dots", "checker", "grid"];

    public static bool IsPattern(string s) => s.StartsWith("pattern(", StringComparison.Ordinal);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?$")]
    private static partial Regex ColorRegex();

    const string Syntax =
        "Motivi: \"pattern(stripes, #FF0000, #FFFFFF, 8)\" = tipo (stripes, dots, checker, grid), colore del motivo, colore dello sfondo, " +
        "passo in unita' documento e angolo opzionale in gradi (es. \"pattern(dots, #1E3A8A, #DBEAFE, 12, 30)\").";

    static CommandException Error(string field, string message) => new($"'{field}': {message} {Syntax}");

    public static PatternSpec Parse(string value, string field)
    {
        var s = value.Trim();
        if (!IsPattern(s) || !s.EndsWith(')')) throw Error(field, $"motivo '{value}' non valido.");
        var parts = s[(s.IndexOf('(') + 1)..^1].Split(',').Select(p => p.Trim()).ToList();
        if (parts.Count is < 4 or > 5) throw Error(field, $"servono 4 o 5 valori (trovati {parts.Count}).");

        var kind = parts[0].ToLowerInvariant();
        if (!Kinds.Contains(kind)) throw Error(field, $"tipo '{parts[0]}' sconosciuto (valori: {string.Join(", ", Kinds)}).");
        foreach (var c in parts.Skip(1).Take(2))
            if (!ColorRegex().IsMatch(c)) throw Error(field, $"colore '{c}' non valido: usa #RRGGBB o #RRGGBBAA (con alfa 00 lo sfondo e' trasparente).");
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || size <= 0 || size > 1000)
            throw Error(field, $"passo '{parts[3]}' non valido: un numero tra 0 e 1000.");
        var angle = 0.0;
        if (parts.Count == 5 && (!double.TryParse(parts[4].Replace("deg", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out angle) || double.IsNaN(angle)))
            throw Error(field, $"angolo '{parts[4]}' non valido.");
        return new PatternSpec(kind, parts[1].ToUpperInvariant(), parts[2].ToUpperInvariant(), size, angle);
    }

    public static bool TryParse(string value, out PatternSpec spec)
    {
        try { spec = Parse(value, "fill"); return true; }
        catch (CommandException) { spec = null!; return false; }
    }

    public static string Format(PatternSpec p)
    {
        static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        return $"pattern({p.Kind}, {p.Fg}, {p.Bg}, {N(p.Size)}, {N(p.Angle)})";
    }
}
