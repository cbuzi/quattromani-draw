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
[JsonDerivedType(typeof(ResizeObject), "object.resize")]
[JsonDerivedType(typeof(RotateObjects), "object.rotate")]
[JsonDerivedType(typeof(GroupObjects), "object.group")]
[JsonDerivedType(typeof(UngroupObjects), "object.ungroup")]
[JsonDerivedType(typeof(BooleanOp), "shape.boolean")]
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
    /// <summary>Solo per type "path": dati SVG (M L H V C S Q T A Z, assoluti o relativi).</summary>
    public string? D { get; set; }

    SceneObject? _obj;

    /// <summary>Solo in uscita: id assegnato dopo l'applicazione (visibile in get_changes).</summary>
    public string? Id => _obj?.Id;

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _obj is null ? [] : [_obj.Id];

    public override void Apply(Document doc)
    {
        if (_obj == null)
        {
            SceneObject obj;
            switch (Type)
            {
                case "rectangle" or "ellipse":
                    if (Width <= 0 || Height <= 0)
                        throw new CommandException("shape.create: 'width' e 'height' devono essere > 0.");
                    obj = Type == "ellipse" ? new EllipseObject() : new RectangleObject();
                    obj.X = X; obj.Y = Y; obj.Width = Width; obj.Height = Height;
                    break;
                case "path":
                    if (string.IsNullOrWhiteSpace(D))
                        throw new CommandException("shape.create: type 'path' richiede 'd' (dati SVG), es. \"M 0 0 L 100 0 L 50 80 Z\".");
                    if (X != 0 || Y != 0 || Width != 0 || Height != 0)
                        throw new CommandException("shape.create: type 'path' prende la posizione da 'd': togli x, y, width e height.");
                    var segs = PathData.Parse(D);
                    var box = PathData.Bounds(segs)!.Value;
                    obj = new PathObject { Segments = segs, Source = box, X = box.X, Y = box.Y, Width = box.Width, Height = box.Height };
                    break;
                default:
                    throw new CommandException($"shape.create: type '{Type}' non supportato. Valori: \"rectangle\", \"ellipse\", \"path\".");
            }
            obj.Id = doc.NewId();
            obj.Name = Name;
            obj.Fill = Fill is null ? null : Document.ParseColor(Fill, "fill");
            obj.Stroke = Stroke is null ? null : Document.ParseColor(Stroke, "stroke");
            // Un path senza fill né stroke sarebbe invisibile: gli do un tratto nero.
            if (obj is PathObject && obj.Fill is null && obj.Stroke is null && Fill is null && Stroke is null) obj.Stroke = "#000000";
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

    static IEnumerable<SceneObject> Leaves(SceneObject o) => o is GroupObject g ? g.Children.SelectMany(Leaves) : [o];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.style: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\"].");
        var fill = Fill is null ? null : Document.ParseColor(Fill, "fill");
        var stroke = Stroke is null ? null : Document.ParseColor(Stroke, "stroke");
        // Su un gruppo lo stile si applica a tutte le forme che contiene.
        _old = Targets.Select(t => doc.Find(t)).SelectMany(Leaves).Distinct()
            .Select(o => (o, o.Fill, o.Stroke, o.StrokeWidth)).ToList();
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

    List<(List<SceneObject> Container, int Index, SceneObject Obj)> _removed = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _removed.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.delete: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\"].");
        _removed = Targets.Select(t => doc.Find(t)).Distinct()
            .Select(o => { var c = doc.ContainerOf(o)!; return (c, c.IndexOf(o), o); })
            .OrderBy(e => e.Item2)
            .ToList();
        foreach (var (container, _, obj) in _removed) container.Remove(obj);
    }

    public override void Undo(Document doc)
    {
        // Indici crescenti: ogni reinserimento ripristina la posizione originale.
        foreach (var (container, index, obj) in _removed) container.Insert(index, obj);
    }
}

public sealed class ResizeObject : Command
{
    public List<string> Targets { get; set; } = [];
    /// <summary>Nuova dimensione propria dell'oggetto (prima della rotazione). Null = invariata.</summary>
    public double? Width { get; set; }
    public double? Height { get; set; }
    /// <summary>Punto che resta fermo durante il ridimensionamento.</summary>
    public string Anchor { get; set; } = "top-left";

    List<(SceneObject Obj, Box Old)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    /// <summary>Nuovo rettangolo locale con il punto (ax, ay) fermo.</summary>
    public static Box Resized(Box b, double ax, double ay, double newW, double newH) =>
        new(b.X + ax * (b.Width - newW), b.Y + ay * (b.Height - newH), newW, newH);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.resize: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\"].");
        if (Width is null && Height is null) throw new CommandException("object.resize: indica almeno 'width' o 'height'.");
        if (Width <= 0 || Height <= 0) throw new CommandException("object.resize: 'width' e 'height' devono essere > 0.");
        var (ax, ay) = Core.Anchor.Parse(Anchor);

        _old = Targets.Select(t => doc.Find(t)).Select(o => (o, new Box(o.X, o.Y, o.Width, o.Height))).ToList();
        foreach (var (obj, old) in _old)
        {
            var b = Resized(old, ax, ay, Width ?? old.Width, Height ?? old.Height);
            (obj.X, obj.Y, obj.Width, obj.Height) = (b.X, b.Y, b.Width, b.Height);
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var (obj, b) in _old) (obj.X, obj.Y, obj.Width, obj.Height) = (b.X, b.Y, b.Width, b.Height);
    }
}

public sealed class RotateObjects : Command
{
    public List<string> Targets { get; set; } = [];
    /// <summary>Angolo da aggiungere, in gradi; positivo = senso orario. Ogni oggetto ruota attorno al proprio centro.</summary>
    public double Degrees { get; set; }

    List<(SceneObject Obj, Matrix3x2 Old)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.rotate: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\"].");
        _old = Targets.Select(t => doc.Find(t)).Select(o => (o, o.Transform)).ToList();
        foreach (var (obj, old) in _old)
        {
            var center = Vector2.Transform(new((float)(obj.X + obj.Width / 2), (float)(obj.Y + obj.Height / 2)), old);
            obj.Transform = old * Matrix3x2.CreateRotation((float)(Degrees * Math.PI / 180), center);
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var (obj, old) in _old) obj.Transform = old;
    }
}

public sealed class GroupObjects : Command
{
    public List<string> Targets { get; set; } = [];
    public string? Name { get; set; }

    GroupObject? _group;
    List<SceneObject>? _container;
    List<(int Index, SceneObject Obj)> _entries = [];

    /// <summary>Solo in uscita: id del gruppo creato.</summary>
    public string? Id => _group?.Id;

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _group is null ? [] : [_group.Id];

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.group: 'targets' e' vuoto. Passa gli id o i nomi da raggruppare, es. [\"o1\",\"o2\"].");
        var objs = Targets.Select(t => doc.Find(t)).Distinct().ToList();
        var container = doc.ContainerOf(objs[0])!;
        if (objs.Any(o => doc.ContainerOf(o) != container))
            throw new CommandException("object.group: gli oggetti devono stare allo stesso livello (stesso layer o stesso gruppo).");

        _entries = objs.Select(o => (container.IndexOf(o), o)).OrderBy(e => e.Item1).ToList();
        if (_group == null)
        {
            var g = new GroupObject { Id = doc.NewId(), Name = Name };
            g.Children.AddRange(_entries.Select(e => e.Obj));
            var bs = g.Children.Select(c => c.Bounds).ToList();
            var (x0, y0) = (bs.Min(b => b.X), bs.Min(b => b.Y));
            var box = new Box(x0, y0, bs.Max(b => b.X + b.Width) - x0, bs.Max(b => b.Y + b.Height) - y0);
            (g.X, g.Y, g.Width, g.Height, g.Source) = (box.X, box.Y, box.Width, box.Height, box);
            _group = g;
        }

        foreach (var (_, obj) in _entries) container.Remove(obj);
        // Il gruppo prende il posto (z-order) dell'oggetto più in alto tra quelli raggruppati.
        container.Insert(_entries[^1].Index - (_entries.Count - 1), _group);
        _container = container;
    }

    public override void Undo(Document doc)
    {
        _container!.Remove(_group!);
        foreach (var (index, obj) in _entries) _container.Insert(index, obj);
    }
}

public sealed class UngroupObjects : Command
{
    public List<string> Targets { get; set; } = [];

    List<(GroupObject Group, List<SceneObject> Container, int Index, List<Matrix3x2> OldTransforms)> _done = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _done.SelectMany(d => d.Group.Children.Select(c => c.Id));

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.ungroup: 'targets' e' vuoto. Passa l'id o il nome di un gruppo.");
        var objs = Targets.Select(t => doc.Find(t)).Distinct().ToList();
        if (objs.FirstOrDefault(o => o is not GroupObject) is { } notGroup)
            throw new CommandException($"object.ungroup: '{notGroup.Name ?? notGroup.Id}' e' un {notGroup.Type}, non un gruppo.");

        _done = [];
        foreach (var g in objs.Cast<GroupObject>())
        {
            var container = doc.ContainerOf(g)!;
            var index = container.IndexOf(g);
            var old = g.Children.Select(c => c.Transform).ToList();
            // I figli assorbono la trasformazione del gruppo, così restano dove sono.
            var fit = g.Fit;
            foreach (var c in g.Children) c.Transform = c.Transform * fit * g.Transform;
            container.RemoveAt(index);
            container.InsertRange(index, g.Children);
            _done.Add((g, container, index, old));
        }
    }

    public override void Undo(Document doc)
    {
        for (var i = _done.Count - 1; i >= 0; i--)
        {
            var (g, container, index, old) = _done[i];
            container.RemoveRange(index, g.Children.Count);
            container.Insert(index, g);
            for (var k = 0; k < old.Count; k++) g.Children[k].Transform = old[k];
        }
    }
}
