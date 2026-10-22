using Quattromani.Core;

namespace Quattromani.Core.Tests;

public class GeometryTests
{
    static Box ResultBounds(CommandBus bus, string op)
    {
        bus.Rect("a", 0, 0, 100, 100);
        bus.Rect("b", 50, 50, 100, 100);
        var id = bus.Execute([new BooleanOp { Op = op, Targets = ["a", "b"] }]).CreatedIds[0];
        return bus.Document.PageBounds(bus.Document.Find(id));
    }

    [Fact] public void Weld_covers_both_shapes() => T.AssertBox(new Box(0, 0, 150, 150), ResultBounds(T.NewBus(), "weld"));
    [Fact] public void Intersect_is_the_overlap() => T.AssertBox(new Box(50, 50, 50, 50), ResultBounds(T.NewBus(), "intersect"));
    [Fact] public void Subtract_keeps_the_extent_of_the_lowest_shape() => T.AssertBox(new Box(0, 0, 100, 100), ResultBounds(T.NewBus(), "subtract"));

    [Fact]
    public void Boolean_of_shapes_that_do_not_overlap_says_so()
    {
        var bus = T.NewBus();
        bus.Rect("a", 0, 0, 10, 10);
        bus.Rect("b", 100, 100, 10, 10);
        var ex = Assert.Throws<CommandException>(() => bus.Execute([new BooleanOp { Op = "intersect", Targets = ["a", "b"] }]));
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void Polygons_and_stars_fill_their_box_exactly()
    {
        var bus = T.NewBus();
        bus.Execute([
            new CreateShape { Type = "polygon", Name = "hex", Sides = 6, X = 10, Y = 20, Width = 100, Height = 80 },
            new CreateShape { Type = "star", Name = "star", Points = 5, InnerRatio = 0.4, X = 10, Y = 20, Width = 100, Height = 96 },
        ]);
        T.AssertBox(new Box(10, 20, 100, 80), bus.Bounds("hex"));
        T.AssertBox(new Box(10, 20, 100, 96), bus.Bounds("star"));
        Assert.Equal(7, ((PathObject)bus.Document.Find("hex")).Segments.Count); // M, five L, Z
    }

    [Fact]
    public void Shape_parameters_are_validated()
    {
        var bus = T.NewBus();
        Assert.Throws<CommandException>(() => bus.Execute([new CreateShape { Type = "polygon", Sides = 2, Width = 10, Height = 10 }]));
        Assert.Throws<CommandException>(() => bus.Execute([new CreateShape { Type = "star", Points = 5, InnerRatio = 1.5, Width = 10, Height = 10 }]));
        Assert.Throws<CommandException>(() => bus.Execute([new CreateShape { Type = "line" }]));
        Assert.Throws<CommandException>(() => bus.Execute([new CreateShape { Type = "ellipse", Width = 10, Height = 10, CornerRadius = 3 }]));
    }

    [Fact]
    public void A_line_may_go_in_any_direction()
    {
        var bus = T.NewBus();
        bus.Execute([new CreateShape { Type = "line", Name = "l", X = 10, Y = 10, Width = 100, Height = -40 }]);
        T.AssertBox(new Box(10, -30, 100, 40), bus.Bounds("l"));
    }

    [Fact]
    public void Corner_radius_is_limited_to_half_the_shorter_side_and_survives_conversion_to_a_path()
    {
        var bus = T.NewBus();
        bus.Execute([new CreateShape { Type = "rectangle", Name = "card", Width = 100, Height = 40, CornerRadius = 500 }]);
        Assert.Equal(20, ((RectangleObject)bus.Document.Find("card")).EffectiveRadius);
        bus.Execute([new SetCornerRadius { Targets = ["card"], Radius = 8 }]);
        Assert.Equal(8, ((RectangleObject)bus.Document.Find("card")).EffectiveRadius);
        bus.Execute([new ConvertToPath { Targets = ["card"] }]);
        var path = (PathObject)bus.Document.Find("card");
        Assert.Contains(path.Segments, s => s.Op == 'C'); // rounded: curves at the corners
        T.AssertBox(new Box(0, 0, 100, 40), bus.Bounds("card"));
    }

    [Fact]
    public void Flip_mirrors_around_the_center_and_keeps_the_bounds()
    {
        var bus = T.NewBus();
        bus.Execute([new CreateShape { Type = "path", Name = "arrow", D = "M 0 0 L 40 20 L 0 40 Z", Fill = "#FF0000" }]);
        bus.Execute([new FlipObjects { Targets = ["arrow"], Axis = "x" }]);
        T.AssertBox(new Box(0, 0, 40, 40), bus.Bounds("arrow"));
        Assert.Equal(-1, bus.Document.Find("arrow").Transform.M11);
        bus.Undo();
        Assert.Equal(1, bus.Document.Find("arrow").Transform.M11);
    }

    [Fact]
    public void Flipping_objects_together_mirrors_their_layout()
    {
        var bus = T.NewBus();
        bus.Rect("a", 0, 0, 10, 10);
        bus.Rect("b", 90, 0, 10, 10);
        bus.Execute([new FlipObjects { Targets = ["a", "b"], Axis = "x", Together = true }]);
        Assert.Equal(90, bus.Bounds("a").X);
        Assert.Equal(0, bus.Bounds("b").X);
    }

    [Theory]
    [InlineData("M 0 0 h 10 v 10 z", 4)]            // relative commands, closed
    [InlineData("M 0 0 L 10 0 L 10 10 Z", 4)]
    [InlineData("M 0 0 Q 5 10 10 0", 2)]             // a quadratic becomes a cubic
    public void Path_data_is_normalized_to_absolute_segments(string d, int segments) =>
        Assert.Equal(segments, PathData.Parse(d).Count);

    [Fact]
    public void Path_data_that_is_not_valid_explains_itself()
    {
        var ex = Assert.Throws<CommandException>(() => PathData.Parse("L 10 10"));
        Assert.Contains("M", ex.Message);
    }

    [Fact]
    public void Freehand_fitting_turns_a_dense_arc_into_a_few_curves_and_keeps_sharp_corners()
    {
        var rnd = new Random(1);
        var arc = Enumerable.Range(0, 120).Select(i => (X: 100 * Math.Cos(i * Math.PI / 2 / 119) + rnd.NextDouble() - .5, Y: 100 * Math.Sin(i * Math.PI / 2 / 119) + rnd.NextDouble() - .5)).ToList();
        var fitted = ImageTrace.FitOpen(arc, 2);
        Assert.True(fitted.Count is >= 2 and <= 6, $"expected few segments, got {fitted.Count}");

        var v = Enumerable.Range(0, 40).Select(i => (X: i * 3.0, Y: i * 3.0)).Concat(Enumerable.Range(1, 40).Select(i => (X: 117.0 + i * 3, Y: 117.0 - i * 3))).ToList();
        var corner = ImageTrace.FitOpen(v, 2).First(s => s.Op == 'C');
        Assert.InRange(corner.X, 115, 119); // the first curve ends at the sharp corner
        Assert.InRange(corner.Y, 115, 119);
    }
}
