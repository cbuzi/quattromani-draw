using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDraw.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Un campo sbagliato dall'LLM deve dare errore, non essere ignorato in silenzio.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

/// <summary>
/// Comando serializzabile, annullabile. Lo stato necessario all'undo sta in campi privati (non serializzati).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "cmd")]
[JsonDerivedType(typeof(CreateShape), "shape.create")]
[JsonDerivedType(typeof(MoveObjects), "object.move")]
[JsonDerivedType(typeof(SetStyle), "object.style")]
[JsonDerivedType(typeof(DeleteObjects), "object.delete")]
public abstract class Command
{
    /// <summary>"human" oppure "llm:&lt;nome&gt;". Impostato dal CommandBus.</summary>
    [JsonIgnore] public string Author { get; set; } = "human";

    [JsonIgnore] public virtual IEnumerable<string> CreatedIds => [];

    /// <summary>Id degli oggetti toccati dal comando (validi dopo Apply).</summary>
    [JsonIgnore] public virtual IEnumerable<string> AffectedIds => CreatedIds;

    public abstract void Apply(Document doc);
    public abstract void Undo(Document doc);
}

public sealed class CreateShape : Command
{
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? Fill { get; set; }
    public string? Stroke { get; set; }
    public double? StrokeWidth { get; set; }

    SceneObject? _obj;

    /// <summary>Solo in uscita: id assegnato dopo l'applicazione (visibile in get_changes).</summary>
    public string? Id => _obj?.Id;

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _obj is null ? [] : [_obj.Id];

    public override void Apply(Document doc)
    {
        if (_obj == null)
        {
            if (Width <= 0 || Height <= 0)
                throw new CommandException("shape.create: 'width' e 'height' devono essere > 0.");
            SceneObject obj = Type switch
            {
                "rectangle" => new RectangleObject(),
                "ellipse" => new EllipseObject(),
                _ => throw new CommandException($"shape.create: type '{Type}' non supportato. Valori: \"rectangle\", \"ellipse\"."),
            };
            obj.Id = doc.NewId();
            obj.Name = Name;
            obj.X = X; obj.Y = Y; obj.Width = Width; obj.Height = Height;
            obj.Fill = Fill is null ? null : Document.ParseColor(Fill, "fill");
            obj.Stroke = Stroke is null ? null : Document.ParseColor(Stroke, "stroke");
            obj.StrokeWidth = StrokeWidth ?? (obj.Stroke is null ? 0 : 1);
            _obj = obj;
        }
        doc.Pages[0].Layers[0].Objects.Add(_obj);
    }

    public override void Undo(Document doc) => doc.Pages[0].Layers[0].Objects.Remove(_obj!);
}

public sealed class MoveObjects : Command
{
    /// <summary>Id o nomi degli oggetti.</summary>
    public List<string> Targets { get; set; } = [];
    public double Dx { get; set; }
    public double Dy { get; set; }

    List<(SceneObject Obj, Matrix3x2 Old)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.move: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\"].");
        _old = Targets.Select(t => doc.Find(t)).Select(o => (o, o.Transform)).ToList();
        foreach (var (obj, old) in _old)
            obj.Transform = old * Matrix3x2.CreateTranslation((float)Dx, (float)Dy);
    }

    public override void Undo(Document doc)
    {
        foreach (var (obj, old) in _old) obj.Transform = old;
    }
}

public sealed class SetStyle : Command
{
    public List<string> Targets { get; set; } = [];
    /// <summary>Campi null = invariati. "none" rimuove il colore.</summary>
    public string? Fill { get; set; }
    public string? Stroke { get; set; }
    public double? StrokeWidth { get; set; }

    List<(SceneObject Obj, string? Fill, string? Stroke, double Width)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.style: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\"].");
        var fill = Fill is null ? null : Document.ParseColor(Fill, "fill");
        var stroke = Stroke is null ? null : Document.ParseColor(Stroke, "stroke");
        _old = Targets.Select(t => doc.Find(t)).Select(o => (o, o.Fill, o.Stroke, o.StrokeWidth)).ToList();
        foreach (var (obj, _, _, _) in _old)
        {
            if (Fill != null) obj.Fill = fill;
            if (Stroke != null) obj.Stroke = stroke;
            if (StrokeWidth != null) obj.StrokeWidth = StrokeWidth.Value;
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var (obj, fill, stroke, width) in _old)
        {
            obj.Fill = fill; obj.Stroke = stroke; obj.StrokeWidth = width;
        }
    }
}

public sealed class DeleteObjects : Command
{
    public List<string> Targets { get; set; } = [];

    List<(Layer Layer, int Index, SceneObject Obj)> _removed = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _removed.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.delete: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\"].");
        var objs = Targets.Select(t => doc.Find(t)).Distinct().ToList();
        _removed = doc.Pages.SelectMany(p => p.Layers)
            .SelectMany(l => l.Objects.Select((o, i) => (Layer: l, Index: i, Obj: o)))
            .Where(e => objs.Contains(e.Obj))
            .OrderBy(e => e.Index)
            .ToList();
        foreach (var (layer, _, obj) in _removed) layer.Objects.Remove(obj);
    }

    public override void Undo(Document doc)
    {
        // Indici crescenti: ogni reinserimento ripristina la posizione originale.
        foreach (var (layer, index, obj) in _removed) layer.Objects.Insert(index, obj);
    }
}
