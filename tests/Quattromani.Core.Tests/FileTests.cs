using Quattromani.Core;

namespace Quattromani.Core.Tests;

public class FileTests
{
    static Document RoundTrip(CommandBus bus) => DocumentFile.Load(DocumentFile.Save(bus.Document));

    [Fact]
    public void Save_and_load_preserve_objects_styles_layers_and_states()
    {
        var bus = T.NewBus();
        bus.Execute([
            new CreateShape { Type = "rectangle", Name = "card", X = 10, Y = 20, Width = 100, Height = 50, Fill = "linear-gradient(90deg, #FF0000, #0000FF)",
                Stroke = "#000000", StrokeWidth = 3, Dash = "dashed", Cap = "round", Join = "bevel", Opacity = 0.5, CornerRadius = 12 },
            new CreateShape { Type = "path", Name = "wave", D = "M 0 0 C 10 -10 20 10 30 0", Stroke = "#00FF00" },
            new CreateShape { Type = "star", Name = "star", Points = 7, InnerRatio = 0.3, Width = 50, Height = 50, Fill = "#FACC15" },
            new AddLayer { Name = "sketch" },
        ]);
        bus.Rect("ref", layer: "sketch");
        bus.Execute([new SetObjectState { Targets = ["ref"], Hidden = true, Locked = true }, new SetLayer { Layer = "sketch", Locked = true }]);
        bus.Execute([new GroupObjects { Targets = ["card", "wave"], Name = "g" }, new RotateObjects { Targets = ["g"], Degrees = 15 }]);

        var loaded = RoundTrip(bus);

        Assert.Equal(["Layer 1", "sketch"], loaded.Pages[0].Layers.Select(l => l.Name));
        Assert.True(loaded.Pages[0].Layers[1].Locked);
        Assert.Equal("sketch", loaded.Pages[0].ActiveLayer.Name);
        var card = (RectangleObject)loaded.Find("card");
        Assert.Equal(12, card.CornerRadius);
        Assert.Equal([12.0, 6.0], card.StrokeDash!);
        Assert.Equal(("round", "bevel", 0.5), (card.StrokeCap, card.StrokeJoin, card.Opacity));
        Assert.Equal(bus.Document.Find("card").Fill, card.Fill);
        var r = loaded.Find("ref");
        Assert.True(r.Hidden);
        Assert.True(r.Locked);
        Assert.Equal(15, loaded.Find("g").Rotation, 3);
        Assert.Equal(bus.Document.Find("star").Bounds, loaded.Find("star").Bounds);
        Assert.Equal(bus.Document.NextIdValue, loaded.NextIdValue); // new ids never collide with old ones
    }

    [Fact]
    public void Loading_a_file_from_a_newer_version_or_another_format_fails_clearly()
    {
        Assert.Contains("not a Quattromani", Assert.Throws<CommandException>(() => DocumentFile.Load("{\"format\":\"other\",\"pages\":[]}")).Message);
        var newer = "{\"format\":\"quattromani\",\"version\":" + (DocumentFile.CurrentVersion + 1) + ",\"pages\":[]}";
        Assert.Contains("newer version", Assert.Throws<CommandException>(() => DocumentFile.Load(newer)).Message);
        Assert.Contains("not a valid", Assert.Throws<CommandException>(() => DocumentFile.Load("{nonsense")).Message);
    }

    [Fact]
    public void A_corrupt_file_never_half_loads()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        var json = DocumentFile.Save(bus.Document).Replace("\"width\": 100", "\"width\": 1e999"); // not a finite number
        Assert.Throws<CommandException>(() => DocumentFile.Load(json));
    }

    [Fact]
    public void Copy_and_paste_make_new_ids_and_numbered_names()
    {
        var bus = T.NewBus();
        var id = bus.Rect("logo");
        var data = DocumentFile.ExportObjects(bus.Document.Objects);
        var pasted = bus.Execute([new PasteObjects { Data = data, Dx = 20, Dy = 20 }]).CreatedIds;
        Assert.NotEqual(id, pasted[0]);
        Assert.Equal("logo 2", bus.Document.Find(pasted[0]).Name);
        Assert.Equal(20, bus.Document.PageBounds(bus.Document.Find(pasted[0])).X);
    }

    [Fact]
    public void Duplicate_keeps_style_and_sits_just_above_the_original()
    {
        var bus = T.NewBus();
        bus.Execute([new CreateShape { Type = "rectangle", Name = "a", Width = 10, Height = 10, Fill = "#123456", Opacity = 0.4 }]);
        bus.Rect("top");
        var copy = bus.Execute([new DuplicateObjects { Targets = ["a"], Dx = 5, Dy = 5 }]).CreatedIds[0];
        var objects = bus.Document.Pages[0].Layers[0].Objects;
        Assert.Equal(["a", "a 2", "top"], objects.Select(o => o.Name));
        Assert.Equal(0.4, bus.Document.Find(copy).Opacity);
    }
}
