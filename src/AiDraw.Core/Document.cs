using System.Numerics;
using System.Text.RegularExpressions;

namespace AiDraw.Core;

public sealed class CommandException(string message) : Exception(message);

public readonly record struct Box(double X, double Y, double Width, double Height);

/// <summary>Punti di ancoraggio nominati sul rettangolo di un oggetto (frazioni di larghezza e altezza).</summary>
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
        throw new CommandException($"anchor '{name}' non valido. Valori: {string.Join(", ", All.Select(a => $"\"{a.Name}\""))}.");
    }

    public static string NameOf(double x, double y) => All.First(a => a.X == x && a.Y == y).Name;
}

public abstract class SceneObject
{
    public string Id { get; internal set; } = "";
    public string? Name { get; set; }
    public abstract string Type { get; }

    // Geometria locale (prima della trasformazione).
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public Matrix3x2 Transform { get; set; } = Matrix3x2.Identity;

    // Stile. Colori come "#RRGGBB" o "#RRGGBBAA"; null = nessuno.
    public string? Fill { get; set; }
    public string? Stroke { get; set; }
    public double StrokeWidth { get; set; }

    /// <summary>Bounding box nello spazio del layer, dopo la trasformazione.</summary>
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

    /// <summary>Rotazione in gradi (oraria sullo schermo, dato che y cresce verso il basso).</summary>
    public double Rotation => Math.Atan2(Transform.M12, Transform.M11) * 180 / Math.PI;
}

public sealed class RectangleObject : SceneObject { public override string Type => "rectangle"; }
public sealed class EllipseObject : SceneObject { public override string Type => "ellipse"; }

public sealed class Layer(string name)
{
    public string Name { get; set; } = name;
    public List<SceneObject> Objects { get; } = [];
}

public sealed class Page
{
    public string Name { get; set; } = "Page 1";
    public List<Layer> Layers { get; } = [new("Layer 1")];
}

public sealed partial class Document
{
    int _nextId;

    /// <summary>Incrementata dal CommandBus per ogni comando applicato.</summary>
    public long Version { get; internal set; }
    public List<Page> Pages { get; } = [new()];

    public IEnumerable<SceneObject> Objects =>
        Pages.SelectMany(p => p.Layers).SelectMany(l => l.Objects);

    internal string NewId() => "o" + ++_nextId;

    /// <summary>Trova un oggetto per id o per name.</summary>
    public SceneObject Find(string idOrName)
    {
        var obj = Objects.FirstOrDefault(o => o.Id == idOrName);
        if (obj != null) return obj;

        var byName = Objects.Where(o => o.Name == idOrName).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
            throw new CommandException(
                $"Il nome '{idOrName}' e' ambiguo (id: {string.Join(", ", byName.Select(o => o.Id))}). Usa l'id.");

        var known = string.Join(", ", Objects.Take(20).Select(o => o.Name is null ? o.Id : $"{o.Id}/{o.Name}"));
        throw new CommandException(
            $"Oggetto '{idOrName}' non trovato. Oggetti presenti: {(known.Length == 0 ? "nessuno" : known)}. Usa get_document per l'elenco.");
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$")]
    private static partial Regex ColorRegex();

    /// <summary>Valida un colore; "none" diventa null (nessun colore).</summary>
    internal static string? ParseColor(string value, string field)
    {
        if (value == "none") return null;
        if (ColorRegex().IsMatch(value)) return value;
        throw new CommandException($"'{field}': colore '{value}' non valido. Usa \"#RRGGBB\", \"#RRGGBBAA\" oppure \"none\".");
    }
}
