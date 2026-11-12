using Avalonia.Controls;
using Avalonia.Input;
using Quattromani.Core;

namespace Quattromani.App;

public partial class MainWindow
{
    // Right click on the canvas: Paste and Select all are always there; the rest adapts to what is selected.
    void ShowCanvasMenu()
    {
        var ids = Canvas.SelectedIds.ToList();
        var n = ids.Count;
        var (groups, canConvert) = n == 0 ? (0, false) : _bus.Read(d =>
        {
            var objs = ids.Select(id => d.AllObjects.FirstOrDefault(o => o.Id == id)).OfType<SceneObject>().ToList();
            return (objs.Count(o => o is GroupObject), objs.Any(o => o is not PathObject));
        });
        var cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyGesture Key_(Key k, KeyModifiers m = KeyModifiers.None) => new(k, cmd | m);
        MenuItem Sub(string header, params Control[] items) => new() { Header = header, ItemsSource = items };

        var items = new List<Control>();
        if (n > 0)
        {
            items.Add(Entry("Cut", () => CopySelection(true), gesture: Key_(Avalonia.Input.Key.X)));
            items.Add(Entry("Copy", () => CopySelection(false), gesture: Key_(Avalonia.Input.Key.C)));
        }
        var here = Canvas.ContextPoint;
        items.Add(Entry("Paste", () => _ = PasteAsync(), gesture: Key_(Avalonia.Input.Key.V)));
        items.Add(Entry("Paste here", () => _ = PasteAsync(here)));
        if (n > 0)
        {
            items.Add(Entry("Duplicate", Duplicate, gesture: Key_(Avalonia.Input.Key.D)));
            items.Add(Entry("Delete", () => Exec(new DeleteObjects { Targets = ids }), gesture: new KeyGesture(Avalonia.Input.Key.Delete)));
            items.Add(new Separator());
            items.Add(Sub("Arrange",
                Entry("To front", () => OrderSelected("front"), gesture: Key_(Avalonia.Input.Key.OemCloseBrackets, KeyModifiers.Shift)),
                Entry("Forward one", () => OrderSelected("forward"), gesture: Key_(Avalonia.Input.Key.OemCloseBrackets)),
                Entry("Back one", () => OrderSelected("backward"), gesture: Key_(Avalonia.Input.Key.OemOpenBrackets)),
                Entry("To back", () => OrderSelected("back"), gesture: Key_(Avalonia.Input.Key.OemOpenBrackets, KeyModifiers.Shift))));
            items.Add(Sub("Align",
                Entry("Left", () => AlignSelected("left", null)), Entry("Center (horizontal)", () => AlignSelected("center", null)), Entry("Right", () => AlignSelected("right", null)),
                new Separator(),
                Entry("Top", () => AlignSelected(null, "top")), Entry("Middle (vertical)", () => AlignSelected(null, "middle")), Entry("Bottom", () => AlignSelected(null, "bottom")),
                new Separator(),
                Entry("Center on page", () => AlignSelected("center", "middle", "page")),
                new Separator(),
                Entry("Distribute horizontally", () => DistributeSelected("x", "gaps"), n >= 3),
                Entry("Distribute vertically", () => DistributeSelected("y", "gaps"), n >= 3)));
            items.Add(Sub("Flip", Entry("Horizontally", () => FlipSelected("x")), Entry("Vertically", () => FlipSelected("y"))));
            if (n >= 2 || groups > 0) items.Add(new Separator());
            if (n >= 2) items.Add(Entry("Group", Group, gesture: Key_(Avalonia.Input.Key.G)));
            if (groups > 0) items.Add(Entry("Ungroup", Ungroup, gesture: Key_(Avalonia.Input.Key.G, KeyModifiers.Shift)));
            if (n >= 2)
                items.Add(Sub("Combine shapes", Entry("Weld", () => RunBoolean("weld")), Entry("Trim", () => RunBoolean("trim")),
                    Entry("Intersect", () => RunBoolean("intersect")), Entry("Subtract", () => RunBoolean("subtract"))));
            if (canConvert) items.Add(Entry("Convert to curves", ToCurves, gesture: Key_(Avalonia.Input.Key.Q)));
            items.Add(new Separator());
            items.AddRange(ObjectStateItems());
        }
        else
        {
            items.Add(new Separator());
            items.Add(Entry("Select all", Canvas.SelectAll, gesture: Key_(Avalonia.Input.Key.A)));
        }
        new ContextMenu { ItemsSource = items }.Open(Canvas);
    }
}
