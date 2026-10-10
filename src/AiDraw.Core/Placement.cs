using System.Numerics;
using System.Text.Json.Serialization;

namespace AiDraw.Core;

/// <summary>
/// Posizionamento relativo ad altri oggetti, pensato per l'LLM. Si indica esattamente UN riferimento tra
/// inside, rightOf, leftOf, above, below, alignTo (id o nome dell'oggetto di riferimento).
/// Il punto <c>anchor</c> dell'oggetto (default dipende dal riferimento) viene portato sul punto <c>refAnchor</c>
/// del riferimento, poi si applica <c>margin</c>. Gli anchor sono quelli di <see cref="Anchor"/>
/// (top-left, top, top-right, left, center, right, bottom-left, bottom, bottom-right).
/// </summary>
public sealed class Placement
{
    /// <summary>Dentro il riferimento, con l'anchor (default center) a filo del riferimento, rientrando di margin.</summary>
    public string? Inside { get; set; }
    /// <summary>A destra del riferimento, a distanza margin, centrato in verticale (o secondo anchor/refAnchor).</summary>
    public string? RightOf { get; set; }
    public string? LeftOf { get; set; }
    public string? Above { get; set; }
    public string? Below { get; set; }
    /// <summary>Allinea l'anchor dell'oggetto a quello del riferimento (refAnchor, se diverso).</summary>
    public string? AlignTo { get; set; }

    /// <summary>Punto dell'oggetto da posizionare.</summary>
    public string? Anchor { get; set; }
    /// <summary>Punto del riferimento a cui agganciarlo; di default quello del riferimento è lo stesso di anchor (inside, alignTo) o il lato opposto (rightOf, leftOf, above, below).</summary>
    public string? RefAnchor { get; set; }
    /// <summary>Distanza in unità documento: rientro per inside e alignTo, spazio vuoto per rightOf, leftOf, above, below.</summary>
    public double Margin { get; set; }

    /// <summary>Spostamento (dx, dy) da applicare all'oggetto che occupa <paramref name="own"/> (spazio pagina) per rispettare il posizionamento.</summary>
    internal (double Dx, double Dy) Resolve(Document doc, Box own, ICollection<SceneObject>? exclude = null)
    {
        var given = new[] { ("inside", Inside), ("rightOf", RightOf), ("leftOf", LeftOf), ("above", Above), ("below", Below), ("alignTo", AlignTo) }
            .Where(r => r.Item2 != null).ToList();
        if (given.Count != 1)
            throw new CommandException(
                $"rel: indica esattamente un riferimento tra inside, rightOf, leftOf, above, below, alignTo (ne ho trovati {given.Count}). Esempio: {{\"inside\":\"cielo\",\"anchor\":\"top-right\",\"margin\":20}}.");
        var (kind, refName) = given[0];

        var reference = doc.Find(refName!);
        if (exclude != null && exclude.Contains(reference))
            throw new CommandException($"rel: '{refName}' e' tra gli oggetti da posizionare: non puo' essere il proprio riferimento.");
        var rb = doc.PageBounds(reference);

        // Anchor di default e direzione del margine per ogni tipo di riferimento.
        string defAnchor, defRef;
        (double X, double Y) dir; // direzione in cui agisce il margine
        switch (kind)
        {
            case "rightOf": (defAnchor, defRef, dir) = ("left", "right", (1, 0)); break;
            case "leftOf": (defAnchor, defRef, dir) = ("right", "left", (-1, 0)); break;
            case "below": (defAnchor, defRef, dir) = ("top", "bottom", (0, 1)); break;
            case "above": (defAnchor, defRef, dir) = ("bottom", "top", (0, -1)); break;
            default: (defAnchor, defRef, dir) = ("center", "center", (0, 0)); break; // inside e alignTo: la direzione dipende dall'anchor, sotto
        }

        var (ax, ay) = Core.Anchor.Parse(Anchor ?? defAnchor);
        string refAnchorName;
        if (kind == "inside")
        {
            if (RefAnchor != null) throw new CommandException("rel: con 'inside' usa solo 'anchor' (il punto dell'oggetto che va a filo del riferimento); 'refAnchor' serve con 'alignTo'.");
            refAnchorName = Anchor ?? defAnchor;
        }
        else refAnchorName = RefAnchor ?? (kind == "alignTo" ? Anchor ?? defAnchor : defRef);
        var (rx, ry) = Core.Anchor.Parse(refAnchorName);

        if (kind == "inside" || kind == "alignTo" && RefAnchor == null)
            dir = (ax == 0 ? 1 : ax == 1 ? -1 : 0, ay == 0 ? 1 : ay == 1 ? -1 : 0); // rientro verso l'interno
        else if (kind == "alignTo")
            dir = (rx == 0 ? -1 : rx == 1 ? 1 : 0, ry == 0 ? -1 : ry == 1 ? 1 : 0);   // verso l'esterno del riferimento

        var tx = rb.X + rx * rb.Width + Margin * dir.X;
        var ty = rb.Y + ry * rb.Height + Margin * dir.Y;
        var px = own.X + ax * own.Width;
        var py = own.Y + ay * own.Height;
        return (tx - px, ty - py);
    }
}

/// <summary>Sposta oggetti esistenti con un posizionamento relativo; più oggetti si muovono insieme, come un blocco unico.</summary>
public sealed class PlaceObjects : Command
{
    public List<string> Targets { get; set; } = [];
    public Placement Rel { get; set; } = new();

    List<(SceneObject Obj, Matrix3x2 Old)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.place: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\"].");
        var objs = Targets.Select(t => doc.Find(t)).Distinct().ToList();

        var boxes = objs.Select(doc.PageBounds).ToList();
        var x0 = boxes.Min(b => b.X); var y0 = boxes.Min(b => b.Y);
        var union = new Box(x0, y0, boxes.Max(b => b.X + b.Width) - x0, boxes.Max(b => b.Y + b.Height) - y0);
        var (dx, dy) = Rel.Resolve(doc, union, objs);

        // Lo spostamento è in spazio pagina: per gli oggetti dentro un gruppo va riportato nello spazio del gruppo.
        _old = objs.Select(o => (o, o.Transform)).ToList();
        foreach (var (obj, _) in _old) obj.Transform = Shift.Apply(doc, obj, dx, dy);
    }

    public override void Undo(Document doc)
    {
        foreach (var (obj, old) in _old) obj.Transform = old;
    }
}
