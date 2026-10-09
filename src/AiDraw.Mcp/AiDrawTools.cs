using System.ComponentModel;
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
        `bounds` is the axis-aligned box on the page. Rotated objects also report `rotation` (degrees, clockwise) and `size` ([width,height] of the object itself, before rotation).
        Objects are listed bottom to top (the last one is drawn on top). Bounds are in document pixels, y grows downward.
        Call this before editing to learn ids and names, and remember `version` to call get_changes later.
        """)]
    public string GetDocument() => bus.Read(d => JsonSerializer.Serialize(new
    {
        version = d.Version,
        objects = d.Objects.Select(o =>
        {
            var b = o.Bounds;
            var rotated = Math.Abs(o.Rotation) > 0.005;
            return new
            {
                id = o.Id, name = o.Name, type = o.Type,
                bounds = new[] { Math.Round(b.X, 2), Math.Round(b.Y, 2), Math.Round(b.Width, 2), Math.Round(b.Height, 2) },
                rotation = rotated ? Math.Round(o.Rotation, 2) : (double?)null,
                size = rotated ? new[] { Math.Round(o.Width, 2), Math.Round(o.Height, 2) } : null,
                fill = o.Fill, stroke = o.Stroke, strokeWidth = o.StrokeWidth == 0 ? (double?)null : o.StrokeWidth,
            };
        }).ToList(),
    }, Json.Options));

    [McpServerTool(Name = "execute")]
    [Description("""
        Applies a batch of commands to the document as ONE undoable step, all-or-nothing: if any command fails, nothing changes.
        `commands` is a JSON array. Returns {"version":N,"created":[ids]}.
        Commands:
          {"cmd":"shape.create","type":"rectangle"|"ellipse","name":"sun","x":10,"y":10,"width":100,"height":60,"fill":"#FFC800","stroke":"#000000","strokeWidth":2}
          {"cmd":"object.move","targets":["o1"],"dx":50,"dy":-20}
          {"cmd":"object.style","targets":["sun"],"fill":"#FF0000","stroke":"none","strokeWidth":1}   (omitted fields stay unchanged)
          {"cmd":"object.resize","targets":["sun"],"width":120,"height":120,"anchor":"center"}   (anchor = the point that stays fixed; default "top-left"; omit width or height to keep it)
          {"cmd":"object.rotate","targets":["sun"],"degrees":45}   (added to the current rotation, clockwise, around the object's center)
          {"cmd":"object.delete","targets":["o1"]}
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
                throw new McpException($"commands[{i}] is invalid: {ex.Message} Valid commands: shape.create, object.move, object.style, object.resize, object.rotate, object.delete; each needs a \"cmd\" field.");
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
