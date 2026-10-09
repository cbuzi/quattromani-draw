using System.ComponentModel;
using System.Numerics;
using System.Text.Json;
using AiDraw.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiDraw.Mcp;

[McpServerToolType]
public sealed class AiDrawTools(CommandBus bus)
{
    [McpServerTool(Name = "get_document", ReadOnly = true)]
    [Description("""
        Returns the current document as compact JSON: {"version":N,"objects":[{id,name,type,bounds:[x,y,width,height],fill,stroke,strokeWidth}]}.
        type is "rectangle", "ellipse", "path" or "group". Paths include `d` (SVG path data in page coordinates, absolute M/L/C/Z);
        groups include `children` (same structure, listed bottom to top).
        `bounds` is the axis-aligned box on the page (also for children of groups). Rotated objects also report `rotation` (degrees, clockwise) and `size` ([width,height] of the object itself, before rotation).
        Objects are listed bottom to top (the last one is drawn on top). Bounds are in document pixels, y grows downward.
        Call this before editing to learn ids and names, and remember `version` to call get_changes later.
        """)]
    public string GetDocument() => bus.Read(d => JsonSerializer.Serialize(new
    {
        version = d.Version,
        objects = d.Objects.Select(o => Describe(o, Matrix3x2.Identity)).ToList(),
    }, Json.Options));

    sealed record ObjInfo(string Id, string? Name, string Type, double[] Bounds, double? Rotation, double[]? Size,
        string? Fill, string? Stroke, double? StrokeWidth, string? D, List<ObjInfo>? Children);

    // `parent` porta dallo spazio del contenitore (gruppo) allo spazio pagina.
    static ObjInfo Describe(SceneObject o, Matrix3x2 parent)
    {
        var world = o.Transform * parent;
        var b = SceneObject.BoundsOf(o.X, o.Y, o.Width, o.Height, world);
        var rotation = Math.Atan2(world.M12, world.M11) * 180 / Math.PI;
        var rotated = Math.Abs(rotation) > 0.005;
        static double R(double v) => Math.Round(v, 2);
        return new ObjInfo(o.Id, o.Name, o.Type, [R(b.X), R(b.Y), R(b.Width), R(b.Height)],
            rotated ? R(rotation) : null, rotated ? [R(o.Width), R(o.Height)] : null,
            o.Fill, o.Stroke, o.StrokeWidth == 0 ? null : o.StrokeWidth,
            o is PathObject p ? PathData.ToString(PathData.Transform(p.Segments, p.Fit * world)) : null,
            o is GroupObject g ? g.Children.Select(c => Describe(c, g.Fit * world)).ToList() : null);
    }

    [McpServerTool(Name = "execute")]
    [Description("""
        Applies a batch of commands to the document as ONE undoable step, all-or-nothing: if any command fails, nothing changes.
        `commands` is a JSON array. Returns {"version":N,"created":[ids]}.
        Commands:
          {"cmd":"shape.create","type":"rectangle"|"ellipse","name":"sun","x":10,"y":10,"width":100,"height":60,"fill":"#FFC800","stroke":"#000000","strokeWidth":2}
          {"cmd":"shape.create","type":"path","name":"roof","d":"M 0 60 L 50 0 L 100 60 Z","fill":"#B91C1C"}   (a path takes its position from `d`: omit x/y/width/height; with neither fill nor stroke it gets a black stroke)
          {"cmd":"object.move","targets":["o1"],"dx":50,"dy":-20}
          {"cmd":"object.style","targets":["sun"],"fill":"#FF0000","stroke":"none","strokeWidth":1}   (omitted fields stay unchanged)
          {"cmd":"object.resize","targets":["sun"],"width":120,"height":120,"anchor":"center"}   (anchor = the point that stays fixed; default "top-left"; omit width or height to keep it)
          {"cmd":"object.rotate","targets":["sun"],"degrees":45}   (added to the current rotation, clockwise, around the object's center)
          {"cmd":"object.group","targets":["roof","wall"],"name":"house"}   (same layer/group only; returns the group id in `created`)
          {"cmd":"object.ungroup","targets":["house"]}   (children keep their look and position)
          {"cmd":"object.delete","targets":["o1"]}
        Path data `d` is SVG syntax: M L H V C S Q T A Z, absolute (uppercase) or relative (lowercase), e.g. "M 10 10 h 80 v 50 h -80 z" or a circle "M 0 50 A 50 50 0 1 1 100 50 A 50 50 0 1 1 0 50 Z".
        Styling a group (object.style) applies to every shape inside it; moving, resizing and rotating a group transforms it as a whole.
        `targets` are object ids ("o1") or names ("sun"). Colors are "#RRGGBB", "#RRGGBBAA" or "none".
        `name` is optional but recommended on creation: it lets you address the object later without looking up its id.
        Example: [{"cmd":"shape.create","type":"rectangle","name":"sky","x":0,"y":0,"width":400,"height":200,"fill":"#87CEEB"},
                  {"cmd":"shape.create","type":"ellipse","name":"sun","x":300,"y":20,"width":60,"height":60,"fill":"#FFC800"}]
        """)]
    public string Execute(
        [Description("JSON array of command objects (see tool description).")] JsonElement commands,
        [Description("Your agent name, e.g. \"claude-code\". Recorded as author \"llm:<name>\".")] string agent = "agent")
    {
        if (commands.ValueKind != JsonValueKind.Array)
            throw new McpException("'commands' must be a JSON array of command objects, e.g. [{\"cmd\":\"object.move\",\"targets\":[\"o1\"],\"dx\":10,\"dy\":0}].");

        var parsed = new List<Command>();
        var i = 0;
        foreach (var el in commands.EnumerateArray())
        {
            try { parsed.Add(el.Deserialize<Command>(Json.Options) ?? throw new JsonException("null command")); }
            catch (JsonException ex)
            {
                throw new McpException($"commands[{i}] is invalid: {ex.Message} Valid commands: shape.create, object.move, object.style, object.resize, object.rotate, object.group, object.ungroup, object.delete; each needs a \"cmd\" field.");
            }
            i++;
        }

        try
        {
            var r = bus.Execute(parsed, "llm:" + agent);
            return JsonSerializer.Serialize(new { version = r.Version, created = r.CreatedIds }, Json.Options);
        }
        catch (CommandException ex)
        {
            throw new McpException($"{ex.Message} (the whole batch was rolled back; nothing changed)");
        }
    }

    [McpServerTool(Name = "get_changes", ReadOnly = true)]
    [Description("""
        Returns the changes applied after `sinceVersion`, including those made by the human with the mouse.
        Result: {"version":N,"changes":[{"version":V,"author":"human"|"llm:<name>","kind":"do"|"undo"|"redo","command":{...}}]}.
        Pass the `version` you last saw (from get_document or execute); use 0 for the full history.
        A "human" object.move/object.style/object.delete tells you what the user changed; "shape.create" includes the new object's id.
        """)]
    public string GetChanges([Description("Last version you know.")] long sinceVersion)
    {
        var (version, changes) = bus.GetChanges(sinceVersion);
        return JsonSerializer.Serialize(new { version, changes }, Json.Options);
    }
}
