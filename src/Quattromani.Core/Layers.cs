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

    /// <summary>Nome valido e unico (maiuscole ininfluenti, come nella ricerca); <paramref name="except"/> è il layer che sta per prenderlo.</summary>
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

/// <summary>Crea un layer (di default in cima) e lo rende attivo: gli oggetti nuovi finiscono lì.</summary>
public sealed class AddLayer : Command
{
    public string? Name { get; set; }
    /// <summary>Nome di un layer esistente: il nuovo gli sta subito sopra. Senza, in cima a tutti.</summary>
    public string? Above { get; set; }
    /// <summary>Rende attivo il nuovo layer (default sì).</summary>
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

/// <summary>Rinomina un layer, lo mostra o nasconde, lo blocca o sblocca, lo rende attivo. I campi omessi restano invariati.</summary>
public sealed class SetLayer : Command
{
    public string Layer { get; set; } = "";
    public string? Name { get; set; }
    public bool? Visible { get; set; }
    public bool? Locked { get; set; }
    /// <summary>true = gli oggetti nuovi finiranno in questo layer.</summary>
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

/// <summary>Elimina un layer con tutto il suo contenuto (l'undo lo riporta). L'ultimo layer non si può eliminare.</summary>
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

/// <summary>Cambia la posizione di un layer: i layer più in alto coprono quelli sotto.</summary>
public sealed class OrderLayer : Command
{
    public string Layer { get; set; } = "";
    /// <summary>"front" (in cima), "back" (in fondo), "up" (uno più in alto), "down" (uno più in basso).</summary>
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

/// <summary>Sposta oggetti (anche da dentro un gruppo) in cima a un altro layer, senza cambiare dove appaiono.</summary>
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
        // Se un gruppo e un suo figlio sono entrambi indicati, basta spostare il gruppo.
        objs = objs.Where(o => !objs.Any(p => p != o && Document.SelfAndDescendants(p).Contains(o))).ToList();
        var order = doc.AllObjects.ToList();
        var moved = objs.OrderBy(order.IndexOf)
            .Select(o => { var c = doc.ContainerOf(o)!; return (o, c, c.IndexOf(o), o.Transform); }).ToList();
        var parents = moved.Select(m => doc.ParentMatrix(m.o)).ToList(); // prima di toglierli dal loro contenitore

        _moved = moved;
        _dest = dest;
        for (var i = 0; i < moved.Count; i++)
        {
            var (o, container, _, _) = moved[i];
            o.Transform *= parents[i]; // fuori dal gruppo l'oggetto porta con sé la trasformazione del gruppo
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

/// <summary>Nasconde o mostra, blocca o sblocca oggetti. I campi omessi restano invariati.</summary>
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
