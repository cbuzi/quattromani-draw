using System.Text.Json.Serialization;

namespace Quattromani.Core;

/// <summary>Duplicates objects: the copy sits right above the original, shifted by (dx, dy) in page space. New ids; names become "name 2", "name 3"...</summary>
public sealed class DuplicateObjects : Command
{
    public List<string> Targets { get; set; } = [];
    public double Dx { get; set; } = 20;
    public double Dy { get; set; } = 20;

    List<(List<SceneObject> Container, SceneObject Original, SceneObject Copy)> _placed = [];

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _placed.Select(p => p.Copy.Id);

    public override void Apply(Document doc)
    {
        var fresh = _placed.Count == 0;
        if (fresh)
        {
            if (Targets.Count == 0) throw new CommandException("object.duplicate: 'targets' is empty. Pass ids or names, e.g. [\"o1\"].");
            var objs = Targets.Select(t => doc.FindEditable(t)).Distinct().ToList();
            // If a group and one of its children are both given, duplicating the group is enough.
            objs = objs.Where(o => !objs.Any(p => p != o && Document.SelfAndDescendants(p).Contains(o))).ToList();
            _placed = objs.Select(o => (doc.ContainerOf(o)!, o, DocumentFile.ImportObjects(DocumentFile.ExportObjects([o]), doc)[0])).ToList();
        }

        foreach (var (container, original, copy) in _placed)
        {
            container.Insert(container.IndexOf(original) + 1, copy);
            if (fresh) copy.Transform = Shift.Apply(doc, copy, Dx, Dy);
        }
    }

    public override void Undo(Document doc)
    {
        foreach (var (container, _, copy) in _placed) container.Remove(copy);
    }
}

/// <summary>Pastes objects serialized by <see cref="DocumentFile.ExportObjects"/> on top of the first layer, shifted by (dx, dy).</summary>
public sealed class PasteObjects : Command
{
    public string Data { get; set; } = "";
    public double Dx { get; set; }
    public double Dy { get; set; }

    List<SceneObject> _pasted = [];
    Layer? _layer;

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _pasted.Select(o => o.Id);

    public override void Apply(Document doc)
    {
        var fresh = _pasted.Count == 0;
        if (fresh)
        {
            if (string.IsNullOrWhiteSpace(Data)) throw new CommandException("object.paste: 'data' is empty.");
            _pasted = DocumentFile.ImportObjects(Data, doc);
        }

        _layer ??= doc.TargetLayer(null);
        _layer.Objects.AddRange(_pasted);
        if (fresh)
            foreach (var o in _pasted) o.Transform = Shift.Apply(doc, o, Dx, Dy);
    }

    public override void Undo(Document doc)
    {
        foreach (var o in _pasted) _layer!.Objects.Remove(o);
    }
}
