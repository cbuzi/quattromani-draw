using System.Numerics;
using System.Text.RegularExpressions;

namespace Quattromani.Core;

public sealed class CommandException(string message) : Exception(message);

public readonly record struct Box(double X, double Y, double Width, double Height);

/// <summary>Named anchor points on an object's rectangle (fractions of width and height).</summary>
public static class Anchor
{
    public static readonly (string Name, double X, double Y)[] All =
    [
        ("top-left", 0, 0), ("top", .5, 0), ("top-right", 1, 0),
        ("left", 0, .5), ("center", .5, .5), ("right", 1, .5),
        ("bottom-left", 0, 1), ("bottom", .5, 1), ("bottom-right", 1, 1),
    ];

    public static (double X, double Y) Parse(string name)
    {
        foreach (var a in All) if (a.Name == name) return (a.X, a.Y);
        throw new CommandException($"anchor '{name}' is not valid. Values: {string.Join(", ", All.Select(a => $"\"{a.Name}\""))}.");
    }

    public static string NameOf(double x, double y) => All.First(a => a.X == x && a.Y == y).Name;
}

public abstract class SceneObject
{
    public string Id { get; internal set; } = "";
    public string? Name { get; set; }
    public abstract string Type { get; }

    // Local geometry (before the transform).
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public Matrix3x2 Transform { get; set; } = Matrix3x2.Identity;

    /// <summary>Hidden: not drawn, not exported and not selectable with the mouse (commands still see it).</summary>
    public bool Hidden { get; set; }
    /// <summary>Locked: commands that modify it fail and the mouse ignores it. Also applies to the content of a locked group and to the objects of a locked layer.</summary>
    public bool Locked { get; set; }

    // Style. Colors as "#RRGGBB" or "#RRGGBBAA"; null = none.
    public string? Fill { get; set; }
    public string? Stroke { get; set; }
    public double StrokeWidth { get; set; }
    /// <summary>Dash pattern in px (dash, gap, dash, gap...); null = solid line.</summary>
    public double[]? StrokeDash { get; set; }
    /// <summary>"butt", "round" or "square".</summary>
    public string StrokeCap { get; set; } = "butt";
    /// <summary>"miter", "round" or "bevel".</summary>
    public string StrokeJoin { get; set; } = "miter";
    /// <summary>0 (invisible) to 1 (opaque). Applies to the whole object, fill and stroke together (for a group, to all its content together).</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>Copies fill, stroke and opacity from another object.</summary>
    public void CopyStyleFrom(SceneObject o) =>
        (Fill, Stroke, StrokeWidth, StrokeDash, StrokeCap, StrokeJoin, Opacity) = (o.Fill, o.Stroke, o.StrokeWidth, o.StrokeDash, o.StrokeCap, o.StrokeJoin, o.Opacity);

    /// <summary>Bounding box in the layer's space, after the transform.</summary>
    public Box Bounds => BoundsOf(X, Y, Width, Height, Transform);

    public static Box BoundsOf(double x, double y, double w, double h, Matrix3x2 m)
    {
        Span<Vector2> p =
        [
            Vector2.Transform(new((float)x, (float)y), m),
            Vector2.Transform(new((float)(x + w), (float)y), m),
            Vector2.Transform(new((float)x, (float)(y + h)), m),
            Vector2.Transform(new((float)(x + w), (float)(y + h)), m),
        ];
        float minX = p[0].X, maxX = minX, minY = p[0].Y, maxY = minY;
        foreach (var v in p[1..])
        {
            minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
            minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
        }
        return new(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Matrix that stretches the box <paramref name="src"/> (content) onto the box <paramref name="dst"/>.</summary>
    public static Matrix3x2 FitMatrix(Box src, Box dst)
    {
        var sx = src.Width > 1e-9 ? dst.Width / src.Width : 1;
        var sy = src.Height > 1e-9 ? dst.Height / src.Height : 1;
        return new Matrix3x2((float)sx, 0, 0, (float)sy, (float)(dst.X - src.X * sx), (float)(dst.Y - src.Y * sy));
    }

    /// <summary>Rotation in degrees (clockwise on screen, since y grows downwards).</summary>
    public double Rotation => Math.Atan2(Transform.M12, Transform.M11) * 180 / Math.PI;
}

public sealed class RectangleObject : SceneObject
{
    public override string Type => "rectangle";
    /// <summary>Corner radius in px (0 = square corners); never more than half the shorter side.</summary>
    public double CornerRadius { get; set; }
    public double EffectiveRadius => Math.Max(0, Math.Min(CornerRadius, Math.Min(Width, Height) / 2));
    public List<PathSeg> Outline() => PathData.RectangleSegments(X, Y, Width, Height, EffectiveRadius);
}
public sealed class EllipseObject : SceneObject { public override string Type => "ellipse"; }

/// <summary>Embedded raster image: the bytes of the original file (PNG, JPEG, GIF, WebP, BMP). Geometry like the rectangle; fill = background, stroke = frame.</summary>
public sealed class ImageObject : SceneObject
{
    public override string Type => "image";
    public byte[] Data { get; internal set; } = [];
    public int PixelWidth { get; internal set; }
    public int PixelHeight { get; internal set; }
}

/// <summary>
/// Bezier path. The segments stay as created (Source = their box); (X, Y, Width, Height) is the box they are
/// stretched onto, so resizing a path only changes those four numbers, like the other shapes.
/// </summary>
public sealed class PathObject : SceneObject
{
    public override string Type => "path";
    public List<PathSeg> Segments { get; internal set; } = [];
    /// <summary>Kind of each node (see PathNodes.KindsOf), for the Node tool; null = derived from the geometry.</summary>
    public string? NodeKinds { get; internal set; }
    public Box Source { get; internal set; }
    public Matrix3x2 Fit => FitMatrix(Source, new(X, Y, Width, Height));
}

/// <summary>Group: children are in the group's space (stretched as for PathObject, then Transform).</summary>
public sealed class GroupObject : SceneObject
{
    public override string Type => "group";
    public List<SceneObject> Children { get; } = [];
    public Box Source { get; internal set; }
    public Matrix3x2 Fit => FitMatrix(Source, new(X, Y, Width, Height));
}

public sealed class Layer(string name)
{
    public string Name { get; set; } = name;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public List<SceneObject> Objects { get; } = [];
}

public sealed class Page
{
    public string Name { get; set; } = "Page 1";
    /// <summary>Size in px (96 dpi); A4 portrait by default. The origin is the top-left corner.</summary>
    public double Width { get; set; } = Units.ToPx(210, "mm");
    public double Height { get; set; } = Units.ToPx(297, "mm");
    public List<Layer> Layers { get; } = [new("Layer 1")];
    Layer? _active;
    /// <summary>The layer where new objects go (if none other is given); the first by default.</summary>
    public Layer ActiveLayer { get => _active != null && Layers.Contains(_active) ? _active : Layers[0]; internal set => _active = value; }
    /// <summary>Guide lines (neither printed nor exported).</summary>
    public List<Guide> Guides { get; } = [];
}

public sealed partial class Document
{
    int _nextId;

    /// <summary>Id counter (to save and reopen without collisions).</summary>
    public int NextIdValue { get => _nextId; internal set => _nextId = value; }

    /// <summary>Measurement and outlines of text, provided by the app; without it, text commands give an explicit error.</summary>
    public ITextEngine? TextEngine { get; set; }

    /// <summary>Size in pixels of an image (null if the bytes are not a readable image); provided by the app.</summary>
    public Func<byte[], (int Width, int Height)?>? ImageProbe { get; set; }

    /// <summary>RGBA pixels (non-premultiplied, 4 bytes each) of an image, downscaled if its longest side exceeds the given size; provided by the app.</summary>
    public Func<byte[], int, (int Width, int Height, byte[] Rgba)?>? ImagePixels { get; set; }

    /// <summary>Incremented by the CommandBus for every applied command.</summary>
    public long Version { get; internal set; }
    public List<Page> Pages { get; } = [new()];

    public IEnumerable<SceneObject> Objects =>
        Pages.SelectMany(p => p.Layers).SelectMany(l => l.Objects);

    /// <summary>The objects to draw (visible layers, non-hidden objects), from bottom to top.</summary>
    public IEnumerable<SceneObject> RenderObjects =>
        Pages.SelectMany(p => p.Layers).Where(l => l.Visible).SelectMany(l => l.Objects).Where(o => !o.Hidden);

    /// <summary>The objects the mouse can pick: the drawn ones, except locked ones and those in locked layers.</summary>
    public IEnumerable<SceneObject> PickableObjects =>
        Pages.SelectMany(p => p.Layers).Where(l => l.Visible && !l.Locked).SelectMany(l => l.Objects).Where(o => !o.Hidden && !o.Locked);

    /// <summary>The layer that contains the object (also inside groups).</summary>
    public Layer? LayerOf(SceneObject target)
    {
        foreach (var layer in Pages.SelectMany(p => p.Layers))
            if (layer.Objects.Any(o => SelfAndDescendants(o).Contains(target))) return layer;
        return null;
    }

    /// <summary>The object is locked itself, or a group containing it is, or its layer.</summary>
    public bool IsLocked(SceneObject target)
    {
        static bool InGroup(IEnumerable<SceneObject> list, SceneObject t, bool locked)
        {
            foreach (var o in list)
            {
                if (o == t) return locked || o.Locked;
                if (o is GroupObject g && g.Children.Any(c => SelfAndDescendants(c).Contains(t))) return InGroup(g.Children, t, locked || o.Locked);
            }
            return false;
        }

        var layer = LayerOf(target);
        return layer != null && InGroup(layer.Objects, target, layer.Locked);
    }

    /// <summary>Like <see cref="Find"/>, for objects a command is about to modify: if locked it explains how to unlock it.</summary>
    public SceneObject FindEditable(string idOrName)
    {
        var obj = Find(idOrName);
        if (IsLocked(obj))
            throw new CommandException($"Object '{obj.Name ?? obj.Id}' is locked (itself, a group containing it, or its layer). Unlock it with object.state {{\"targets\":[\"{obj.Id}\"],\"locked\":false}} or layer.set, or ask the user.");
        return obj;
    }

    /// <summary>Finds a layer by name (case-insensitive).</summary>
    public Layer FindLayer(string name)
    {
        var layers = Pages[0].Layers;
        return layers.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new CommandException($"Layer '{name}' not found. Layers (bottom to top): {string.Join(", ", layers.Select(l => $"\"{l.Name}\""))}.");
    }

    /// <summary>The layer where a command creates new objects: the given one or the active one; it must be visible and unlocked.</summary>
    public Layer TargetLayer(string? name)
    {
        var layer = name is null ? Pages[0].ActiveLayer : FindLayer(name);
        if (layer.Locked || !layer.Visible)
            throw new CommandException($"Layer '{layer.Name}' is {(layer.Locked ? "locked" : "hidden")}: nothing can be added to it. Give another layer (\"layer\":\"name\"), create one with layer.add, or {(layer.Locked ? "unlock" : "show")} this one with layer.set.");
        return layer;
    }

    /// <summary>All objects, group children included.</summary>
    public IEnumerable<SceneObject> AllObjects => Objects.SelectMany(SelfAndDescendants);

    public static IEnumerable<SceneObject> SelfAndDescendants(SceneObject o) =>
        o is GroupObject g ? g.Children.SelectMany(SelfAndDescendants).Prepend(o) : [o];

    /// <summary>The list (layer or group children) that contains the object.</summary>
    public List<SceneObject>? ContainerOf(SceneObject target)
    {
        static List<SceneObject>? Search(List<SceneObject> list, SceneObject t)
        {
            if (list.Contains(t)) return list;
            foreach (var g in list.OfType<GroupObject>())
                if (Search(g.Children, t) is { } found) return found;
            return null;
        }

        foreach (var layer in Pages.SelectMany(p => p.Layers))
            if (Search(layer.Objects, target) is { } found) return found;
        return null;
    }

    /// <summary>Matrix from the space of the object's container (layer or group) to page space.</summary>
    public Matrix3x2 ParentMatrix(SceneObject target)
    {
        static bool Find(List<SceneObject> list, SceneObject t, Matrix3x2 parent, out Matrix3x2 result)
        {
            if (list.Contains(t)) { result = parent; return true; }
            foreach (var g in list.OfType<GroupObject>())
                if (Find(g.Children, t, g.Fit * g.Transform * parent, out result)) return true;
            result = Matrix3x2.Identity;
            return false;
        }

        foreach (var layer in Pages.SelectMany(p => p.Layers))
            if (Find(layer.Objects, target, Matrix3x2.Identity, out var m)) return m;
        return Matrix3x2.Identity;
    }

    /// <summary>Box of the object in page space (even if it is inside transformed groups).</summary>
    public Box PageBounds(SceneObject o) =>
        SceneObject.BoundsOf(o.X, o.Y, o.Width, o.Height, o.Transform * ParentMatrix(o));

    /// <summary>Replaces the content with that of another document (open, new). The version and the font engine stay.</summary>
    internal void ReplaceWith(Document other)
    {
        Pages.Clear();
        Pages.AddRange(other.Pages);
        _nextId = other._nextId;
    }

    /// <summary>Recomputes texts on paths (they change when the path or the text changes; commands do not deal with it).</summary>
    internal void RefreshPathTexts()
    {
        foreach (var t in AllObjects.OfType<TextObject>().Where(t => t.OnPath != null).ToList())
            TextOnPathLayout.Update(this, t);
    }

    internal string NewId() => "o" + ++_nextId;

    /// <summary>Finds an object by id or by name.</summary>
    public SceneObject Find(string idOrName)
    {
        var obj = AllObjects.FirstOrDefault(o => o.Id == idOrName);
        if (obj != null) return obj;

        var byName = AllObjects.Where(o => o.Name == idOrName).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
            throw new CommandException(
                $"The name '{idOrName}' is ambiguous (ids: {string.Join(", ", byName.Select(o => o.Id))}). Use the id.");

        var known = string.Join(", ", AllObjects.Take(20).Select(o => o.Name is null ? o.Id : $"{o.Id}/{o.Name}"));
        throw new CommandException(
            $"Object '{idOrName}' not found. Existing objects: {(known.Length == 0 ? "none" : known)}. Use get_document for the list.");
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$")]
    private static partial Regex ColorRegex();

    /// <summary>Validates a color or gradient (CSS style); "none" becomes null (no color). Gradients are rewritten in canonical form.</summary>
    internal static string? ParseColor(string value, string field)
    {
        if (value == "none") return null;
        if (Gradient.IsGradient(value)) return Gradient.Format(Gradient.Parse(value, field));
        if (Pattern.IsPattern(value)) return Pattern.Format(Pattern.Parse(value, field));
        if (ColorRegex().IsMatch(value)) return value;
        throw new CommandException(
            $"'{field}': color '{value}' is not valid. Use \"#RRGGBB\", \"#RRGGBBAA\", \"none\", a gradient like \"linear-gradient(90deg, #FF0000, #0000FF)\" or \"radial-gradient(#FFFFFF, #000000)\", or a pattern like \"pattern(stripes, #FF0000, #FFFFFF, 8)\".");
    }
}
