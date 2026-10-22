using System.Globalization;

namespace Quattromani.Core;

/// <summary>Stroke dash, cap and join: parsing, validation and the presets the UI and the LLM use.</summary>
public static class StrokeStyle
{
    public static readonly string[] Caps = ["butt", "round", "square"];
    public static readonly string[] Joins = ["miter", "round", "bevel"];

    const string DashSyntax = "Use \"solid\", \"dashed\", \"dotted\", \"dashdot\" (all scaled to the stroke width) or numbers in px such as \"8 4\" or \"8 4 2 4\".";

    /// <summary>Dash pattern in px for a stroke of the given width; null = solid line.</summary>
    public static double[]? ParseDash(string spec, double strokeWidth)
    {
        var w = strokeWidth > 0 ? strokeWidth : 1;
        switch (spec.Trim().ToLowerInvariant())
        {
            case "solid" or "none" or "": return null;
            case "dashed": return [4 * w, 2 * w];
            case "dotted": return [w, 1.5 * w];
            case "dashdot": return [4 * w, 2 * w, w, 2 * w];
        }
        var parts = spec.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !(values[i] >= 0) || values[i] > 10000)
                throw new CommandException($"'dash': '{spec}' is not valid. {DashSyntax}");
        if (values.Length > 8 || values.Sum() <= 0) throw new CommandException($"'dash': '{spec}' is not valid (1 to 8 numbers, not all zero). {DashSyntax}");
        return values;
    }

    public static string CheckCap(string cap) =>
        Caps.Contains(cap) ? cap : throw new CommandException($"'cap': '{cap}' is not valid. Values: {string.Join(", ", Caps.Select(c => $"\"{c}\""))}.");

    public static string CheckJoin(string join) =>
        Joins.Contains(join) ? join : throw new CommandException($"'join': '{join}' is not valid. Values: {string.Join(", ", Joins.Select(c => $"\"{c}\""))}.");

    public static double CheckOpacity(double opacity) =>
        opacity is >= 0 and <= 1 ? opacity : throw new CommandException("'opacity' must be between 0 (invisible) and 1 (opaque).");
}
