using Quattromani.Core;

namespace Quattromani.Core.Tests;

static class T
{
    public static CommandBus NewBus() => new(new Document());

    public static string Rect(this CommandBus bus, string name, double x = 0, double y = 0, double w = 100, double h = 100, string? fill = "#FF0000", string? layer = null) =>
        bus.Execute([new CreateShape { Type = "rectangle", Name = name, X = x, Y = y, Width = w, Height = h, Fill = fill, Layer = layer }]).CreatedIds[0];

    public static Box Bounds(this CommandBus bus, string name) => bus.Document.PageBounds(bus.Document.Find(name));

    public static void AssertBox(Box expected, Box actual, double tolerance = 0.05)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Width, expected.Width - tolerance, expected.Width + tolerance);
        Assert.InRange(actual.Height, expected.Height - tolerance, expected.Height + tolerance);
    }
}
