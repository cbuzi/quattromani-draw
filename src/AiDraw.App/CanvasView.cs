using AiDraw.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using SkiaSharp;

namespace AiDraw.App;

/// <summary>Canvas Skia con pan/zoom, selezione e spostamento. Le modifiche passano solo dal CommandBus.</summary>
public sealed class CanvasView : Control
{
    enum Drag { None, Pan, Move }

    Point _pan = new(120, 90); // posizione schermo dell'origine del documento
    double _zoom = 1;
    Drag _drag;
    Point _last, _start;
    Vector _moveOffset; // spostamento in corso, in coordinate documento (anteprima)

    const long HighlightMs = 3000;
    readonly Dictionary<string, long> _highlights = []; // id oggetto -> TickCount64 della modifica dell'agente
    readonly DispatcherTimer _fade;

    public CommandBus? Bus { get; set; }
    public string? SelectedId { get; private set; }
    public double Zoom => _zoom;

    public event Action? SelectionChanged;
    public event Action? ViewChanged;

    public CanvasView()
    {
        ClipToBounds = true;
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        _fade = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => FadeTick());
    }

    /// <summary>Evidenzia per qualche secondo gli oggetti creati o modificati dall'agente.</summary>
    public void Highlight(IEnumerable<string> ids)
    {
        var now = Environment.TickCount64;
        foreach (var id in ids) _highlights[id] = now;
        if (_highlights.Count > 0) _fade.Start();
        InvalidateVisual();
    }

    void FadeTick()
    {
        var now = Environment.TickCount64;
        foreach (var id in _highlights.Where(h => now - h.Value > HighlightMs).Select(h => h.Key).ToList())
            _highlights.Remove(id);
        if (_highlights.Count == 0) _fade.Stop();
        InvalidateVisual();
    }

    public void Select(string? id)
    {
        if (SelectedId == id) return;
        SelectedId = id;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    Point ToDoc(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);

    string? HitTest(Point doc) => Bus?.Read(d => d.Objects.Reverse().FirstOrDefault(o => Contains(o, doc))?.Id);

    static bool Contains(SceneObject o, Point p)
    {
        var b = o.Bounds;
        if (o is EllipseObject)
        {
            var rx = b.Width / 2; var ry = b.Height / 2;
            var dx = (p.X - (b.X + rx)) / rx; var dy = (p.Y - (b.Y + ry)) / ry;
            return dx * dx + dy * dy <= 1;
        }
        return p.X >= b.X && p.X <= b.X + b.Width && p.Y >= b.Y && p.Y <= b.Y + b.Height;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var pt = e.GetCurrentPoint(this);
        _last = _start = pt.Position;

        if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
            _drag = Drag.Pan;
        else if (pt.Properties.IsLeftButtonPressed)
        {
            Select(HitTest(ToDoc(pt.Position)));
            _drag = SelectedId != null ? Drag.Move : Drag.None;
        }
        if (_drag != Drag.None) e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_drag == Drag.Pan)
        {
            _pan += pos - _last;
            ViewChanged?.Invoke();
        }
        else if (_drag == Drag.Move)
            _moveOffset = (pos - _start) / _zoom;
        else
            return;
        _last = pos;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag == Drag.Move && SelectedId is { } id && (_moveOffset.X != 0 || _moveOffset.Y != 0))
        {
            var (dx, dy) = (_moveOffset.X, _moveOffset.Y);
            _moveOffset = default;
            Bus?.Execute([new MoveObjects { Targets = [id], Dx = dx, Dy = dy }]); // un drag = un solo undo
        }
        _moveOffset = default;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        var newZoom = Math.Clamp(_zoom * Math.Pow(1.1, e.Delta.Y), 0.05, 64);
        var k = newZoom / _zoom;
        _pan = new(pos.X - (pos.X - _pan.X) * k, pos.Y - (pos.Y - _pan.Y) * k);
        _zoom = newZoom;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Bus == null) return;
        // Senza uno sfondo (anche trasparente) il controllo non riceve gli eventi del mouse.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var items = Bus.Read(d => d.Objects.Select(o => new Item(o.Id, o is EllipseObject, o.Bounds, o.X, o.Y, o.Width, o.Height,
            o.Transform, o.Fill, o.Stroke, o.StrokeWidth)).ToList());
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var now = Environment.TickCount64;
        var glow = _highlights.ToDictionary(h => h.Key, h => (float)Math.Max(0, 1 - (now - h.Value) / (double)HighlightMs));
        context.Custom(new SceneOp(new Rect(Bounds.Size), items, SelectedId, _moveOffset, _pan, _zoom, dark, glow));
    }

    // Copia immutabile degli oggetti: il rendering gira su un altro thread.
    sealed record Item(string Id, bool IsEllipse, Box Bounds, double X, double Y, double W, double H,
        System.Numerics.Matrix3x2 M, string? Fill, string? Stroke, double StrokeWidth);

    sealed class SceneOp(Rect bounds, List<Item> items, string? selected, Vector offset, Point pan, double zoom, bool dark,
        Dictionary<string, float> glow)
        : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height));
            canvas.Clear(dark ? new SKColor(0x1E, 0x1F, 0x22) : new SKColor(0xEE, 0xF0, 0xF3));
            canvas.Translate((float)pan.X, (float)pan.Y);
            canvas.Scale((float)zoom);

            using var paint = new SKPaint { IsAntialias = true };
            foreach (var it in items)
            {
                canvas.Save();
                if (it.Id == selected) canvas.Translate((float)offset.X, (float)offset.Y);
                var m = new SKMatrix(it.M.M11, it.M.M21, it.M.M31, it.M.M12, it.M.M22, it.M.M32, 0, 0, 1);
                canvas.Concat(in m);
                var r = SKRect.Create((float)it.X, (float)it.Y, (float)it.W, (float)it.H);

                if (ParseColor(it.Fill) is { } fill)
                {
                    paint.Style = SKPaintStyle.Fill; paint.Color = fill;
                    if (it.IsEllipse) canvas.DrawOval(r, paint); else canvas.DrawRect(r, paint);
                }
                if (ParseColor(it.Stroke) is { } stroke && it.StrokeWidth > 0)
                {
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = (float)it.StrokeWidth; paint.Color = stroke;
                    if (it.IsEllipse) canvas.DrawOval(r, paint); else canvas.DrawRect(r, paint);
                }
                canvas.Restore();
            }

            // Oggetti toccati dall'agente: alone viola che sfuma.
            foreach (var it in items)
            {
                if (!glow.TryGetValue(it.Id, out var a)) continue;
                var pad = 4 / zoom;
                var r = SKRect.Create((float)(it.Bounds.X - pad), (float)(it.Bounds.Y - pad),
                    (float)(it.Bounds.Width + 2 * pad), (float)(it.Bounds.Height + 2 * pad));
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new SKColor(0xA8, 0x55, 0xF7, (byte)(50 * a));
                canvas.DrawRect(r, paint);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)(2.5 / zoom);
                paint.Color = new SKColor(0xA8, 0x55, 0xF7, (byte)(255 * a));
                canvas.DrawRect(r, paint);
            }

            // Selezione: contorno sul bounding box (+ anteprima dello spostamento).
            if (items.FirstOrDefault(i => i.Id == selected) is { } sel)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)(1.5 / zoom);
                paint.Color = new SKColor(0x3B, 0x82, 0xF6);
                canvas.DrawRect(SKRect.Create((float)(sel.Bounds.X + offset.X), (float)(sel.Bounds.Y + offset.Y),
                    (float)sel.Bounds.Width, (float)sel.Bounds.Height), paint);
            }
            canvas.Restore();
        }

        // Formato del documento: "#RRGGBB" o "#RRGGBBAA" (diverso da quello ARGB di Skia).
        static SKColor? ParseColor(string? s)
        {
            if (s is not { Length: 7 or 9 }) return null;
            var v = Convert.ToUInt32(s[1..], 16);
            return s.Length == 7
                ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
                : new SKColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
    }
}
