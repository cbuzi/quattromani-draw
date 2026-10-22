using System.Numerics;
using System.Text.Json.Serialization;

namespace Quattromani.Core;

public sealed record TextStyle(string Family, double Size, bool Bold, bool Italic, string Align, double LineHeight);

/// <summary>Una riga già impaginata: X è lo scostamento orizzontale, Y la linea di base, entrambi dall'angolo in alto a sinistra del testo.</summary>
public sealed record TextLine(string Text, double X, double Y);

public sealed record TextLayout(List<TextLine> Lines, double Width, double Height);

/// <summary>
/// Misura e contorni dei glifi, forniti dall'app (che ha i font). Core non dipende da nessuna libreria grafica:
/// i comandi chiamano il motore quando impaginano un testo, e il risultato (righe e ingombro) resta nel documento.
/// </summary>
public interface ITextEngine
{
    /// <summary>Famiglie di font disponibili.</summary>
    IReadOnlyList<string> Families { get; }
    string DefaultFamily { get; }
    /// <param name="wrapWidth">Larghezza oltre cui andare a capo; null = nessun a capo automatico.</param>
    TextLayout Layout(TextStyle style, string text, double? wrapWidth);
    /// <summary>Contorni dei glifi delle righe, in coordinate del layout (stesse di <see cref="TextLine"/>).</summary>
    List<PathSeg> Outline(TextStyle style, IReadOnlyList<TextLine> lines);
    /// <summary>Un glifo per carattere di una sola riga (senza a capo): serve a disporre il testo su un tracciato.</summary>
    List<GlyphShape> Glyphs(TextStyle style, string text);
}

/// <summary>
/// Testo. Il colore è il Fill (e Stroke/StrokeWidth un eventuale contorno). Le righe sono impaginate al momento della
/// creazione o della modifica; come per i path, (X, Y, Width, Height) è il riquadro su cui il testo impaginato (Source)
/// viene stirato, quindi ridimensionare con le maniglie stira il testo.
/// </summary>
public sealed class TextObject : SceneObject
{
    public override string Type => "text";

    public string Text { get; internal set; } = "";
    public string FontFamily { get; internal set; } = "";
    public double FontSize { get; internal set; } = 24;
    public bool Bold { get; internal set; }
    public bool Italic { get; internal set; }
    /// <summary>"left", "center" o "right".</summary>
    public string Align { get; internal set; } = "left";
    /// <summary>Moltiplicatore dell'interlinea (1 = quella del font).</summary>
    public double LineHeight { get; internal set; } = 1;
    /// <summary>Larghezza per l'a capo automatico (testo "a paragrafo"); null = testo "artistico", una riga per ogni a capo.</summary>
    public double? WrapWidth { get; internal set; }

    public List<TextLine> Lines { get; internal set; } = [];
    /// <summary>Se non è null il testo segue un tracciato: i glifi sono in <see cref="PathGlyphs"/> (ricalcolati dal documento) e <see cref="Lines"/> non si usa.</summary>
    public TextOnPath? OnPath { get; internal set; }
    public List<PathSeg>? PathGlyphs { get; internal set; }
    internal string? PathKey { get; set; }
    public Box Source { get; internal set; }
    public Matrix3x2 Fit => FitMatrix(Source, new(X, Y, Width, Height));

    public TextStyle Style => new(FontFamily, FontSize, Bold, Italic, Align, LineHeight);

    /// <summary>Reimpagina il testo tenendo fermo l'angolo in alto a sinistra; un eventuale stiramento viene azzerato.</summary>
    internal void Relayout(Document doc)
    {
        var engine = doc.TextEngine ?? throw new CommandException("Il motore dei font non e' disponibile: il testo non si puo' impaginare.");
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
                    $"Il font '{family}' non e' disponibile su questo computer. Chiama list_fonts per l'elenco (es. {string.Join(", ", engine.Families.Take(5))}).");
        }
        if (size is { } s && (s <= 0 || s > 2000 || double.IsNaN(s))) throw new CommandException("fontSize deve essere tra 0 e 2000.");
        if (align is not (null or "left" or "center" or "right")) throw new CommandException($"align '{align}' non valido. Valori: \"left\", \"center\", \"right\".");
        if (lineHeight is { } l && (l < 0.3 || l > 5)) throw new CommandException("lineHeight (moltiplicatore dell'interlinea) deve essere tra 0.3 e 5.");
        if (wrap is { } w && (w < 0 || double.IsNaN(w))) throw new CommandException("width (larghezza per l'a capo) non puo' essere negativa.");
    }
}

/// <summary>Modifica il contenuto o lo stile di testi esistenti; i campi omessi restano invariati. Reimpagina il testo.</summary>
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
    /// <summary>Larghezza per l'a capo automatico; 0 lo toglie.</summary>
    public double? Width { get; set; }

    record Snapshot(TextObject Obj, string Text, string Family, double Size, bool Bold, bool Italic, string Align, double LineHeight,
        double? Wrap, List<TextLine> Lines, Box Source, Box Rect);

    List<Snapshot> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(s => s.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("text.set: 'targets' e' vuoto. Passa id o nomi di testi, es. [\"o1\"].");
        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        if (objs.FirstOrDefault(o => o is not TextObject) is { } other)
            throw new CommandException($"text.set: '{other.Name ?? other.Id}' e' un {other.Type}, non un testo.");
        if (Text is { Length: 0 }) throw new CommandException("text.set: 'text' non puo' essere vuoto (per eliminare il testo usa object.delete).");
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
