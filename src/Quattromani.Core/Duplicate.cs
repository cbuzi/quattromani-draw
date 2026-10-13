using System.Text.Json.Serialization;

namespace Quattromani.Core;

/// <summary>Duplica degli oggetti: la copia sta subito sopra l'originale, spostata di (dx, dy) nello spazio pagina. Id nuovi; i nomi diventano "nome 2", "nome 3"...</summary>
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
            if (Targets.Count == 0) throw new CommandException("object.duplicate: 'targets' e' vuoto. Passa id o nomi, es. [\"o1\"].");
            var objs = Targets.Select(t => doc.Find(t)).Distinct().ToList();
            // Se un gruppo e un suo figlio sono entrambi indicati, basta duplicare il gruppo.
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

/// <summary>Incolla oggetti serializzati da <see cref="DocumentFile.ExportObjects"/> in cima al primo layer, spostati di (dx, dy).</summary>
public sealed class PasteObjects : Command
{
    public string Data { get; set; } = "";
    public double Dx { get; set; }
    public double Dy { get; set; }

    List<SceneObject> _pasted = [];

    [JsonIgnore] public override IEnumerable<string> CreatedIds => _pasted.Select(o => o.Id);

    public override void Apply(Document doc)
    {
        var fresh = _pasted.Count == 0;
        if (fresh)
        {
            if (string.IsNullOrWhiteSpace(Data)) throw new CommandException("object.paste: 'data' e' vuoto.");
            _pasted = DocumentFile.ImportObjects(Data, doc);
        }

        var layer = doc.Pages[0].Layers[0].Objects;
        layer.AddRange(_pasted);
        if (fresh)
            foreach (var o in _pasted) o.Transform = Shift.Apply(doc, o, Dx, Dy);
    }

    public override void Undo(Document doc)
    {
        foreach (var o in _pasted) doc.Pages[0].Layers[0].Objects.Remove(o);
    }
}
