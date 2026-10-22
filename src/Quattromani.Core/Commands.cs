using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quattromani.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A wrong field from the LLM must give an error, not be silently ignored.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

/// <summary>
/// Serializable, undoable command. The state needed for undo lives in private fields (not serialized).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "cmd")]
[JsonDerivedType(typeof(CreateShape), "shape.create")]
[JsonDerivedType(typeof(MoveObjects), "object.move")]
[JsonDerivedType(typeof(SetStyle), "object.style")]
[JsonDerivedType(typeof(DeleteObjects), "object.delete")]
[JsonDerivedType(typeof(DuplicateObjects), "object.duplicate")]
[JsonDerivedType(typeof(PasteObjects), "object.paste")]
[JsonDerivedType(typeof(ResizeObject), "object.resize")]
[JsonDerivedType(typeof(RotateObjects), "object.rotate")]
[JsonDerivedType(typeof(GroupObjects), "object.group")]
[JsonDerivedType(typeof(UngroupObjects), "object.ungroup")]
[JsonDerivedType(typeof(BooleanOp), "shape.boolean")]
[JsonDerivedType(typeof(SetPath), "path.set")]
[JsonDerivedType(typeof(PlaceObjects), "object.place")]
[JsonDerivedType(typeof(SetText), "text.set")]
[JsonDerivedType(typeof(SetPage), "page.set")]
[JsonDerivedType(typeof(RenameObject), "object.rename")]
[JsonDerivedType(typeof(OrderObjects), "object.order")]
[JsonDerivedType(typeof(AlignObjects), "object.align")]
[JsonDerivedType(typeof(DistributeObjects), "object.distribute")]
[JsonDerivedType(typeof(SetTextOnPath), "text.onPath")]
[JsonDerivedType(typeof(AddGuide), "guide.add")]
[JsonDerivedType(typeof(MoveGuide), "guide.move")]
[JsonDerivedType(typeof(DeleteGuide), "guide.delete")]
[JsonDerivedType(typeof(DocumentReset), "document.reset")]
[JsonDerivedType(typeof(ConvertToPath), "shape.toPath")]
[JsonDerivedType(typeof(TraceImage), "image.trace")]
[JsonDerivedType(typeof(ImportSvg), "svg.import")]
[JsonDerivedType(typeof(AddLayer), "layer.add")]
[JsonDerivedType(typeof(SetLayer), "layer.set")]
[JsonDerivedType(typeof(DeleteLayer), "layer.delete")]
[JsonDerivedType(typeof(OrderLayer), "layer.order")]
[JsonDerivedType(typeof(MoveToLayer), "object.toLayer")]
[JsonDerivedType(typeof(SetObjectState), "object.state")]
public abstract class Command
{
    /// <summary>"human" or "llm:&lt;name&gt;". Set by the CommandBus.</summary>
    [JsonIgnore] public string Author { get; set; } = "human";

    [JsonIgnore] public virtual IEnumerable<string> CreatedIds => [];

    /// <summary>Ids of the objects touched by the command (valid after Apply).</summary>
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
    /// <summary>Only for type "path": SVG data (M L H V C S Q T A Z, absolute or relative).</summary>
    public string? D { get; set; }
    /// <summary>Relative positioning to other objects, instead of x and y.</summary>
    public Placement? Rel { get; set; }
    /// <summary>Only for type "text": the content ('\n' = line break) and the style. For text, width is the width for automatic wrapping.</summary>
    public string? Text { get; set; }
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public string? Align { get; set; }
    public double? LineHeight { get; set; }
    /// <summary>Only for type "image": absolute file path or base64 data URI. After applying, a data URI is shortened.</summary>
    public string? Src { get; set; }
    /// <summary>Name of the layer where to create the object; without it, the active layer.</summary>
    public string? Layer { get; set; }

    const int MaxImageBytes = 30 * 1024 * 1024;

    SceneObject? _obj;
    Layer? _layer;

    /// <summary>Output only: id assigned after applying (visible in get_changes).</summary>
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
                        throw new CommandException("shape.create: 'width' and 'height' must be > 0.");
                    obj = Type == "ellipse" ? new EllipseObject() : new RectangleObject();
                    obj.X = X; obj.Y = Y; obj.Width = Width; obj.Height = Height;
                    break;
                case "path":
                    if (string.IsNullOrWhiteSpace(D))
                        throw new CommandException("shape.create: type 'path' requires 'd' (SVG path data), e.g. \"M 0 0 L 100 0 L 50 80 Z\".");
                    if (X != 0 || Y != 0 || Width != 0 || Height != 0)
                        throw new CommandException("shape.create: type 'path' takes its position from 'd': remove x, y, width and height.");
                    var segs = PathData.Parse(D);
                    var box = PathData.Bounds(segs)!.Value;
                    obj = new PathObject { Segments = segs, Source = box, X = box.X, Y = box.Y, Width = box.Width, Height = box.Height };
                    break;
                case "text":
                    if (string.IsNullOrEmpty(Text))
                        throw new CommandException("shape.create: type 'text' requires 'text', e.g. {\"cmd\":\"shape.create\",\"type\":\"text\",\"text\":\"Hello\",\"fontSize\":32,\"x\":10,\"y\":10}.");
                    if (Height != 0)
                        throw new CommandException("shape.create: for text 'height' is computed; 'width', if given, is the width for automatic line wrapping.");
                    TextObject.Validate(doc, FontFamily, FontSize, Align, LineHeight, Width);
                    var engine = doc.TextEngine ?? throw new CommandException("The font engine is not available: the text cannot be created.");
                    var text = new TextObject
                    {
                        Text = Text, X = X, Y = Y,
                        FontFamily = FontFamily is null ? engine.DefaultFamily : engine.Families.First(f => f.Equals(FontFamily, StringComparison.OrdinalIgnoreCase)),
                        FontSize = FontSize ?? 24, Bold = Bold ?? false, Italic = Italic ?? false, Align = Align ?? "left",
                        LineHeight = LineHeight ?? 1, WrapWidth = Width > 0 ? Width : null,
                    };
                    text.Relayout(doc);
                    obj = text;
                    break;
                case "image":
                    obj = CreateImage(doc);
                    break;
                default:
                    throw new CommandException($"shape.create: type '{Type}' is not supported. Values: \"rectangle\", \"ellipse\", \"path\", \"text\", \"image\".");
            }
            if (Rel != null)
            {
                if (Type != "path" && (X != 0 || Y != 0))
                    throw new CommandException("shape.create: with 'rel' the position is computed: remove x and y.");
                var (dx, dy) = Rel.Resolve(doc, obj.Bounds);
                if (obj is PathObject) obj.Transform = Matrix3x2.CreateTranslation((float)dx, (float)dy);
                else { obj.X += dx; obj.Y += dy; }
            }
            obj.Id = doc.NewId();
            obj.Name = Name;
            obj.Fill = Fill is null ? null : Document.ParseColor(Fill, "fill");
            obj.Stroke = Stroke is null ? null : Document.ParseColor(Stroke, "stroke");
            // A text without color would be invisible: black.
            if (obj is TextObject && Fill is null && Stroke is null) obj.Fill = "#000000";
            // A path with neither fill nor stroke would be invisible: give it a black stroke.
            if (obj is PathObject && obj.Fill is null && obj.Stroke is null && Fill is null && Stroke is null) obj.Stroke = "#000000";
            obj.StrokeWidth = StrokeWidth ?? (obj.Stroke is null ? 0 : 1);
            _obj = obj;
        }
        _layer ??= doc.TargetLayer(Layer);
        _layer.Objects.Add(_obj);
    }

    public override void Undo(Document doc) => _layer!.Objects.Remove(_obj!);

    ImageObject CreateImage(Document doc)
    {
        const string Example = "{\"cmd\":\"shape.create\",\"type\":\"image\",\"src\":\"/Users/me/photo.png\",\"width\":400}";
        if (string.IsNullOrWhiteSpace(Src))
            throw new CommandException($"shape.create: type 'image' requires 'src' (absolute file path or base64 data URI), e.g. {Example}.");
        if (Height < 0 || Width < 0)
            throw new CommandException("shape.create: 'width' and 'height' must be > 0.");
        var probe = doc.ImageProbe ?? throw new CommandException("The image decoder is not available: the image cannot be created.");

        byte[] data;
        if (Src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = Src.IndexOf(',');
            if (comma < 0 || !Src[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new CommandException("shape.create: 'src' as a data URI must be \"data:image/png;base64,...\".");
            try { data = Convert.FromBase64String(Src[(comma + 1)..]); }
            catch (FormatException) { throw new CommandException("shape.create: the base64 in 'src' is not valid."); }
            Src = $"data:image (base64, {data.Length / 1024} KB)"; // redo reuses the object: the data is no longer needed in the command
        }
        else
        {
            if (!Path.IsPathFullyQualified(Src))
                throw new CommandException($"shape.create: 'src' must be an absolute path (e.g. \"/Users/me/photo.png\") or a data URI; got '{Src}'.");
            if (!File.Exists(Src)) throw new CommandException($"shape.create: file not found: {Src}");
            if (new FileInfo(Src).Length > MaxImageBytes) throw new CommandException($"shape.create: the file exceeds {MaxImageBytes / 1024 / 1024} MB.");
            try { data = File.ReadAllBytes(Src); }
            catch (IOException ex) { throw new CommandException($"shape.create: cannot read {Src}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { throw new CommandException($"shape.create: cannot read {Src}: {ex.Message}"); }
        }
        if (data.Length > MaxImageBytes) throw new CommandException($"shape.create: the image exceeds {MaxImageBytes / 1024 / 1024} MB.");
        var size = probe(data) is { Width: > 0, Height: > 0 } sz ? sz : throw new CommandException("shape.create: 'src' is not a readable image. Formats: PNG, JPEG, GIF, WebP, BMP.");

        // Native size if not given; with a single measure the other follows the proportions.
        double w = Width, h = Height;
        if (w <= 0 && h <= 0) (w, h) = (size.Width, size.Height);
        else if (h <= 0) h = w * size.Height / size.Width;
        else if (w <= 0) w = h * size.Width / size.Height;
        return new ImageObject { Data = data, PixelWidth = size.Width, PixelHeight = size.Height, X = X, Y = Y, Width = w, Height = h };
    }
}

public sealed class MoveObjects : Command
{
    /// <summary>Ids or names of the objects.</summary>
    public List<string> Targets { get; set; } = [];
    public double Dx { get; set; }
    public double Dy { get; set; }

    List<(SceneObject Obj, Matrix3x2 Old)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.move: 'targets' is empty. Pass ids or names, e.g. [\"o1\"].");
        _old = Targets.Select(t => doc.FindEditable(t)).Select(o => (o, o.Transform)).ToList();
        _oldOnPath = [];
        foreach (var (obj, old) in _old)
        {
            if (obj is TextObject { OnPath: { } op } text)
            {
                if (_old.Any(e => e.Obj.Id == op.PathId)) continue; // the path moves too: the text follows it by itself
                // A text on a path has no position of its own: moving it slides it along the path (offset) and moves it closer or farther (distance).
                var delta = System.Numerics.Vector2.TransformNormal(new((float)Dx, (float)Dy), doc.ParentMatrix(text));
                if (TextOnPathLayout.Slide(doc, text, delta) is { } moved)
                {
                    _oldOnPath.Add((text, op));
                    text.OnPath = moved;
                    continue;
                }
            }
            obj.Transform = old * Matrix3x2.CreateTranslation((float)Dx, (float)Dy);
        }
    }

    List<(TextObject Text, TextOnPath OnPath)> _oldOnPath = [];

    public override void Undo(Document doc)
    {
        foreach (var (obj, old) in _old) obj.Transform = old;
        foreach (var (text, op) in _oldOnPath) text.OnPath = op;
    }
}

public sealed class SetStyle : Command
{
    public List<string> Targets { get; set; } = [];
    /// <summary>Null fields = unchanged. "none" removes the color.</summary>
    public string? Fill { get; set; }
    public string? Stroke { get; set; }
    public double? StrokeWidth { get; set; }

    List<(SceneObject Obj, string? Fill, string? Stroke, double Width)> _old = [];

    static IEnumerable<SceneObject> Leaves(SceneObject o) => o is GroupObject g ? g.Children.SelectMany(Leaves) : [o];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.style: 'targets' is empty. Pass ids or names, e.g. [\"o1\"].");
        var fill = Fill is null ? null : Document.ParseColor(Fill, "fill");
        var stroke = Stroke is null ? null : Document.ParseColor(Stroke, "stroke");
        // On a group the style applies to all the shapes it contains.
        _old = Targets.Select(t => doc.FindEditable(t)).SelectMany(Leaves).Distinct()
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
        if (Targets.Count == 0) throw new CommandException("object.delete: 'targets' is empty. Pass ids or names, e.g. [\"o1\"].");
        _removed = Targets.Select(t => doc.FindEditable(t)).Distinct()
            .Select(o => { var c = doc.ContainerOf(o)!; return (c, c.IndexOf(o), o); })
            .OrderBy(e => e.Item2)
            .ToList();
        foreach (var (container, _, obj) in _removed) container.Remove(obj);
    }

    public override void Undo(Document doc)
    {
        // Ascending indexes: each reinsertion restores the original position.
        foreach (var (container, index, obj) in _removed) container.Insert(index, obj);
    }
}

public sealed class ResizeObject : Command
{
    public List<string> Targets { get; set; } = [];
    /// <summary>New own size of the object (before the rotation). Null = unchanged.</summary>
    public double? Width { get; set; }
    public double? Height { get; set; }
    /// <summary>Point that stays fixed during the resize.</summary>
    public string Anchor { get; set; } = "top-left";

    List<(SceneObject Obj, Box Old)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    /// <summary>New local rectangle with the point (ax, ay) fixed.</summary>
    public static Box Resized(Box b, double ax, double ay, double newW, double newH) =>
        new(b.X + ax * (b.Width - newW), b.Y + ay * (b.Height - newH), newW, newH);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.resize: 'targets' is empty. Pass ids or names, e.g. [\"o1\"].");
        if (Width is null && Height is null) throw new CommandException("object.resize: give at least 'width' or 'height'.");
        if (Width <= 0 || Height <= 0) throw new CommandException("object.resize: 'width' and 'height' must be > 0.");
        var (ax, ay) = Core.Anchor.Parse(Anchor);

        _old = Targets.Select(t => doc.FindEditable(t)).Select(o => (o, new Box(o.X, o.Y, o.Width, o.Height))).ToList();
        _oldFonts = [];
        foreach (var (obj, old) in _old)
        {
            if (obj is TextObject { OnPath: not null } text && old.Width > 0 && old.Height > 0)
            {
                // A text on a path is not stretched: its extent is decided by the path. Resizing it changes the font size.
                var kw = (Width ?? old.Width) / old.Width;
                var kh = (Height ?? old.Height) / old.Height;
                _oldFonts.Add((text, text.FontSize));
                text.FontSize = Math.Clamp(text.FontSize * (Math.Abs(kw - 1) >= Math.Abs(kh - 1) ? kw : kh), 1, 2000);
                continue;
            }
            var b = Resized(old, ax, ay, Width ?? old.Width, Height ?? old.Height);
            (obj.X, obj.Y, obj.Width, obj.Height) = (b.X, b.Y, b.Width, b.Height);
        }
    }

    List<(TextObject Text, double FontSize)> _oldFonts = [];

    public override void Undo(Document doc)
    {
        foreach (var (obj, b) in _old) (obj.X, obj.Y, obj.Width, obj.Height) = (b.X, b.Y, b.Width, b.Height);
        foreach (var (text, size) in _oldFonts) text.FontSize = size;
    }
}

public sealed class RotateObjects : Command
{
    public List<string> Targets { get; set; } = [];
    /// <summary>Angle to add, in degrees; positive = clockwise. Each object rotates around its own center.</summary>
    public double Degrees { get; set; }

    List<(SceneObject Obj, Matrix3x2 Old)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.rotate: 'targets' is empty. Pass ids or names, e.g. [\"o1\"].");
        _old = Targets.Select(t => doc.FindEditable(t)).Select(o => (o, o.Transform)).ToList();
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

    /// <summary>Output only: id of the created group.</summary>
    public string? Id => _group?.Id;

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _group is null ? [] : [_group.Id];

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.group: 'targets' is empty. Pass the ids or names to group, e.g. [\"o1\",\"o2\"].");
        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        var container = doc.ContainerOf(objs[0])!;
        if (objs.Any(o => doc.ContainerOf(o) != container))
            throw new CommandException("object.group: the objects must be at the same level (same layer or same group).");

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
        // The group takes the place (z-order) of the topmost of the grouped objects.
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
        if (Targets.Count == 0) throw new CommandException("object.ungroup: 'targets' is empty. Pass the id or name of a group.");
        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        if (objs.FirstOrDefault(o => o is not GroupObject) is { } notGroup)
            throw new CommandException($"object.ungroup: '{notGroup.Name ?? notGroup.Id}' is a {notGroup.Type}, not a group.");

        _done = [];
        foreach (var g in objs.Cast<GroupObject>())
        {
            var container = doc.ContainerOf(g)!;
            var index = container.IndexOf(g);
            var old = g.Children.Select(c => c.Transform).ToList();
            // The children absorb the group's transform, so they stay where they are.
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

/// <summary>Replaces the geometry of a path (nodes moved, added, removed...). The accumulated rotation/translation are embedded in the data.</summary>
public sealed class SetPath : Command
{
    public string Target { get; set; } = "";
    /// <summary>New SVG data, in page space (as in get_document).</summary>
    public string D { get; set; } = "";
    /// <summary>Optional (written by the Node tool): one character per node, 'c' cusp, 's' smooth, 'y' symmetric. Ignored if it does not match the nodes of <c>d</c>.</summary>
    public string? Kinds { get; set; }

    PathObject? _obj;
    string? _oldKinds;
    List<PathSeg> _oldSegs = [];
    Box _oldSource, _oldRect;
    Matrix3x2 _oldTransform;

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _obj is null ? [] : [_obj.Id];

    public override void Apply(Document doc)
    {
        var o = doc.FindEditable(Target);
        if (o is not PathObject p)
            throw new CommandException($"path.set: '{Target}' is a {o.Type}, not a path (shape.toPath converts rectangles and ellipses to paths).");
        if (!doc.Pages.SelectMany(pg => pg.Layers).Any(l => l.Objects.Contains(p)))
            throw new CommandException($"path.set: '{Target}' is inside a group: ungroup it first with object.ungroup.");
        var segs = PathData.Parse(D);
        var box = PathData.Bounds(segs)!.Value;
        if (Kinds != null && Kinds.Any(c => c is not ('c' or 's' or 'y')))
            throw new CommandException("path.set: 'kinds' may only contain 'c' (cusp), 's' (smooth) and 'y' (symmetric).");
        var kinds = Kinds != null && Kinds.Length == PathNodes.FromSegments(segs).Sum(sp => sp.Nodes.Count) ? Kinds : null;

        _obj = p;
        _oldKinds = p.NodeKinds;
        (_oldSegs, _oldSource, _oldRect, _oldTransform) = (p.Segments, p.Source, new Box(p.X, p.Y, p.Width, p.Height), p.Transform);
        p.NodeKinds = kinds;
        p.Segments = segs;
        p.Source = box;
        (p.X, p.Y, p.Width, p.Height) = (box.X, box.Y, box.Width, box.Height);
        p.Transform = Matrix3x2.Identity;
    }

    public override void Undo(Document doc)
    {
        var p = _obj!;
        p.NodeKinds = _oldKinds;
        p.Segments = _oldSegs;
        p.Source = _oldSource;
        (p.X, p.Y, p.Width, p.Height) = (_oldRect.X, _oldRect.Y, _oldRect.Width, _oldRect.Height);
        p.Transform = _oldTransform;
    }
}

/// <summary>Converts rectangles, ellipses and texts to paths (same id, name, style and transform), so their nodes can be edited.</summary>
public sealed class ConvertToPath : Command
{
    public List<string> Targets { get; set; } = [];

    List<(List<SceneObject> Container, int Index, SceneObject Old, PathObject New)> _done = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _done.Select(d => d.New.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("shape.toPath: 'targets' is empty. Pass ids or names, e.g. [\"o1\"].");
        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        if (objs.FirstOrDefault(o => o is GroupObject) is { } g)
            throw new CommandException($"shape.toPath: '{g.Name ?? g.Id}' is a group: ungroup it first with object.ungroup.");
        if (objs.FirstOrDefault(o => o is ImageObject) is { } img)
            throw new CommandException($"shape.toPath: '{img.Name ?? img.Id}' is an image: images cannot be converted to paths.");
        var convertible = objs.Where(o => o is RectangleObject or EllipseObject or TextObject).ToList();
        if (convertible.Count == 0) throw new CommandException("shape.toPath: the given objects are already paths.");

        if (_done.Count == 0)
            foreach (var o in convertible)
            {
                List<PathSeg> segs;
                if (o is TextObject t)
                {
                    // The glyphs become curves: positioned and stretched like the text, then the transform stays that of the object.
                    if (t.PathGlyphs != null) segs = PathData.Transform(t.PathGlyphs, t.Fit); // text on a path: the glyphs are already laid out
                    else
                    {
                        var engine = doc.TextEngine ?? throw new CommandException("The font engine is not available.");
                        var outline = engine.Outline(t.Style, t.Lines);
                        segs = PathData.Transform(outline, Matrix3x2.CreateTranslation((float)t.Source.X, (float)t.Source.Y) * t.Fit);
                    }
                    if (!segs.Any(s => s.Op is 'L' or 'C')) throw new CommandException("shape.toPath: the text has no visible characters.");
                }
                else if (o is EllipseObject) segs = PathData.EllipseSegments(o.X, o.Y, o.Width, o.Height);
                else segs = PathData.RectangleSegments(o.X, o.Y, o.Width, o.Height);
                var box = o is TextObject ? PathData.Bounds(segs)!.Value : new Box(o.X, o.Y, o.Width, o.Height);
                var container = doc.ContainerOf(o)!;
                _done.Add((container, container.IndexOf(o), o, new PathObject
                {
                    Id = o.Id, Name = o.Name, Segments = segs, Source = box, X = box.X, Y = box.Y, Width = box.Width, Height = box.Height,
                    Transform = o.Transform, Fill = o.Fill, Stroke = o.Stroke, StrokeWidth = o.StrokeWidth,
                }));
            }
        foreach (var (container, _, old, @new) in _done) container[container.IndexOf(old)] = @new;
    }

    public override void Undo(Document doc)
    {
        foreach (var (container, _, old, @new) in _done) container[container.IndexOf(@new)] = old;
    }
}

/// <summary>Changes the name of an object (empty name = no name). Names like "o12" are reserved for ids.</summary>
public sealed partial class RenameObject : Command
{
    public string Target { get; set; } = "";
    public string? Name { get; set; }

    SceneObject? _obj;
    string? _old;

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _obj is null ? [] : [_obj.Id];

    [System.Text.RegularExpressions.GeneratedRegex(@"^o\d+$")]
    private static partial System.Text.RegularExpressions.Regex IdLike();

    public override void Apply(Document doc)
    {
        var o = doc.Find(Target);
        var name = Name?.Trim();
        if (string.IsNullOrEmpty(name)) name = null;
        if (name != null)
        {
            if (name.Length > 80) throw new CommandException("object.rename: the name can have at most 80 characters.");
            if (IdLike().IsMatch(name))
                throw new CommandException($"object.rename: '{name}' looks like an id (o1, o2...), which is reserved: choose another name.");
        }
        _obj = o;
        _old = o.Name;
        o.Name = name;
    }

    public override void Undo(Document doc) => _obj!.Name = _old;
}
