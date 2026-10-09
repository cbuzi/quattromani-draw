using System.ComponentModel;
using System.Numerics;
using System.Text.Json;
using AiDraw.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiDraw.Mcp;

[McpServerToolType]
public sealed class AiDrawTools(CommandBus bus, IDocumentRenderer renderer)
{
    [McpServerTool(Name = "get_document", ReadOnly = true)]
    [Description("""
        Returns the current document as compact JSON: {"version":N,"objects":[{id,name,type,bounds:[x,y,width,height],fill,stroke,strokeWidth}]}.
        type is "rectangle", "ellipse", "path", "text" or "group". Texts include `text`, `fontFamily`, `fontSize` (and bold/italic/align when set); their fill is the text color. Paths include `d` (SVG path data in page coordinates, absolute M/L/C/Z);
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
        string? Fill, string? Stroke, double? StrokeWidth, string? D, List<ObjInfo>? Children,
        string? Text = null, string? FontFamily = null, double? FontSize = null, bool? Bold = null, bool? Italic = null, string? Align = null);

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
            o is GroupObject g ? g.Children.Select(c => Describe(c, g.Fit * world)).ToList() : null,
            (o as TextObject)?.Text, (o as TextObject)?.FontFamily, (o as TextObject)?.FontSize,
            o is TextObject { Bold: true } ? true : null, o is TextObject { Italic: true } ? true : null, (o as TextObject)?.Align);
    }

    [McpServerTool(Name = "execute")]
    [Description("""
        Applies a batch of commands to the document as ONE undoable step, all-or-nothing: if any command fails, nothing changes.
        `commands` is a JSON array. Returns {"version":N,"created":[ids]}.
        Commands:
          {"cmd":"shape.create","type":"rectangle"|"ellipse","name":"sun","x":10,"y":10,"width":100,"height":60,"fill":"#FFC800","stroke":"#000000","strokeWidth":2}
          {"cmd":"shape.create","type":"path","name":"roof","d":"M 0 60 L 50 0 L 100 60 Z","fill":"#B91C1C"}   (a path takes its position from `d`: omit x/y/width/height; with neither fill nor stroke it gets a black stroke)
          {"cmd":"object.move","targets":["o1"],"dx":50,"dy":-20}
          {"cmd":"object.place","targets":["sun"],"rel":{"inside":"sky","anchor":"top-right","margin":20}}   (moves existing objects relative to another one; several targets move together as one block)
          {"cmd":"object.style","targets":["sun"],"fill":"#FF0000","stroke":"none","strokeWidth":1}   (omitted fields stay unchanged)
          {"cmd":"object.resize","targets":["sun"],"width":120,"height":120,"anchor":"center"}   (anchor = the point that stays fixed; default "top-left"; omit width or height to keep it)
          {"cmd":"object.rotate","targets":["sun"],"degrees":45}   (added to the current rotation, clockwise, around the object's center)
          {"cmd":"object.group","targets":["roof","wall"],"name":"house"}   (same layer/group only; returns the group id in `created`)
          {"cmd":"object.ungroup","targets":["house"]}   (children keep their look and position)
          {"cmd":"shape.boolean","op":"weld","targets":["a","b"],"name":"merged"}   (op: weld = union; intersect; subtract = the lowest object minus all the others; trim = like subtract but the cutting objects stay)
          {"cmd":"path.set","target":"roof","d":"M 0 60 L 50 -10 L 100 60 Z"}   (replaces the geometry of a top-level path with new SVG data in page coordinates, i.e. what get_document reports in `d`; use it to move/add/remove nodes or reshape curves; accumulated move/rotation is baked into the data)
          {"cmd":"shape.toPath","targets":["sun"]}   (turns rectangles/ellipses into paths, keeping id, name, style and rotation, so they can be reshaped with path.set)
          {"cmd":"shape.create","type":"text","name":"title","text":"Hello\nworld","fontFamily":"Helvetica Neue","fontSize":32,"bold":true,"align":"center","fill":"#111827","x":20,"y":20}   (text: '\n' = new line; with `width` the text wraps at that width, otherwise it is one line per '\n'; height is computed; accepts rel too)
          {"cmd":"text.set","targets":["title"],"text":"Goodbye","fontSize":40,"italic":true}   (change content and/or style; omitted fields stay; width 0 removes wrapping)
          {"cmd":"object.delete","targets":["o1"]}
        Fonts: if you omit fontFamily a default is used; call list_fonts for the families installed on this computer (an unknown font gives an error listing some). Text can be moved, resized (stretches it), rotated, grouped and positioned with rel like any object; shape.toPath turns text into editable curves (needed for shape.boolean).
        shape.boolean works on rectangles, ellipses and paths on the same level (ungroup groups first), needs 2+ targets and returns the new path id in `created`.
        The result is a path with the style of the lowest object (in z-order); curves are flattened into short segments. Add "keepOriginals":true to keep the source objects.
        RELATIVE POSITIONING: instead of x/y, shape.create accepts "rel" (and object.place takes it too), so you do not have to compute coordinates:
          "rel":{"inside":"sky","anchor":"top-right","margin":20}   inside `sky`, the object's top-right corner 20 units from the sky's top-right corner
          "rel":{"rightOf":"house","margin":10}   to the right of `house`, 10 units apart, vertically centered (also leftOf, above, below)
          "rel":{"below":"title","anchor":"top-left","refAnchor":"bottom-left"}   left edges aligned, directly under `title`
          "rel":{"alignTo":"box","anchor":"center"}   same anchor point on both (default center): centers, or "left" aligns left edges and centers vertically
        Give exactly ONE of inside/rightOf/leftOf/above/below/alignTo (an id or name that already exists, even created earlier in the same batch).
        `anchor` is the point of the NEW object, `refAnchor` the point of the reference it attaches to. Anchors: top-left, top, top-right, left, center, right, bottom-left, bottom, bottom-right.
        `margin` is the inset for inside/alignTo and the gap for rightOf/leftOf/above/below. With rel, omit x and y (width/height are still needed for rectangles and ellipses).
        Path data `d` is SVG syntax: M L H V C S Q T A Z, absolute (uppercase) or relative (lowercase), e.g. "M 10 10 h 80 v 50 h -80 z" or a circle "M 0 50 A 50 50 0 1 1 100 50 A 50 50 0 1 1 0 50 Z".
        Styling a group (object.style) applies to every shape inside it; moving, resizing and rotating a group transforms it as a whole.
        `targets` are object ids ("o1") or names ("sun"). Colors are "#RRGGBB", "#RRGGBBAA" or "none".
        GRADIENTS: `fill` and `stroke` (in shape.create and object.style) also accept CSS-style gradients over the object's own box, e.g.
          "linear-gradient(90deg, #FF0000, #0000FF)"   angle in degrees: 0 = bottom-to-top, 90 = left-to-right, 180 = top-to-bottom (the default); also "to right", "to bottom right"...
          "linear-gradient(180deg, #87CEEB 0%, #FFFFFF 70%, #4ADE80 100%)"   any number of colors, optional % positions (missing ones are spread evenly)
          "radial-gradient(#FFFFFF, #000000)"   from the center out to the farthest corner; optional shape/center: "radial-gradient(circle at 30% 40%, #FFF3B0, #F59E0B)"
          "linear-gradient(90deg, #FF000080, #FF000000)"   colors may carry alpha (#RRGGBBAA) for fades
          "linear-gradient(from 10% 20% to 90% 80%, #FDE68A, #F59E0B)"   explicit start and end points (percentages of the object's box, may go outside 0-100%) instead of an angle
          "radial-gradient(ellipse 35% 55% at 30% 40%, #FFFFFF, #F472B6 60%, #831843)"   explicit radii (% of width, height) and center
        PATTERNS: `fill` and `stroke` also accept two-color repeating patterns, anchored to the object's top-left corner:
          "pattern(stripes, #DC2626, #FEF2F2, 10)"   kind (stripes, dots, checker, grid), pattern color, background color (#RRGGBB00 = transparent), size in document units, optional rotation: "pattern(dots, #1E3A8A, #DBEAFE, 12, 30)"
        The gradient or pattern is stored (and returned by get_document) in canonical form with explicit values.
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
                throw new McpException($"commands[{i}] is invalid: {ex.Message} Valid commands: shape.create, object.move, object.style, object.resize, object.rotate, object.group, object.ungroup, shape.boolean, path.set, shape.toPath, object.place, text.set, object.delete; each needs a \"cmd\" field.");
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

    [McpServerTool(Name = "list_fonts", ReadOnly = true)]
    [Description("""
        Lists the font families installed on the computer running AiDraw, to use as `fontFamily` in text commands.
        Returns {"default":"<family used when fontFamily is omitted>","families":[...]}. Optional `filter` keeps only the names containing that text (case-insensitive).
        """)]
    public string ListFonts([Description("Only families whose name contains this text.")] string? filter = null)
    {
        var engine = bus.Document.TextEngine ?? throw new McpException("The font engine is not available.");
        var families = engine.Families.Where(f => string.IsNullOrEmpty(filter) || f.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        return JsonSerializer.Serialize(new { @default = engine.DefaultFamily, families }, Json.Options);
    }

    [McpServerTool(Name = "render", ReadOnly = true)]
    [Description("""
        Renders the document (or a part of it) as a PNG image so you can check the result visually. Call it after drawing to verify what you made.
        Optional arguments: `area` = [x, y, width, height] in document units to zoom into a region (default: all the content with a small margin);
        `maxSize` = longest side in pixels (default 800, max 2048); `background` = "#RRGGBB", "#RRGGBBAA" or "transparent" (default white);
        `labels` = true writes each top-level object's id and name on the image, handy to match what you see with ids for later commands.
        Returns a short text with the area shown and the scale, then the image. Selection and editing handles are not drawn.
        """)]
    public IEnumerable<ContentBlock> Render(
        [Description("[x, y, width, height] in document units. Omit to render all the content.")] double[]? area = null,
        [Description("Longest side in pixels, 64..2048.")] int maxSize = 800,
        [Description("\"#RRGGBB\", \"#RRGGBBAA\" or \"transparent\".")] string? background = null,
        [Description("Write object ids and names on the image.")] bool labels = false)
    {
        Box? box = null;
        if (area != null)
        {
            if (area.Length != 4 || area[2] <= 0 || area[3] <= 0 || area.Any(v => !double.IsFinite(v)))
                throw new McpException("'area' must be [x, y, width, height] with width and height > 0, e.g. [0, 0, 400, 300].");
            box = new(area[0], area[1], area[2], area[3]);
        }
        if (maxSize is < 64 or > 2048) throw new McpException("'maxSize' must be between 64 and 2048.");
        if (background is not (null or "transparent") && !System.Text.RegularExpressions.Regex.IsMatch(background, "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$"))
            throw new McpException("'background' must be \"#RRGGBB\", \"#RRGGBBAA\" or \"transparent\".");

        var img = renderer.Render(box, maxSize, background, labels);
        var a = img.Area;
        return
        [
            new TextContentBlock { Text = FormattableString.Invariant($"{img.PixelWidth}x{img.PixelHeight}px showing area [{a.X:0.##}, {a.Y:0.##}, {a.Width:0.##}, {a.Height:0.##}] (1 pixel = {a.Width / img.PixelWidth:0.###} document units)") },
            ImageContentBlock.FromBytes(img.Png, "image/png"),
        ];
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
