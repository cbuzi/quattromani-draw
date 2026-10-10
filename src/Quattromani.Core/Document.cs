using System.Numerics;
using System.Text.RegularExpressions;

namespace Quattromani.Core;

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

    /// <summary>Matrice che stira il riquadro <paramref name="src"/> (contenuto) sul riquadro <paramref name="dst"/>.</summary>
    public static Matrix3x2 FitMatrix(Box src, Box dst)
    {
        var sx = src.Width > 1e-9 ? dst.Width / src.Width : 1;
        var sy = src.Height > 1e-9 ? dst.Height / src.Height : 1;
        return new Matrix3x2((float)sx, 0, 0, (float)sy, (float)(dst.X - src.X * sx), (float)(dst.Y - src.Y * sy));
    }

    /// <summary>Rotazione in gradi (oraria sullo schermo, dato che y cresce verso il basso).</summary>
    public double Rotation => Math.Atan2(Transform.M12, Transform.M11) * 180 / Math.PI;
}

public sealed class RectangleObject : SceneObject { public override string Type => "rectangle"; }
public sealed class EllipseObject : SceneObject { public override string Type => "ellipse"; }

/// <summary>
/// Path bezier. I segmenti restano come creati (Source = loro riquadro); (X, Y, Width, Height) è il riquadro su cui
/// vengono stirati, così ridimensionare un path cambia solo quei quattro numeri, come per le altre forme.
/// </summary>
public sealed class PathObject : SceneObject
{
    public override string Type => "path";
    public List<PathSeg> Segments { get; internal set; } = [];
    /// <summary>Tipo di ogni nodo (vedi PathNodes.KindsOf), per lo strumento Nodi; null = dedotto dalla geometria.</summary>
    public string? NodeKinds { get; internal set; }
    public Box Source { get; internal set; }
    public Matrix3x2 Fit => FitMatrix(Source, new(X, Y, Width, Height));
}

/// <summary>Gruppo: i figli sono nello spazio del gruppo (stirato come per PathObject, poi Transform).</summary>
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
    public List<SceneObject> Objects { get; } = [];
}

public sealed class Page
{
    public string Name { get; set; } = "Page 1";
    /// <summary>Dimensioni in px (96 dpi); di default un A4 verticale. L'origine è l'angolo in alto a sinistra.</summary>
    public double Width { get; set; } = Units.ToPx(210, "mm");
    public double Height { get; set; } = Units.ToPx(297, "mm");
    public List<Layer> Layers { get; } = [new("Layer 1")];
}

public sealed partial class Document
{
    int _nextId;

    /// <summary>Contatore degli id (per salvare e riaprire senza collisioni).</summary>
    public int NextIdValue { get => _nextId; internal set => _nextId = value; }

    /// <summary>Misura e contorni del testo, forniti dall'app; senza, i comandi sul testo danno un errore esplicito.</summary>
    public ITextEngine? TextEngine { get; set; }

    /// <summary>Incrementata dal CommandBus per ogni comando applicato.</summary>
    public long Version { get; internal set; }
    public List<Page> Pages { get; } = [new()];

    public IEnumerable<SceneObject> Objects =>
        Pages.SelectMany(p => p.Layers).SelectMany(l => l.Objects);

    /// <summary>Tutti gli oggetti, figli dei gruppi compresi.</summary>
    public IEnumerable<SceneObject> AllObjects => Objects.SelectMany(SelfAndDescendants);

    public static IEnumerable<SceneObject> SelfAndDescendants(SceneObject o) =>
        o is GroupObject g ? g.Children.SelectMany(SelfAndDescendants).Prepend(o) : [o];

    /// <summary>La lista (layer o figli di un gruppo) che contiene l'oggetto.</summary>
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

    /// <summary>Matrice dallo spazio del contenitore dell'oggetto (layer o gruppo) allo spazio pagina.</summary>
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

    /// <summary>Riquadro dell'oggetto nello spazio pagina (anche se sta dentro dei gruppi trasformati).</summary>
    public Box PageBounds(SceneObject o) =>
        SceneObject.BoundsOf(o.X, o.Y, o.Width, o.Height, o.Transform * ParentMatrix(o));

    /// <summary>Sostituisce il contenuto con quello di un altro documento (apri, nuovo). La versione e il motore dei font restano.</summary>
    internal void ReplaceWith(Document other)
    {
        Pages.Clear();
        Pages.AddRange(other.Pages);
        _nextId = other._nextId;
    }

    internal string NewId() => "o" + ++_nextId;

    /// <summary>Trova un oggetto per id o per name.</summary>
    public SceneObject Find(string idOrName)
    {
        var obj = AllObjects.FirstOrDefault(o => o.Id == idOrName);
        if (obj != null) return obj;

        var byName = AllObjects.Where(o => o.Name == idOrName).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
            throw new CommandException(
                $"Il nome '{idOrName}' e' ambiguo (id: {string.Join(", ", byName.Select(o => o.Id))}). Usa l'id.");

        var known = string.Join(", ", AllObjects.Take(20).Select(o => o.Name is null ? o.Id : $"{o.Id}/{o.Name}"));
        throw new CommandException(
            $"Oggetto '{idOrName}' non trovato. Oggetti presenti: {(known.Length == 0 ? "nessuno" : known)}. Usa get_document per l'elenco.");
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$")]
    private static partial Regex ColorRegex();

    /// <summary>Valida un colore o un gradiente (stile CSS); "none" diventa null (nessun colore). I gradienti vengono riscritti in forma canonica.</summary>
    internal static string? ParseColor(string value, string field)
    {
        if (value == "none") return null;
        if (Gradient.IsGradient(value)) return Gradient.Format(Gradient.Parse(value, field));
        if (Pattern.IsPattern(value)) return Pattern.Format(Pattern.Parse(value, field));
        if (ColorRegex().IsMatch(value)) return value;
        throw new CommandException(
            $"'{field}': colore '{value}' non valido. Usa \"#RRGGBB\", \"#RRGGBBAA\", \"none\", un gradiente come \"linear-gradient(90deg, #FF0000, #0000FF)\" o \"radial-gradient(#FFFFFF, #000000)\", oppure un motivo come \"pattern(stripes, #FF0000, #FFFFFF, 8)\".");
    }
}
