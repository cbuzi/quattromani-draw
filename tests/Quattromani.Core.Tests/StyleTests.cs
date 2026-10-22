using Quattromani.Core;

namespace Quattromani.Core.Tests;

public class StyleTests
{
    [Theory]
    [InlineData("dashed", new[] { 24.0, 12.0 })]            // scaled to a stroke width of 6
    [InlineData("dotted", new[] { 6.0, 9.0 })]
    [InlineData("dashdot", new[] { 24.0, 12.0, 6.0, 12.0 })]
    [InlineData("8 4", new[] { 8.0, 4.0 })]
    [InlineData("8,4,2,4", new[] { 8.0, 4.0, 2.0, 4.0 })]
    public void Dash_presets_scale_with_the_stroke_width_and_numbers_are_taken_as_px(string spec, double[] expected) =>
        Assert.Equal(expected, StrokeStyle.ParseDash(spec, 6));

    [Theory]
    [InlineData("solid")]
    [InlineData("none")]
    public void Solid_means_no_dash(string spec) => Assert.Null(StrokeStyle.ParseDash(spec, 3));

    [Theory]
    [InlineData("zigzag")]
    [InlineData("0 0")]
    [InlineData("1 2 3 4 5 6 7 8 9")]
    [InlineData("-3 2")]
    public void Invalid_dash_values_explain_the_valid_ones(string spec)
    {
        var ex = Assert.Throws<CommandException>(() => StrokeStyle.ParseDash(spec, 1));
        Assert.Contains("dashed", ex.Message);
    }

    [Fact]
    public void Style_applies_to_every_shape_in_a_group_but_opacity_to_the_group_itself()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        bus.Rect("b", x: 200);
        bus.Execute([new GroupObjects { Targets = ["a", "b"], Name = "g" }]);
        bus.Execute([new SetStyle { Targets = ["g"], Stroke = "#000000", StrokeWidth = 4, Dash = "dashed", Cap = "round", Join = "bevel", Opacity = 0.5 }]);

        foreach (var name in new[] { "a", "b" })
        {
            var o = bus.Document.Find(name);
            Assert.Equal("#000000", o.Stroke);
            Assert.Equal([16.0, 8.0], o.StrokeDash!);
            Assert.Equal("round", o.StrokeCap);
            Assert.Equal("bevel", o.StrokeJoin);
            Assert.Equal(1, o.Opacity);
        }
        Assert.Equal(0.5, bus.Document.Find("g").Opacity);

        bus.Undo();
        Assert.Null(bus.Document.Find("a").Stroke);
        Assert.Equal(1, bus.Document.Find("g").Opacity);
    }

    [Fact]
    public void Style_values_are_validated()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        Assert.Throws<CommandException>(() => bus.Execute([new SetStyle { Targets = ["a"], Opacity = 1.5 }]));
        Assert.Throws<CommandException>(() => bus.Execute([new SetStyle { Targets = ["a"], Cap = "pointy" }]));
        Assert.Throws<CommandException>(() => bus.Execute([new SetStyle { Targets = ["a"], Join = "sharp" }]));
        Assert.Throws<CommandException>(() => bus.Execute([new SetStyle { Targets = ["a"], Fill = "rosso" }]));
    }

    [Fact]
    public void Gradients_and_patterns_are_stored_in_canonical_form()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        bus.Execute([new SetStyle { Targets = ["a"], Fill = "linear-gradient(90deg, #ff0000, #0000ff)" }]);
        Assert.Equal("linear-gradient(90deg, #FF0000 0%, #0000FF 100%)", bus.Document.Find("a").Fill);
        bus.Execute([new SetStyle { Targets = ["a"], Fill = "pattern(stripes, #ff0000, #ffffff, 8)" }]);
        Assert.Equal("pattern(stripes, #FF0000, #FFFFFF, 8, 0)", bus.Document.Find("a").Fill);
    }

    [Fact]
    public void Names_that_look_like_ids_are_refused_and_ambiguous_names_ask_for_the_id()
    {
        var bus = T.NewBus();
        bus.Rect("twin");
        bus.Rect("twin");
        var ex = Assert.Throws<CommandException>(() => bus.Document.Find("twin"));
        Assert.Contains("ambiguous", ex.Message);
        Assert.Throws<CommandException>(() => bus.Execute([new RenameObject { Target = "twin", Name = "o12" }]));
    }

    [Fact]
    public void Relative_positioning_places_objects_next_to_a_reference()
    {
        var bus = T.NewBus();
        bus.Rect("house", 100, 100, 50, 50);
        bus.Execute([new CreateShape { Type = "rectangle", Name = "door", Width = 10, Height = 20, Rel = new Placement { RightOf = "house", Margin = 10 } }]);
        var door = bus.Bounds("door");
        Assert.Equal(160, door.X);              // house right edge (150) + margin
        Assert.Equal(115, door.Y);              // vertically centered on the house (125 - 10)
    }
}
