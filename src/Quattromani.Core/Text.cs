using System.Numerics;
using System.Text.Json.Serialization;

namespace Quattromani.Core;

public sealed record TextStyle(string Family, double Size, bool Bold, bool Italic, string Align, double LineHeight);

/// <summary>An already laid-out line: X is the horizontal offset, Y the baseline, both from the top-left corner of the text.</summary>
public sealed record TextLine(string Text, double X, double Y);

public sealed record TextLayout(List<TextLine> Lines, double Width, double Height);

/// <summary>
/// Measurement and glyph outlines, provided by the app (which has the fonts). Core does not depend on any graphics library:
/// commands call the engine when they lay out a text, and the result (lines and extent) stays in the document.
/// </summary>
public interface ITextEngine
{
    /// <summary>Available font families.</summary>
    IReadOnlyList<string> Families { get; }
    string DefaultFamily { get; }
    /// <param name="wrapWidth">Width beyond which to wrap; null = no automatic wrapping.</param>
    TextLayout Layout(TextStyle style, string text, double? wrapWidth);
    /// <summary>Glyph outlines of the lines, in layout coordinates (the same as <see cref="TextLine"/>).</summary>
    List<PathSeg> Outline(TextStyle style, IReadOnlyList<TextLine> lines);
    /// <summary>One glyph per character of a single line (no line breaks): used to lay out text on a path.</summary>
    List<GlyphShape> Glyphs(TextStyle style, string text);
}

/// <summary>
/// Text. The color is the Fill (and Stroke/StrokeWidth an optional outline). Lines are laid out when created or
/// edited; as with paths, (X, Y, Width, Height) is the box the laid-out text (Source)
/// is stretched onto, so resizing with the handles stretches the text.
/// </summary>
public sealed class TextObject : SceneObject
{
    public override string Type => "text";

    public string Text { get; internal set; } = "";
    public string FontFamily { get; internal set; } = "";
    public double FontSize { get; internal set; } = 24;
    public bool Bold { get; internal set; }
    public bool Italic { get; internal set; }
    /// <summary>"left", "center" or "right".</summary>
    public string Align { get; internal set; } = "left";
    /// <summary>Line spacing multiplier (1 = the font's own).</summary>
    public double LineHeight { get; internal set; } = 1;
    /// <summary>Width for automatic wrapping ("paragraph" text); null = "artistic" text, one line per line break.</summary>
    public double? WrapWidth { get; internal set; }

    public List<TextLine> Lines { get; internal set; } = [];
    /// <summary>If not null the text follows a path: the glyphs are in <see cref="PathGlyphs"/> (recomputed by the document) and <see cref="Lines"/> is not used.</summary>
    public TextOnPath? OnPath { get; internal set; }
    public List<PathSeg>? PathGlyphs { get; internal set; }
    internal string? PathKey { get; set; }
    public Box Source { get; internal set; }
    public Matrix3x2 Fit => FitMatrix(Source, new(X, Y, Width, Height));

    public TextStyle Style => new(FontFamily, FontSize, Bold, Italic, Align, LineHeight);

    /// <summary>Lays the text out again keeping the top-left corner fixed; any stretching is reset.</summary>
    internal void Relayout(Document doc)
    {
        var engine = doc.TextEngine ?? throw new CommandException("The font engine is not available: the text cannot be laid out.");
        var layout = engine.Layout(Style, Text, WrapWidth);
        Lines = layout.Lines;
        Source = new Box(X, Y, layout.Width, layout.Height);
        Width = layout.Width;
        Height = layout.Height;
    }

    internal static void Validate(Document doc, string? family, double? size, string? align, double? lineHeight, double? wrap)
    {
        if (family != null)
        {
            var engine = doc.TextEngine;
            if (engine != null && !engine.Families.Contains(family, StringComparer.OrdinalIgnoreCase))
                throw new CommandException(
                    $"The font '{family}' is not available on this computer. Call list_fonts for the list (e.g. {string.Join(", ", engine.Families.Take(5))}).");
        }
        if (size is { } s && (s <= 0 || s > 2000 || double.IsNaN(s))) throw new CommandException("fontSize must be between 0 and 2000.");
        if (align is not (null or "left" or "center" or "right")) throw new CommandException($"align '{align}' is not valid. Values: \"left\", \"center\", \"right\".");
        if (lineHeight is { } l && (l < 0.3 || l > 5)) throw new CommandException("lineHeight (line spacing multiplier) must be between 0.3 and 5.");
        if (wrap is { } w && (w < 0 || double.IsNaN(w))) throw new CommandException("width (wrapping width) cannot be negative.");
    }
}

/// <summary>Changes the content or style of existing texts; omitted fields stay unchanged. Lays the text out again.</summary>
public sealed class SetText : Command
{
    public List<string> Targets { get; set; } = [];
    public string? Text { get; set; }
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public string? Align { get; set; }
    public double? LineHeight { get; set; }
    /// <summary>Width for automatic wrapping; 0 removes it.</summary>
    public double? Width { get; set; }

    record Snapshot(TextObject Obj, string Text, string Family, double Size, bool Bold, bool Italic, string Align, double LineHeight,
        double? Wrap, List<TextLine> Lines, Box Source, Box Rect);

    List<Snapshot> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(s => s.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("text.set: 'targets' is empty. Pass the ids or names of texts, e.g. [\"o1\"].");
        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        if (objs.FirstOrDefault(o => o is not TextObject) is { } other)
            throw new CommandException($"text.set: '{other.Name ?? other.Id}' is a {other.Type}, not a text.");
        if (Text is { Length: 0 }) throw new CommandException("text.set: 'text' cannot be empty (to remove the text use object.delete).");
        TextObject.Validate(doc, FontFamily, FontSize, Align, LineHeight, Width);

        _old = objs.Cast<TextObject>().Select(t => new Snapshot(t, t.Text, t.FontFamily, t.FontSize, t.Bold, t.Italic, t.Align,
            t.LineHeight, t.WrapWidth, t.Lines, t.Source, new Box(t.X, t.Y, t.Width, t.Height))).ToList();
        foreach (var s in _old)
        {
            var t = s.Obj;
            if (Text != null) t.Text = Text;
            if (FontFamily != null) t.FontFamily = doc.TextEngine?.Families.First(f => f.Equals(FontFamily, StringComparison.OrdinalIgnoreCase)) ?? FontFamily;
            if (FontSize != null) t.FontSize = FontSize.Value;
            if (Bold != null) t.Bold = Bold.Value;
            if (Italic != null) t.Italic = Italic.Value;
            if (Align != null) t.Align = Align;
            if (LineHeight != null) t.LineHeight = LineHeight.Value;
            if (Width != null) t.WrapWidth = Width.Value > 0 ? Width.Value : null;
            t.Relayout(doc);
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var s in _old)
        {
            var t = s.Obj;
            (t.Text, t.FontFamily, t.FontSize, t.Bold, t.Italic, t.Align, t.LineHeight, t.WrapWidth) =
                (s.Text, s.Family, s.Size, s.Bold, s.Italic, s.Align, s.LineHeight, s.Wrap);
            (t.Lines, t.Source) = (s.Lines, s.Source);
            (t.X, t.Y, t.Width, t.Height) = (s.Rect.X, s.Rect.Y, s.Rect.Width, s.Rect.Height);
        }
    }
}
