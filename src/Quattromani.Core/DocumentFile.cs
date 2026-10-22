using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Quattromani.Core;

/// <summary>Formato di file del documento (.quattromani): JSON leggibile, con tutto il necessario per ricostruire il disegno identico.</summary>
public static partial class DocumentFile
{
    public const string Extension = ".quattromani";
    public const int CurrentVersion = 1;

    // ---- DTO (la forma del file) ---------------------------------------------------------------

    sealed class FileDto
    {
        public string Format { get; set; } = "quattromani";
        public int Version { get; set; } = CurrentVersion;
        public int NextId { get; set; }
        public List<PageDto> Pages { get; set; } = [];
    }

    sealed class PageDto
    {
        public string Name { get; set; } = "";
        public double Width { get; set; }
        public double Height { get; set; }
        public List<Guide>? Guides { get; set; }
        public List<LayerDto> Layers { get; set; } = [];
        /// <summary>Indice del layer attivo; omesso se è il primo.</summary>
        public int? ActiveLayer { get; set; }
    }

    sealed class LayerDto
    {
        public string Name { get; set; } = "";
        public bool? Hidden { get; set; }
        public bool? Locked { get; set; }
        public List<ObjDto> Objects { get; set; } = [];
    }

    sealed class LineDto
    {
        public string Text { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
    }

    sealed class ObjDto
    {
        public string Type { get; set; } = "";
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        /// <summary>Matrice affine [m11, m12, m21, m22, m31, m32]; omessa se è l'identità.</summary>
        public float[]? Transform { get; set; }
        public string? Fill { get; set; }
        public string? Stroke { get; set; }
        public double? StrokeWidth { get; set; }
        public bool? Hidden { get; set; }
        public bool? Locked { get; set; }

        // path
        public string? D { get; set; }
        public string? NodeKinds { get; set; }
        // path, gruppo, testo: riquadro dei contenuti prima dello stiramento su (X, Y, Width, Height)
        public double[]? Source { get; set; }
        // gruppo
        public List<ObjDto>? Children { get; set; }
        // testo
        public string? Text { get; set; }
        public string? FontFamily { get; set; }
        public double? FontSize { get; set; }
        public bool? Bold { get; set; }
        public bool? Italic { get; set; }
        public string? Align { get; set; }
        public TextOnPath? OnPath { get; set; }
        public double? LineHeight { get; set; }
        public double? WrapWidth { get; set; }
        public List<LineDto>? Lines { get; set; }
        // immagine: file originale in base64 e dimensioni in pixel
        public string? Image { get; set; }
        public int? PixelWidth { get; set; }
        public int? PixelHeight { get; set; }
    }

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- Salvataggio ---------------------------------------------------------------------------

    public static string Save(Document doc) => JsonSerializer.Serialize(new FileDto
    {
        NextId = doc.NextIdValue,
        Pages = doc.Pages.Select(p => new PageDto
        {
            Name = p.Name, Width = p.Width, Height = p.Height, Guides = p.Guides.Count == 0 ? null : p.Guides,
            Layers = p.Layers.Select(l => new LayerDto { Name = l.Name, Hidden = l.Visible ? null : true, Locked = l.Locked ? true : null, Objects = l.Objects.Select(ToDto).ToList() }).ToList(),
            ActiveLayer = p.Layers.IndexOf(p.ActiveLayer) is > 0 and var active ? active : null,
        }).ToList(),
    }, Options);

    static ObjDto ToDto(SceneObject o)
    {
        var dto = new ObjDto
        {
            Type = o.Type, Id = o.Id, Name = o.Name, X = o.X, Y = o.Y, Width = o.Width, Height = o.Height,
            Fill = o.Fill, Stroke = o.Stroke, StrokeWidth = o.StrokeWidth == 0 ? null : o.StrokeWidth,
            Hidden = o.Hidden ? true : null, Locked = o.Locked ? true : null,
            Transform = o.Transform == Matrix3x2.Identity ? null : [o.Transform.M11, o.Transform.M12, o.Transform.M21, o.Transform.M22, o.Transform.M31, o.Transform.M32],
        };
        switch (o)
        {
            case PathObject p:
                dto.D = PathData.ToString(p.Segments, 6);
                dto.NodeKinds = p.NodeKinds;
                dto.Source = [p.Source.X, p.Source.Y, p.Source.Width, p.Source.Height];
                break;
            case GroupObject g:
                dto.Source = [g.Source.X, g.Source.Y, g.Source.Width, g.Source.Height];
                dto.Children = g.Children.Select(ToDto).ToList();
                break;
            case ImageObject im:
                (dto.Image, dto.PixelWidth, dto.PixelHeight) = (Convert.ToBase64String(im.Data), im.PixelWidth, im.PixelHeight);
                break;
            case TextObject t:
                (dto.Text, dto.FontFamily, dto.FontSize, dto.Bold, dto.Italic, dto.Align, dto.LineHeight, dto.WrapWidth) =
                    (t.Text, t.FontFamily, t.FontSize, t.Bold ? true : null, t.Italic ? true : null, t.Align, t.LineHeight, t.WrapWidth);
                dto.Source = [t.Source.X, t.Source.Y, t.Source.Width, t.Source.Height];
                dto.Lines = t.Lines.Select(l => new LineDto { Text = l.Text, X = l.X, Y = l.Y }).ToList();
                dto.OnPath = t.OnPath; // i glifi sul tracciato si ricalcolano all'apertura
                break;
        }
        return dto;
    }

    // ---- Copia e incolla -----------------------------------------------------------------------

    /// <summary>Serializza degli oggetti (figli compresi) per Copia/Duplica; si ricreano con <see cref="ImportObjects"/>.</summary>
    public static string ExportObjects(IEnumerable<SceneObject> objects) => JsonSerializer.Serialize(objects.Select(ToDto).ToList(), Options);

    /// <summary>Ricrea gli oggetti di <see cref="ExportObjects"/> con id nuovi; i nomi già in uso diventano "nome 2", "nome 3"... (devono restare univoci).</summary>
    internal static List<SceneObject> ImportObjects(string json, Document doc)
    {
        List<ObjDto> dtos;
        try { dtos = JsonSerializer.Deserialize<List<ObjDto>>(json, Options) ?? throw new JsonException("vuoto"); }
        catch (JsonException ex) { throw new CommandException($"Dati degli oggetti non validi: {ex.Message}"); }

        var ids = new HashSet<string>();
        var objs = dtos.Select(d => FromDto(d, ids)).ToList();
        var renamed = new Dictionary<string, string>();
        var taken = doc.AllObjects.Select(o => o.Name).OfType<string>().ToHashSet();
        foreach (var o in objs.SelectMany(Document.SelfAndDescendants))
        {
            var id = doc.NewId();
            renamed[o.Id] = id;
            o.Id = id;
            if (o.Name is not { } name || taken.Add(name)) continue;
            var stem = TrailingNumber().Replace(name, "");
            var n = 2;
            while (!taken.Add($"{stem} {n}")) n++;
            o.Name = $"{stem} {n}";
        }
        // Un testo su tracciato che ha copiato anche il suo tracciato segue la copia.
        foreach (var t in objs.SelectMany(Document.SelfAndDescendants).OfType<TextObject>())
            if (t.OnPath is { } p && renamed.TryGetValue(p.PathId, out var newPath)) t.OnPath = p with { PathId = newPath };
        return objs;
    }

    // ---- Apertura ------------------------------------------------------------------------------

    /// <summary>Legge un documento; se il file non è valido solleva un errore che dice cosa non va.</summary>
    public static Document Load(string json)
    {
        FileDto dto;
        try { dto = JsonSerializer.Deserialize<FileDto>(json, Options) ?? throw new JsonException("file vuoto"); }
        catch (JsonException ex) { throw new CommandException($"Il file non e' un documento Quattromani Draw valido: {ex.Message}"); }

        if (dto.Format != "quattromani") throw new CommandException("Il file non e' un documento Quattromani Draw (manca \"format\": \"quattromani\").");
        if (dto.Version > CurrentVersion)
            throw new CommandException($"Il file e' stato creato da una versione piu' recente di Quattromani Draw (formato {dto.Version}, questa legge fino al {CurrentVersion}).");
        if (dto.Pages.Count == 0) throw new CommandException("Il file non contiene nessuna pagina.");

        var doc = new Document();
        doc.Pages.Clear();
        var ids = new HashSet<string>();
        foreach (var pd in dto.Pages)
        {
            if (!(pd.Width >= 1 && pd.Height >= 1 && pd.Width <= 100000 && pd.Height <= 100000))
                throw new CommandException($"Pagina '{pd.Name}': dimensioni non valide ({pd.Width} x {pd.Height}).");
            var page = new Page { Name = pd.Name, Width = pd.Width, Height = pd.Height };
            foreach (var g in pd.Guides ?? [])
            {
                if (g.Axis is not ("x" or "y") || !double.IsFinite(g.Position)) throw new CommandException($"Guida non valida ({g.Axis} = {g.Position}).");
                page.Guides.Add(g);
            }
            page.Layers.Clear();
            foreach (var ld in pd.Layers)
            {
                var layer = new Layer(ld.Name) { Visible = ld.Hidden != true, Locked = ld.Locked == true };
                if (page.Layers.Any(l => l.Name.Equals(layer.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new CommandException($"Il file ha due layer con lo stesso nome ('{layer.Name}').");
                foreach (var od in ld.Objects) layer.Objects.Add(FromDto(od, ids));
                page.Layers.Add(layer);
            }
            if (page.Layers.Count == 0) page.Layers.Add(new Layer("Layer 1"));
            page.ActiveLayer = page.Layers[Math.Clamp(pd.ActiveLayer ?? 0, 0, page.Layers.Count - 1)];
            doc.Pages.Add(page);
        }

        // Il contatore degli id riparte oltre il più alto già usato: gli id nuovi non collidono mai.
        var max = ids.Select(i => IdNumber().Match(i)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
        doc.NextIdValue = Math.Max(dto.NextId, max);
        return doc;
    }

    static TextOnPath? ValidOnPath(TextOnPath? p, string label)
    {
        if (p == null) return null;
        if (string.IsNullOrEmpty(p.PathId) || p.Align is not ("start" or "center" or "end") || !double.IsFinite(p.Offset) || !double.IsFinite(p.Distance))
            throw new CommandException($"Oggetto {label}: testo su tracciato non valido.");
        return p;
    }

    [GeneratedRegex(@" \d+$")]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"^o(\d+)$")]
    private static partial Regex IdNumber();

    static SceneObject FromDto(ObjDto d, HashSet<string> ids)
    {
        var label = string.IsNullOrEmpty(d.Id) ? $"({d.Type})" : d.Id;
        if (string.IsNullOrEmpty(d.Id) || !ids.Add(d.Id)) throw new CommandException($"Oggetto {label}: id mancante o duplicato.");
        if (!new[] { d.X, d.Y, d.Width, d.Height }.All(double.IsFinite)) throw new CommandException($"Oggetto {label}: geometria non valida.");

        Box Src() => d.Source is { Length: 4 } s && s.All(double.IsFinite)
            ? new Box(s[0], s[1], s[2], s[3])
            : throw new CommandException($"Oggetto {label}: manca il riquadro 'source'.");

        SceneObject obj;
        switch (d.Type)
        {
            case "rectangle": obj = new RectangleObject(); break;
            case "ellipse": obj = new EllipseObject(); break;
            case "image":
            {
                byte[] data;
                try { data = Convert.FromBase64String(d.Image ?? throw new CommandException($"Oggetto {label}: manca 'image'.")); }
                catch (FormatException) { throw new CommandException($"Oggetto {label}: 'image' non e' base64 valido."); }
                obj = new ImageObject { Data = data, PixelWidth = d.PixelWidth ?? 0, PixelHeight = d.PixelHeight ?? 0 };
                break;
            }
            case "path":
            {
                var segs = PathData.Parse(d.D ?? throw new CommandException($"Oggetto {label}: manca 'd'."));
                obj = new PathObject { Segments = segs, Source = d.Source is { Length: 4 } ? Src() : PathData.Bounds(segs)!.Value, NodeKinds = d.NodeKinds };
                break;
            }
            case "group":
            {
                var g = new GroupObject { Source = Src() };
                foreach (var c in d.Children ?? []) g.Children.Add(FromDto(c, ids));
                obj = g;
                break;
            }
            case "text":
                obj = new TextObject
                {
                    Text = d.Text ?? "", FontFamily = d.FontFamily ?? "", FontSize = d.FontSize ?? 24, Bold = d.Bold ?? false, Italic = d.Italic ?? false,
                    Align = d.Align ?? "left", LineHeight = d.LineHeight ?? 1, WrapWidth = d.WrapWidth,
                    Source = Src(), Lines = (d.Lines ?? []).Select(l => new TextLine(l.Text, l.X, l.Y)).ToList(),
                    OnPath = ValidOnPath(d.OnPath, label),
                };
                break;
            default: throw new CommandException($"Oggetto {label}: tipo '{d.Type}' sconosciuto (rectangle, ellipse, image, path, group, text).");
        }

        obj.Id = d.Id;
        obj.Name = d.Name;
        (obj.X, obj.Y, obj.Width, obj.Height) = (d.X, d.Y, d.Width, d.Height);
        obj.Fill = d.Fill is null ? null : Document.ParseColor(d.Fill, $"{label}.fill");
        obj.Stroke = d.Stroke is null ? null : Document.ParseColor(d.Stroke, $"{label}.stroke");
        obj.StrokeWidth = d.StrokeWidth ?? 0;
        (obj.Hidden, obj.Locked) = (d.Hidden == true, d.Locked == true);
        if (d.Transform is { } t)
        {
            if (t.Length != 6 || !t.All(float.IsFinite)) throw new CommandException($"Oggetto {label}: 'transform' deve avere 6 numeri.");
            obj.Transform = new Matrix3x2(t[0], t[1], t[2], t[3], t[4], t[5]);
        }
        return obj;
    }
}

/// <summary>Voce del registro delle modifiche che segnala la sostituzione dell'intero documento (apri, nuovo): non è un comando eseguibile.</summary>
public sealed class DocumentReset : Command
{
    /// <summary>"new" oppure "open".</summary>
    public string Reason { get; set; } = "";
    public string? File { get; set; }

    public override void Apply(Document doc) => throw new NotSupportedException();
    public override void Undo(Document doc) => throw new NotSupportedException();
}
