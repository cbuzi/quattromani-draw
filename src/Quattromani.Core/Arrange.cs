using System.Numerics;
using System.Text.Json.Serialization;

namespace Quattromani.Core;

/// <summary>Moves objects by an amount expressed in page space, brought back to the space of their container (even inside rotated or scaled groups).</summary>
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
/// Stacking order (z-order) among siblings at the same level. <c>action</c>: "front" (on top), "back" (at the bottom),
/// "forward" / "backward" (one step), or <c>above</c> / <c>below</c> = right above or below another object.
/// Several objects keep their relative order.
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
        if (Targets.Count == 0) throw new CommandException("object.order: 'targets' is empty. Pass ids or names, e.g. [\"o1\"].");
        var given = new[] { Action, Above, Below }.Count(x => x != null);
        if (given != 1)
            throw new CommandException("object.order: give exactly one of 'action' (front, back, forward, backward), 'above' and 'below'. Example: {\"cmd\":\"object.order\",\"targets\":[\"o3\"],\"action\":\"front\"}.");
        if (Action is not (null or "front" or "back" or "forward" or "backward"))
            throw new CommandException($"object.order: action '{Action}' is not valid. Values: \"front\", \"back\", \"forward\", \"backward\".");

        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        _ids = objs.Select(o => o.Id).ToList();
        SceneObject? reference = Above != null ? doc.Find(Above) : Below != null ? doc.Find(Below) : null;
        if (reference != null && objs.Contains(reference))
            throw new CommandException($"object.order: '{reference.Name ?? reference.Id}' is among the objects to move: it cannot be its own reference.");

        var byContainer = objs.GroupBy(o => doc.ContainerOf(o)!).ToList();
        if (reference != null && byContainer.Any(g => !ReferenceEquals(g.Key, doc.ContainerOf(reference))))
            throw new CommandException("object.order: with 'above' and 'below' the objects must be at the same level as the reference (same layer or same group).");

        _old = byContainer.Select(g => (g.Key, g.Key.ToList())).ToList();
        foreach (var g in byContainer)
        {
            var list = g.Key;
            var set = g.ToHashSet();
            var moving = list.Where(set.Contains).ToList(); // in current stacking order
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
/// Aligns each object, individually, to a reference: x = "left" | "center" | "right", y = "top" | "middle" | "bottom" (or both).
/// <c>to</c>: "selection" (the selection box; the default with several objects), "page" (default with a single object) or an object.
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
        if (Targets.Count == 0) throw new CommandException("object.align: 'targets' is empty. Pass ids or names, e.g. [\"o1\",\"o2\"].");
        if (X is null && Y is null)
            throw new CommandException("object.align: give 'x' (left, center, right) and/or 'y' (top, middle, bottom). Example: {\"cmd\":\"object.align\",\"targets\":[\"a\",\"b\"],\"x\":\"center\"}.");
        if (X is not (null or "left" or "center" or "right")) throw new CommandException($"object.align: x '{X}' is not valid. Values: \"left\", \"center\", \"right\".");
        if (Y is not (null or "top" or "middle" or "bottom")) throw new CommandException($"object.align: y '{Y}' is not valid. Values: \"top\", \"middle\", \"bottom\".");

        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
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
            if (objs.Contains(r)) throw new CommandException($"object.align: '{to}' is among the objects to align: it cannot be its own reference.");
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
/// Distributes objects along an axis. mode "gaps" (default): equal gaps between each object and the next, with the first and last fixed
/// (or with an explicit <c>gap</c>, starting from the first); mode "centers": equally spaced centers, first and last fixed.
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
        if (Targets.Count == 0) throw new CommandException("object.distribute: 'targets' is empty. Pass the ids or names of at least 3 objects.");
        if (Axis is not ("x" or "y"))
            throw new CommandException("object.distribute: 'axis' must be \"x\" (horizontal) or \"y\" (vertical). Example: {\"cmd\":\"object.distribute\",\"targets\":[\"a\",\"b\",\"c\"],\"axis\":\"x\"}.");
        var mode = Mode ?? "gaps";
        if (mode is not ("gaps" or "centers")) throw new CommandException($"object.distribute: mode '{Mode}' is not valid. Values: \"gaps\", \"centers\".");
        if (Gap != null && mode != "gaps") throw new CommandException("object.distribute: 'gap' is only used with mode \"gaps\".");

        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        if (objs.Count < (Gap != null ? 2 : 3))
            throw new CommandException(Gap != null
                ? "object.distribute: at least 2 objects are needed."
                : "object.distribute: at least 3 objects are needed (2 are enough with an explicit 'gap').");

        bool horizontal = Axis == "x";
        // (start, end) of each object on the axis, in position order
        var items = objs.Select(o => (Obj: o, B: doc.PageBounds(o)))
            .Select(e => (e.Obj, Start: horizontal ? e.B.X : e.B.Y, Size: horizontal ? e.B.Width : e.B.Height))
            .OrderBy(e => e.Start + e.Size / 2).ToList();

        var target = new double[items.Count]; // new start of each object
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
