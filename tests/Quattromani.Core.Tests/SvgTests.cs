using Quattromani.Core;

namespace Quattromani.Core.Tests;

public class SvgTests
{
    static GroupObject Import(CommandBus bus, string svg, out List<string>? notes)
    {
        var cmd = new ImportSvg { Svg = svg };
        var id = bus.Execute([cmd]).CreatedIds[0];
        notes = cmd.Notes;
        return (GroupObject)bus.Document.Find(id);
    }

    [Fact]
    public void Export_then_import_keeps_shapes_style_and_names()
    {
        var bus = T.NewBus();
        bus.Execute([
            new CreateShape { Type = "rectangle", Name = "card", X = 10, Y = 10, Width = 80, Height = 40, Fill = "linear-gradient(90deg, #FF0000, #0000FF)", CornerRadius = 8 },
            new CreateShape { Type = "path", Name = "road", D = "M 0 100 L 200 100", Stroke = "#111111", StrokeWidth = 6, Dash = "dotted", Cap = "round", Join = "round" },
            new CreateShape { Type = "ellipse", Name = "ghost", X = 50, Y = 50, Width = 40, Height = 40, Fill = "#00FF00", Opacity = 0.25 },
        ]);
        var svg = SvgFile.Export(bus.Document);

        var other = T.NewBus();
        Import(other, svg, out _);

        var card = (RectangleObject)other.Document.Find("card");
        Assert.Equal(8, card.CornerRadius);
        Assert.StartsWith("linear-gradient(", card.Fill);
        Assert.True(Gradient.TryParse(card.Fill!, out var g) && g.Stops.Count == 2);
        var road = other.Document.Find("road");
        Assert.Equal([6.0, 9.0], road.StrokeDash!);
        Assert.Equal(("round", "round"), (road.StrokeCap, road.StrokeJoin));
        Assert.Equal(0.25, other.Document.Find("ghost").Opacity);
    }

    [Fact]
    public void Import_applies_viewBox_scale_css_classes_and_use()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="48" height="48" viewBox="0 0 24 24">
              <style>.a{fill:#336699}</style>
              <defs><circle id="dot" cx="0" cy="0" r="2"/></defs>
              <rect class="a" width="10" height="10"/>
              <use xlink:href="#dot" x="20" y="20" fill="#FF0000"/>
            </svg>
            """;
        var bus = T.NewBus();
        var group = Import(bus, svg, out _);
        Assert.Equal(2, group.Children.Count);
        Assert.Equal("#336699", group.Children[0].Fill);
        T.AssertBox(new Box(0, 0, 20, 20), group.Children[0].Bounds); // 10 units * scale 2
        Assert.IsType<EllipseObject>(group.Children[1]);
        T.AssertBox(new Box(36, 36, 8, 8), group.Children[1].Bounds);
    }

    [Fact]
    public void Import_can_place_and_scale_the_artwork()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><rect width='10' height='10' fill='#000'/></svg>";
        var bus = T.NewBus();
        var id = bus.Execute([new ImportSvg { Svg = svg, X = 100, Y = 50, Width = 40 }]).CreatedIds[0];
        T.AssertBox(new Box(100, 50, 40, 40), bus.Document.PageBounds(bus.Document.Find(id)));
    }

    [Fact]
    public void Import_reports_what_it_cannot_represent()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg'><rect width='10' height='10' fill='#000' clip-path='url(#c)' filter='url(#f)'/></svg>";
        Import(T.NewBus(), svg, out var notes);
        Assert.Contains(notes!, n => n.Contains("clip-path"));
        Assert.Contains(notes!, n => n.Contains("filter"));
    }

    [Fact]
    public void Import_of_something_that_is_not_svg_fails_clearly()
    {
        var bus = T.NewBus();
        Assert.Contains("not valid XML", Assert.Throws<CommandException>(() => bus.Execute([new ImportSvg { Svg = "<svg" }])).Message);
        Assert.Contains("nothing to import", Assert.Throws<CommandException>(() => bus.Execute([new ImportSvg { Svg = "<svg xmlns='http://www.w3.org/2000/svg'/>" }])).Message);
        Assert.Throws<CommandException>(() => bus.Execute([new ImportSvg()])); // neither src nor svg
    }

    [Fact]
    public void Export_marks_hidden_and_locked_things_and_keeps_layers()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        bus.Execute([new SetObjectState { Targets = ["a"], Hidden = true }, new SetLayer { Layer = "Layer 1", Name = "Background" }]);
        var svg = SvgFile.Export(bus.Document);
        Assert.Contains("display=\"none\"", svg);
        Assert.Contains("inkscape:label=\"Background\"", svg);
    }

    [Fact]
    public void Export_of_a_selection_covers_just_its_box()
    {
        var bus = T.NewBus();
        bus.Rect("a", 100, 200, 30, 40);
        bus.Rect("b", 0, 0, 5, 5);
        var svg = SvgFile.ExportObjects(bus.Document, [bus.Document.Find("a")]);
        Assert.Contains("viewBox=\"100 200 30 40\"", svg);
    }
}
