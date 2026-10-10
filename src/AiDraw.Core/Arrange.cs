using System.Numerics;
using System.Text.Json.Serialization;

namespace AiDraw.Core;

/// <summary>Spostamento di oggetti espresso nello spazio pagina, riportato nello spazio del loro contenitore (anche dentro gruppi ruotati o scalati).</summary>
static class Shift
{
    public static Matrix3x2 Apply(Document doc, SceneObject o, double dx, double dy)
    {
        var local = Matrix3x2.Invert(doc.ParentMatrix(o), out var inv)
            ? Vector2.TransformNormal(new((float)dx, (float)dy), inv)
            : new Vector2((float)dx, (float)dy);
        return o.Transform * Matrix3x2.CreateTranslation(local);
    }
}

/// <summary>
/// Ordine di sovrapposizione (z-order) tra fratelli dello stesso livello. <c>action</c>: "front" (in primo piano), "back" (sullo sfondo),
/// "forward" / "backward" (di un posto), oppure <c>above</c> / <c>below</c> = subito sopra o sotto un altro oggetto.
/// Più oggetti mantengono il loro ordine relativo.
/// </summary>
public sealed class OrderObjects : Command
{
    public List<string> Targets { get; set; } = [];
    public string? Action { get; set; }
    public string? Above { get; set; }
    public string? Below { get; set; }

    List<(List<SceneObject> Container, List<SceneObject> Old)> _old = [];

    List<string> _ids = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _ids;

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.order: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\"].");
        var given = new[] { Action, Above, Below }.Count(x => x != null);
        if (given != 1)
            throw new CommandException("object.order: indica esattamente uno tra 'action' (front, back, forward, backward), 'above' e 'below'. Esempio: {\"cmd\":\"object.order\",\"targets\":[\"o3\"],\"action\":\"front\"}.");
        if (Action is not (null or "front" or "back" or "forward" or "backward"))
            throw new CommandException($"object.order: action '{Action}' non valida. Valori: \"front\", \"back\", \"forward\", \"backward\".");

        var objs = Targets.Select(t => doc.Find(t)).Distinct().ToList();
        _ids = objs.Select(o => o.Id).ToList();
        SceneObject? reference = Above != null ? doc.Find(Above) : Below != null ? doc.Find(Below) : null;
        if (reference != null && objs.Contains(reference))
            throw new CommandException($"object.order: '{reference.Name ?? reference.Id}' e' tra gli oggetti da spostare: non puo' essere il proprio riferimento.");

        var byContainer = objs.GroupBy(o => doc.ContainerOf(o)!).ToList();
        if (reference != null && byContainer.Any(g => !ReferenceEquals(g.Key, doc.ContainerOf(reference))))
            throw new CommandException("object.order: con 'above' e 'below' gli oggetti devono stare allo stesso livello del riferimento (stesso layer o stesso gruppo).");

        _old = byContainer.Select(g => (g.Key, g.Key.ToList())).ToList();
        foreach (var g in byContainer)
        {
            var list = g.Key;
            var set = g.ToHashSet();
            var moving = list.Where(set.Contains).ToList(); // in ordine di sovrapposizione attuale
            var others = list.Where(o => !set.Contains(o)).ToList();
            List<SceneObject> result;
            switch (Action, reference)
            {
                case ("front", _): result = [.. others, .. moving]; break;
                case ("back", _): result = [.. moving, .. others]; break;
                case ("forward", _):
                    result = [.. list];
                    for (var i = result.Count - 2; i >= 0; i--)
                        if (set.Contains(result[i]) && !set.Contains(result[i + 1])) (result[i], result[i + 1]) = (result[i + 1], result[i]);
                    break;
                case ("backward", _):
                    result = [.. list];
                    for (var i = 1; i < result.Count; i++)
                        if (set.Contains(result[i]) && !set.Contains(result[i - 1])) (result[i], result[i - 1]) = (result[i - 1], result[i]);
                    break;
                default:
                    result = [.. others];
                    var at = result.IndexOf(reference!);
                    result.InsertRange(Above != null ? at + 1 : at, moving);
                    break;
            }
            list.Clear();
            list.AddRange(result);
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var (container, old) in _old)
        {
            container.Clear();
            container.AddRange(old);
        }
    }
}

/// <summary>
/// Allinea ogni oggetto, singolarmente, a un riferimento: x = "left" | "center" | "right", y = "top" | "middle" | "bottom" (anche entrambi).
/// <c>to</c>: "selection" (il riquadro della selezione; è il default con più oggetti), "page" (default con un oggetto solo) oppure un oggetto.
/// </summary>
public sealed class AlignObjects : Command
{
    public List<string> Targets { get; set; } = [];
    public string? X { get; set; }
    public string? Y { get; set; }
    public string? To { get; set; }

    List<(SceneObject Obj, Matrix3x2 Old)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.align: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\",\"o2\"].");
        if (X is null && Y is null)
            throw new CommandException("object.align: indica 'x' (left, center, right) e/o 'y' (top, middle, bottom). Esempio: {\"cmd\":\"object.align\",\"targets\":[\"a\",\"b\"],\"x\":\"center\"}.");
        if (X is not (null or "left" or "center" or "right")) throw new CommandException($"object.align: x '{X}' non valido. Valori: \"left\", \"center\", \"right\".");
        if (Y is not (null or "top" or "middle" or "bottom")) throw new CommandException($"object.align: y '{Y}' non valido. Valori: \"top\", \"middle\", \"bottom\".");

        var objs = Targets.Select(t => doc.Find(t)).Distinct().ToList();
        var boxes = objs.Select(doc.PageBounds).ToList();

        Box reference;
        var to = To ?? (objs.Count == 1 ? "page" : "selection");
        if (to == "page") reference = new Box(0, 0, doc.Pages[0].Width, doc.Pages[0].Height);
        else if (to == "selection")
        {
            var x0 = boxes.Min(b => b.X); var y0 = boxes.Min(b => b.Y);
            reference = new Box(x0, y0, boxes.Max(b => b.X + b.Width) - x0, boxes.Max(b => b.Y + b.Height) - y0);
        }
        else
        {
            var r = doc.Find(to);
            if (objs.Contains(r)) throw new CommandException($"object.align: '{to}' e' tra gli oggetti da allineare: non puo' essere il proprio riferimento.");
            reference = doc.PageBounds(r);
        }

        _old = [];
        for (var i = 0; i < objs.Count; i++)
        {
            var (b, o) = (boxes[i], objs[i]);
            var dx = X switch
            {
                "left" => reference.X - b.X,
                "center" => reference.X + reference.Width / 2 - (b.X + b.Width / 2),
                "right" => reference.X + reference.Width - (b.X + b.Width),
                _ => 0,
            };
            var dy = Y switch
            {
                "top" => reference.Y - b.Y,
                "middle" => reference.Y + reference.Height / 2 - (b.Y + b.Height / 2),
                "bottom" => reference.Y + reference.Height - (b.Y + b.Height),
                _ => 0,
            };
            _old.Add((o, o.Transform));
            o.Transform = Shift.Apply(doc, o, dx, dy);
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var (obj, old) in _old) obj.Transform = old;
    }
}

/// <summary>
/// Distribuisce gli oggetti lungo un asse. mode "gaps" (default): spazi uguali tra un oggetto e il successivo, con il primo e l'ultimo fermi
/// (oppure con <c>gap</c> esplicito, partendo dal primo); mode "centers": centri equidistanti, primo e ultimo fermi.
/// </summary>
public sealed class DistributeObjects : Command
{
    public List<string> Targets { get; set; } = [];
    public string Axis { get; set; } = "";
    public string? Mode { get; set; }
    public double? Gap { get; set; }

    List<(SceneObject Obj, Matrix3x2 Old)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.distribute: 'targets' e' vuoto. Passa id o nomi di almeno 3 oggetti.");
        if (Axis is not ("x" or "y"))
            throw new CommandException("object.distribute: 'axis' deve essere \"x\" (in orizzontale) o \"y\" (in verticale). Esempio: {\"cmd\":\"object.distribute\",\"targets\":[\"a\",\"b\",\"c\"],\"axis\":\"x\"}.");
        var mode = Mode ?? "gaps";
        if (mode is not ("gaps" or "centers")) throw new CommandException($"object.distribute: mode '{Mode}' non valido. Valori: \"gaps\", \"centers\".");
        if (Gap != null && mode != "gaps") throw new CommandException("object.distribute: 'gap' si usa solo con mode \"gaps\".");

        var objs = Targets.Select(t => doc.Find(t)).Distinct().ToList();
        if (objs.Count < (Gap != null ? 2 : 3))
            throw new CommandException(Gap != null
                ? "object.distribute: servono almeno 2 oggetti."
                : "object.distribute: servono almeno 3 oggetti (con 'gap' esplicito bastano 2).");

        bool horizontal = Axis == "x";
        // (inizio, fine) di ogni oggetto sull'asse, in ordine di posizione
        var items = objs.Select(o => (Obj: o, B: doc.PageBounds(o)))
            .Select(e => (e.Obj, Start: horizontal ? e.B.X : e.B.Y, Size: horizontal ? e.B.Width : e.B.Height))
            .OrderBy(e => e.Start + e.Size / 2).ToList();

        var target = new double[items.Count]; // nuovo inizio di ogni oggetto
        if (mode == "centers")
        {
            double c0 = items[0].Start + items[0].Size / 2, cN = items[^1].Start + items[^1].Size / 2;
            for (var i = 0; i < items.Count; i++)
                target[i] = c0 + (cN - c0) * i / (items.Count - 1) - items[i].Size / 2;
        }
        else
        {
            var span = items.Max(e => e.Start + e.Size) - items.Min(e => e.Start);
            var gap = Gap ?? (span - items.Sum(e => e.Size)) / (items.Count - 1);
            var pos = Gap != null ? items[0].Start : items.Min(e => e.Start);
            for (var i = 0; i < items.Count; i++)
            {
                target[i] = pos;
                pos += items[i].Size + gap;
            }
        }

        _old = [];
        for (var i = 0; i < items.Count; i++)
        {
            var d = target[i] - items[i].Start;
            _old.Add((items[i].Obj, items[i].Obj.Transform));
            items[i].Obj.Transform = Shift.Apply(doc, items[i].Obj, horizontal ? d : 0, horizontal ? 0 : d);
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var (obj, old) in _old) obj.Transform = old;
    }
}
