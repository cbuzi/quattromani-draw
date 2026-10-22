using Avalonia.Controls;
using Quattromani.Core;

namespace Quattromani.App;

/// <summary>Stroke style (dash, line ends, corners) and opacity in the properties panel.</summary>
public partial class MainWindow
{
    static readonly string[] DashNames = ["solid", "dashed", "dotted", "dashdot"];
    static readonly string[] DashLabels = ["Solid", "Dashed", "Dotted", "Dash-dot", "Custom"];

    void InitStrokeStyle()
    {
        DashCombo.ItemsSource = DashLabels;
        CapCombo.ItemsSource = new[] { "Butt", "Round", "Square" };
        JoinCombo.ItemsSource = new[] { "Miter", "Round", "Bevel" };
        DashCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncing && DashCombo.SelectedIndex is >= 0 and < 4) ApplyStrokeStyle(dash: DashNames[DashCombo.SelectedIndex]);
        };
        CapCombo.SelectionChanged += (_, _) => { if (!_syncing && CapCombo.SelectedIndex >= 0) ApplyStrokeStyle(cap: StrokeStyle.Caps[CapCombo.SelectedIndex]); };
        JoinCombo.SelectionChanged += (_, _) => { if (!_syncing && JoinCombo.SelectedIndex >= 0) ApplyStrokeStyle(join: StrokeStyle.Joins[JoinCombo.SelectedIndex]); };
        OpacityBox.ValueChanged += (_, e) => { if (!_syncing && e.NewValue is { } v) ApplyStrokeStyle(opacity: (double)v / 100); };
        RadiusBox.ValueChanged += (_, e) =>
        {
            if (_syncing || e.NewValue is not { } v || Canvas.SelectedIds.Count == 0) return;
            if (Exec(new SetCornerRadius { Targets = Canvas.SelectedIds.ToList(), Radius = (double)v }) == null) Refresh();
        };
        SyncStrokeStyle();
    }

    // To the selection if there is one; otherwise it becomes the style of the shapes drawn next.
    void ApplyStrokeStyle(string? dash = null, string? cap = null, string? join = null, double? opacity = null)
    {
        if (dash != null) Canvas.NewDash = dash;
        if (cap != null) Canvas.NewCap = cap;
        if (join != null) Canvas.NewJoin = join;
        if (opacity != null) Canvas.NewOpacity = opacity.Value;
        if (Canvas.SelectedIds.Count == 0) return;
        if (Exec(new SetStyle { Targets = Canvas.SelectedIds.ToList(), Dash = dash, Cap = cap, Join = join, Opacity = opacity }) == null) Refresh();
    }

    /// <summary>Shows the style of the selected object, or the one new shapes will get. Call inside the _syncing section of Refresh.</summary>
    void SyncStrokeStyle()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        var o = Canvas.SelectedId is { } id ? _bus.Read(d => d.AllObjects.FirstOrDefault(x => x.Id == id) is { } x ? (x.StrokeDash, x.StrokeWidth, x.StrokeCap, x.StrokeJoin, x.Opacity) : ((double[]?, double, string, string, double)?)null) : null;
        var (dash, width, cap, join, opacity) = o ?? (DashArray(Canvas.NewDash), Canvas.StrokeWidth, Canvas.NewCap, Canvas.NewJoin, Canvas.NewOpacity);
        DashCombo.SelectedIndex = DashPreset(dash, width);
        CapCombo.SelectedIndex = Math.Max(0, Array.IndexOf(StrokeStyle.Caps, cap));
        JoinCombo.SelectedIndex = Math.Max(0, Array.IndexOf(StrokeStyle.Joins, join));
        OpacityBox.Value = (decimal)Math.Round(opacity * 100);

        // Corner radius: only when the whole selection is rectangles.
        var ids = Canvas.SelectedIds.ToList();
        var radius = ids.Count == 0 ? null : _bus.Read(d =>
        {
            var objs = ids.Select(id => d.AllObjects.FirstOrDefault(x => x.Id == id)).ToList();
            return objs.All(x => x is RectangleObject) ? (double?)((RectangleObject)objs[0]!).CornerRadius : null;
        });
        RadiusPanel.IsVisible = radius != null;
        if (radius != null) RadiusBox.Value = (decimal)radius.Value;
        _syncing = wasSyncing;
    }

    static double[]? DashArray(string preset) => StrokeStyle.ParseDash(preset, 1);

    // Which preset a stored dash pattern is (compared at the stroke's width); 4 = something else.
    static int DashPreset(double[]? dash, double width)
    {
        if (dash is null) return 0;
        for (var i = 1; i < 4; i++)
        {
            var p = StrokeStyle.ParseDash(DashNames[i], width)!;
            if (p.Length == dash.Length && p.Zip(dash).All(t => Math.Abs(t.First - t.Second) < 0.01)) return i;
        }
        return 4;
    }
}
