using System.Text.Json.Serialization;

namespace Quattromani.Core;

/// <summary>Unità di misura: il documento lavora in "px" a 96 dpi (come CSS); le altre servono per mostrare e inserire misure.</summary>
public static class Units
{
    public static readonly string[] All = ["px", "mm", "cm", "in", "pt"];

    static double PerPx(string unit) => unit switch
    {
        "px" => 1,
        "mm" => 96 / 25.4,
        "cm" => 96 / 2.54,
        "in" => 96,
        "pt" => 96.0 / 72,
        _ => throw new CommandException($"unit '{unit}' non valida. Valori: {string.Join(", ", All.Select(u => $"\"{u}\""))}."),
    };

    /// <summary>Da un valore nell'unità indicata a px.</summary>
    public static double ToPx(double value, string unit) => value * PerPx(unit);

    public static double FromPx(double px, string unit) => px / PerPx(unit);
}

/// <summary>Formati di pagina standard.</summary>
public static class PageSizes
{
    public sealed record Preset(string Name, double Width, double Height, string Unit)
    {
        public double WidthPx => Units.ToPx(Width, Unit);
        public double HeightPx => Units.ToPx(Height, Unit);
        public string Label => $"{Name}  ({Width:0.##} × {Height:0.##} {Unit})";
    }

    public static readonly Preset[] All =
    [
        new("A0", 841, 1189, "mm"), new("A1", 594, 841, "mm"), new("A2", 420, 594, "mm"), new("A3", 297, 420, "mm"),
        new("A4", 210, 297, "mm"), new("A5", 148, 210, "mm"), new("A6", 105, 148, "mm"), new("A7", 74, 105, "mm"), new("A8", 52, 74, "mm"),
        new("B4", 250, 353, "mm"), new("B5", 176, 250, "mm"), new("B6", 125, 176, "mm"),
        new("Letter", 8.5, 11, "in"), new("Legal", 8.5, 14, "in"), new("Tabloid", 11, 17, "in"), new("Executive", 7.25, 10.5, "in"),
        new("HD", 1280, 720, "px"), new("FullHD", 1920, 1080, "px"), new("Square", 1080, 1080, "px"),
    ];

    public static Preset? Find(string name) => All.FirstOrDefault(p => p.Name.Equals(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));

    /// <summary>Il formato che corrisponde alle dimensioni (anche ruotato), se c'è.</summary>
    public static Preset? Match(double widthPx, double heightPx) => All.FirstOrDefault(p =>
        Math.Abs(p.WidthPx - widthPx) < 0.05 && Math.Abs(p.HeightPx - heightPx) < 0.05 ||
        Math.Abs(p.WidthPx - heightPx) < 0.05 && Math.Abs(p.HeightPx - widthPx) < 0.05);
}

/// <summary>Imposta la pagina: un formato standard, oppure larghezza e/o altezza (nell'unità indicata, px se omessa), e l'orientamento.</summary>
public sealed class SetPage : Command
{
    public string? Preset { get; set; }
    /// <summary>"portrait" (verticale) o "landscape" (orizzontale).</summary>
    public string? Orientation { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string? Unit { get; set; }

    (double W, double H) _old;

    [JsonIgnore] public override IEnumerable<string> AffectedIds => [];

    public override void Apply(Document doc)
    {
        var page = doc.Pages[0];
        double w = page.Width, h = page.Height;

        if (Preset != null)
        {
            if (Width != null || Height != null)
                throw new CommandException("page.set: usa 'preset' oppure 'width'/'height', non entrambi.");
            var p = PageSizes.Find(Preset) ?? throw new CommandException(
                $"page.set: formato '{Preset}' sconosciuto. Valori: {string.Join(", ", PageSizes.All.Select(x => x.Name))}.");
            (w, h) = (p.WidthPx, p.HeightPx);
        }
        else if (Width != null || Height != null)
        {
            var unit = Unit ?? "px";
            if (Width is { } nw) w = Units.ToPx(nw, unit);
            if (Height is { } nh) h = Units.ToPx(nh, unit);
        }
        else if (Orientation == null)
            throw new CommandException("page.set: indica 'preset', oppure 'width' e/o 'height', oppure 'orientation'. Esempi: {\"cmd\":\"page.set\",\"preset\":\"A4\",\"orientation\":\"landscape\"} oppure {\"cmd\":\"page.set\",\"width\":210,\"height\":297,\"unit\":\"mm\"}.");

        switch (Orientation)
        {
            case null: break;
            case "portrait": if (w > h) (w, h) = (h, w); break;
            case "landscape": if (h > w) (w, h) = (h, w); break;
            default: throw new CommandException($"page.set: orientation '{Orientation}' non valida. Valori: \"portrait\", \"landscape\".");
        }
        if (w < 1 || h < 1 || w > 100000 || h > 100000 || double.IsNaN(w) || double.IsNaN(h))
            throw new CommandException("page.set: le dimensioni della pagina devono stare tra 1 e 100000 px.");

        _old = (page.Width, page.Height);
        (page.Width, page.Height) = (w, h);
    }

    public override void Undo(Document doc) => (doc.Pages[0].Width, doc.Pages[0].Height) = _old;
}
