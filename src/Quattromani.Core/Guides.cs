using System.Text.Json.Serialization;

namespace Quattromani.Core;

/// <summary>Linea guida: Axis "x" = linea verticale a x = Position, "y" = linea orizzontale a y = Position (px di pagina).</summary>
public sealed record Guide(string Axis, double Position)
{
    public bool Is(string axis, double position) => Axis == axis && Math.Abs(Position - position) < 1e-6;
}

/// <summary>Comandi sulle guide. Una guida è identificata da asse e posizione (due guide uguali non esistono).</summary>
public abstract class GuideCommand : Command
{
    List<Guide> _old = [];

    [JsonIgnore] public override IEnumerable<string> AffectedIds => [];

    protected static string CheckAxis(string cmd, string axis) =>
        axis is "x" or "y" ? axis : throw new CommandException($"{cmd}: 'axis' deve essere \"x\" (linea verticale) o \"y\" (linea orizzontale), non '{axis}'.");

    protected static void CheckPosition(string cmd, string name, double v)
    {
        if (!double.IsFinite(v) || Math.Abs(v) > 1e6) throw new CommandException($"{cmd}: '{name}' non valida ({v}).");
    }

    protected abstract void Change(string cmd, List<Guide> guides);

    public override void Apply(Document doc)
    {
        var guides = doc.Pages[0].Guides;
        _old = [.. guides];
        Change(CmdName, guides);
    }

    public override void Undo(Document doc)
    {
        var guides = doc.Pages[0].Guides;
        guides.Clear();
        guides.AddRange(_old);
    }

    [JsonIgnore] protected abstract string CmdName { get; }
}

/// <summary>Aggiunge una guida: {"cmd":"guide.add","axis":"x","position":100}.</summary>
public sealed class AddGuide : GuideCommand
{
    public string Axis { get; set; } = "";
    public double Position { get; set; }
    [JsonIgnore] protected override string CmdName => "guide.add";

    protected override void Change(string cmd, List<Guide> guides)
    {
        var axis = CheckAxis(cmd, Axis);
        CheckPosition(cmd, "position", Position);
        if (guides.Any(g => g.Is(axis, Position))) throw new CommandException($"{cmd}: esiste gia' una guida {axis} = {Position}.");
        guides.Add(new Guide(axis, Position));
    }
}

/// <summary>Sposta una guida da <c>from</c> a <c>to</c>: {"cmd":"guide.move","axis":"x","from":100,"to":150}.</summary>
public sealed class MoveGuide : GuideCommand
{
    public string Axis { get; set; } = "";
    public double From { get; set; }
    public double To { get; set; }
    [JsonIgnore] protected override string CmdName => "guide.move";

    protected override void Change(string cmd, List<Guide> guides)
    {
        var axis = CheckAxis(cmd, Axis);
        CheckPosition(cmd, "to", To);
        var i = guides.FindIndex(g => g.Is(axis, From));
        if (i < 0) throw new CommandException($"{cmd}: non c'e' nessuna guida {axis} = {From}. Guide presenti: {Describe(guides)}.");
        if (guides.Any(g => g.Is(axis, To)) && Math.Abs(From - To) >= 1e-6) throw new CommandException($"{cmd}: esiste gia' una guida {axis} = {To}.");
        guides[i] = new Guide(axis, To);
    }

    internal static string Describe(List<Guide> guides) => guides.Count == 0 ? "nessuna" : string.Join(", ", guides.Select(g => $"{g.Axis}={g.Position:0.##}"));
}

/// <summary>Elimina una guida ({"cmd":"guide.delete","axis":"x","position":100}), oppure tutte se manca 'axis'.</summary>
public sealed class DeleteGuide : GuideCommand
{
    public string? Axis { get; set; }
    public double Position { get; set; }
    [JsonIgnore] protected override string CmdName => "guide.delete";

    protected override void Change(string cmd, List<Guide> guides)
    {
        if (Axis == null) { guides.Clear(); return; }
        var axis = CheckAxis(cmd, Axis);
        if (guides.RemoveAll(g => g.Is(axis, Position)) == 0)
            throw new CommandException($"{cmd}: non c'e' nessuna guida {axis} = {Position}. Guide presenti: {MoveGuide.Describe(guides)}.");
    }
}
