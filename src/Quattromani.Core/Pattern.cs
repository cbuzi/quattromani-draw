using System.Globalization;
using System.Text.RegularExpressions;

namespace Quattromani.Core;

/// <summary>
/// Pattern fill: <c>pattern(stripes, #FF0000, #FFFFFF, 8, 45)</c> = kind, pattern color, background color,
/// step in document units and optional angle. The pattern starts at the object's top-left corner and follows it.
/// </summary>
public sealed record PatternSpec(string Kind, string Fg, string Bg, double Size, double Angle);

public static partial class Pattern
{
    /// <summary>stripes, dots (polka dots), checker (checkerboard), grid.</summary>
    public static readonly string[] Kinds = ["stripes", "dots", "checker", "grid"];

    public static bool IsPattern(string s) => s.StartsWith("pattern(", StringComparison.Ordinal);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?$")]
    private static partial Regex ColorRegex();

    const string Syntax =
        "Patterns: \"pattern(stripes, #FF0000, #FFFFFF, 8)\" = kind (stripes, dots, checker, grid), pattern color, background color, " +
        "step in document units and optional angle in degrees (e.g. \"pattern(dots, #1E3A8A, #DBEAFE, 12, 30)\").";

    static CommandException Error(string field, string message) => new($"'{field}': {message} {Syntax}");

    public static PatternSpec Parse(string value, string field)
    {
        var s = value.Trim();
        if (!IsPattern(s) || !s.EndsWith(')')) throw Error(field, $"pattern '{value}' is not valid.");
        var parts = s[(s.IndexOf('(') + 1)..^1].Split(',').Select(p => p.Trim()).ToList();
        if (parts.Count is < 4 or > 5) throw Error(field, $"4 or 5 values are needed (found {parts.Count}).");

        var kind = parts[0].ToLowerInvariant();
        if (!Kinds.Contains(kind)) throw Error(field, $"unknown type '{parts[0]}' (values: {string.Join(", ", Kinds)}).");
        foreach (var c in parts.Skip(1).Take(2))
            if (!ColorRegex().IsMatch(c)) throw Error(field, $"color '{c}' is not valid: use #RRGGBB or #RRGGBBAA (with alpha 00 the background is transparent).");
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || size <= 0 || size > 1000)
            throw Error(field, $"step '{parts[3]}' is not valid: a number between 0 and 1000.");
        var angle = 0.0;
        if (parts.Count == 5 && (!double.TryParse(parts[4].Replace("deg", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out angle) || double.IsNaN(angle)))
            throw Error(field, $"angle '{parts[4]}' is not valid.");
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
