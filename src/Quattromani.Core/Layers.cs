using System.Numerics;
using System.Text.Json.Serialization;

namespace Quattromani.Core;

static class LayerNames
{
    public static string Next(Page page)
    {
        var n = page.Layers.Count + 1;
        while (page.Layers.Any(l => l.Name.Equals($"Layer {n}", StringComparison.OrdinalIgnoreCase))) n++;
        return $"Layer {n}";
    }

    /// <summary>Valid and unique name (case-insensitive, like the lookup); <paramref name="except"/> is the layer about to take it.</summary>
    public static string Check(Page page, string? name, Layer? except = null)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new CommandException("The layer name cannot be empty.");
        if (name.Length > 60) throw new CommandException("The layer name can have at most 60 characters.");
        if (page.Layers.Any(l => l != except && l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new CommandException($"A layer named '{name}' already exists: choose another name (layers: {string.Join(", ", page.Layers.Select(l => $"\"{l.Name}\""))}).");
        return name;
    }
}

/// <summary>Creates a layer (on top by default) and makes it active: new objects go there.</summary>
public sealed class AddLayer : Command
{
    public string? Name { get; set; }
    /// <summary>Name of an existing layer: the new one sits right above it. Without it, on top of all.</summary>
    public string? Above { get; set; }
    /// <summary>Makes the new layer active (default yes).</summary>
    public bool Activate { get; set; } = true;

    Layer? _layer;
    Layer? _prevActive;

    public override void Apply(Document doc)
    {
        var page = doc.Pages[0];
        _layer ??= new Layer(Name is null ? LayerNames.Next(page) : LayerNames.Check(page, Name));
        var index = Above is null ? page.Layers.Count : page.Layers.IndexOf(doc.FindLayer(Above)) + 1;
        page.Layers.Insert(index, _layer);
        _prevActive = page.ActiveLayer;
        if (Activate) page.ActiveLayer = _layer;
    }

    public override void Undo(Document doc)
    {
        var page = doc.Pages[0];
        page.Layers.Remove(_layer!);
        page.ActiveLayer = _prevActive!;
    }
}

/// <summary>Renames a layer, shows or hides it, locks or unlocks it, makes it active. Omitted fields stay unchanged.</summary>
public sealed class SetLayer : Command
{
    public string Layer { get; set; } = "";
    public string? Name { get; set; }
    public bool? Visible { get; set; }
    public bool? Locked { get; set; }
    /// <summary>true = new objects will go to this layer.</summary>
    public bool? Active { get; set; }

    (Layer Layer, string Name, bool Visible, bool Locked, Layer Active)? _old;

    public override void Apply(Document doc)
    {
        var layer = doc.FindLayer(Layer);
        if (Name == null && Visible == null && Locked == null && Active == null)
            throw new CommandException("layer.set: give at least one field to change among name, visible, locked, active. Example: {\"cmd\":\"layer.set\",\"layer\":\"sketch\",\"locked\":true}.");
        var page = doc.Pages[0];
        var name = Name is null ? layer.Name : LayerNames.Check(page, Name, layer);
        _old = (layer, layer.Name, layer.Visible, layer.Locked, page.ActiveLayer);
        layer.Name = name;
        if (Visible != null) layer.Visible = Visible.Value;
        if (Locked != null) layer.Locked = Locked.Value;
        if (Active == true) page.ActiveLayer = layer;
    }

    public override void Undo(Document doc)
    {
        var (layer, name, visible, locked, active) = _old!.Value;
        (layer.Name, layer.Visible, layer.Locked) = (name, visible, locked);
        doc.Pages[0].ActiveLayer = active;
    }
}

/// <summary>Deletes a layer with all its content (undo brings it back). The last layer cannot be deleted.</summary>
public sealed class DeleteLayer : Command
{
    public string Layer { get; set; } = "";

    Layer? _layer;
    int _index;

    public override void Apply(Document doc)
    {
        var page = doc.Pages[0];
        var layer = doc.FindLayer(Layer);
        if (page.Layers.Count == 1) throw new CommandException("layer.delete: it is the only layer and cannot be deleted. To empty it use object.delete.");
        if (layer.Locked) throw new CommandException($"layer.delete: layer '{layer.Name}' is locked: unlock it with layer.set before deleting it.");
        _layer = layer;
        _index = page.Layers.IndexOf(layer);
        page.Layers.RemoveAt(_index);
    }

    public override void Undo(Document doc) => doc.Pages[0].Layers.Insert(_index, _layer!);
}

/// <summary>Changes the position of a layer: higher layers cover the ones below.</summary>
public sealed class OrderLayer : Command
{
    public string Layer { get; set; } = "";
    /// <summary>"front" (on top), "back" (at the bottom), "up" (one higher), "down" (one lower).</summary>
    public string Action { get; set; } = "";

    Layer? _layer;
    int _old;

    public override void Apply(Document doc)
    {
        var layers = doc.Pages[0].Layers;
        var layer = doc.FindLayer(Layer);
        var from = layers.IndexOf(layer);
        var to = Action switch
        {
            "front" => layers.Count - 1,
            "back" => 0,
            "up" => Math.Min(from + 1, layers.Count - 1),
            "down" => Math.Max(from - 1, 0),
            _ => throw new CommandException($"layer.order: action '{Action}' is not valid. Values: \"front\", \"back\", \"up\", \"down\"."),
        };
        (_layer, _old) = (layer, from);
        layers.RemoveAt(from);
        layers.Insert(to, layer);
    }

    public override void Undo(Document doc)
    {
        var layers = doc.Pages[0].Layers;
        layers.Remove(_layer!);
        layers.Insert(_old, _layer!);
    }
}

/// <summary>Moves objects (also out of a group) to the top of another layer, without changing where they appear.</summary>
public sealed class MoveToLayer : Command
{
    public List<string> Targets { get; set; } = [];
    public string Layer { get; set; } = "";

    List<(SceneObject Obj, List<SceneObject> Container, int Index, Matrix3x2 Transform)> _moved = [];
    Layer? _dest;

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _moved.Select(m => m.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.toLayer: 'targets' is empty. Pass ids or names, e.g. [\"o1\"].");
        if (string.IsNullOrWhiteSpace(Layer)) throw new CommandException("object.toLayer: missing 'layer'. Example: {\"cmd\":\"object.toLayer\",\"targets\":[\"o1\"],\"layer\":\"sketch\"}.");
        var dest = doc.FindLayer(Layer);
        var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
        // If a group and one of its children are both given, moving the group is enough.
        objs = objs.Where(o => !objs.Any(p => p != o && Document.SelfAndDescendants(p).Contains(o))).ToList();
        var order = doc.AllObjects.ToList();
        var moved = objs.OrderBy(order.IndexOf)
            .Select(o => { var c = doc.ContainerOf(o)!; return (o, c, c.IndexOf(o), o.Transform); }).ToList();
        var parents = moved.Select(m => doc.ParentMatrix(m.o)).ToList(); // before taking them out of their container

        _moved = moved;
        _dest = dest;
        for (var i = 0; i < moved.Count; i++)
        {
            var (o, container, _, _) = moved[i];
            o.Transform *= parents[i]; // outside the group the object carries the group's transform with it
            container.Remove(o);
            dest.Objects.Add(o);
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var (o, _, _, _) in _moved) _dest!.Objects.Remove(o);
        foreach (var (o, container, index, transform) in _moved)
        {
            o.Transform = transform;
            container.Insert(index, o);
        }
    }
}

/// <summary>Hides or shows, locks or unlocks objects. Omitted fields stay unchanged.</summary>
public sealed class SetObjectState : Command
{
    public List<string> Targets { get; set; } = [];
    public bool? Hidden { get; set; }
    public bool? Locked { get; set; }

    List<(SceneObject Obj, bool Hidden, bool Locked)> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => _old.Select(e => e.Obj.Id);

    public override void Apply(Document doc)
    {
        if (Targets.Count == 0) throw new CommandException("object.state: 'targets' is empty. Pass ids or names, e.g. [\"o1\"].");
        if (Hidden == null && Locked == null)
            throw new CommandException("object.state: give hidden and/or locked. Example: {\"cmd\":\"object.state\",\"targets\":[\"o1\"],\"locked\":true}.");
        _old = Targets.Select(t => doc.Find(t)).Distinct().Select(o => (o, o.Hidden, o.Locked)).ToList();
        foreach (var (o, _, _) in _old)
        {
            if (Hidden != null) o.Hidden = Hidden.Value;
            if (Locked != null) o.Locked = Locked.Value;
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var (o, hidden, locked) in _old) (o.Hidden, o.Locked) = (hidden, locked);
    }
}
