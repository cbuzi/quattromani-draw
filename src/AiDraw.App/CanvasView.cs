using System.Numerics;
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

public enum Tool { Select, Rectangle, Ellipse }

/// <summary>Canvas Skia con pan/zoom, selezione, spostamento, ridimensionamento e rotazione. Le modifiche passano solo dal CommandBus.</summary>
public sealed class CanvasView : Control
{
    enum Drag { None, Pan, Move, Create, Resize, Rotate }

    Point _pan = new(120, 90); // posizione schermo dell'origine del documento
    double _zoom = 1;
    Drag _drag;
    Point _last, _start;
    Avalonia.Vector _moveOffset; // spostamento in corso, in coordinate documento (anteprima)
    Point _createStart, _createEnd; // rettangolo in creazione, in coordinate documento
    Tool _tool;

    // Ridimensionamento / rotazione in corso (anteprima: il documento cambia solo al rilascio).
    Item? _dragItem;
    double _handleX, _handleY; // maniglia trascinata, in frazioni del rettangolo
    double _newW, _newH;
    Vector2 _rotCenter;
    double _startAngle, _rotDelta;

    const long HighlightMs = 3000;
    readonly Dictionary<string, long> _highlights = []; // id oggetto -> TickCount64 della modifica dell'agente
    readonly DispatcherTimer _fade;

    public CommandBus? Bus { get; set; }
    public string? SelectedId { get; private set; }
    public double Zoom => _zoom;

    /// <summary>Stile applicato alle nuove forme.</summary>
    public string? Fill { get; set; } = "#3B82F6";
    public string? Stroke { get; set; } = "#1F2937";
    public double StrokeWidth { get; set; } = 2;

    public Tool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            Cursor = value == Tool.Select ? null : new Cursor(StandardCursorType.Cross);
            ToolChanged?.Invoke();
        }
    }

    public event Action? SelectionChanged;
    public event Action? ViewChanged;
    public event Action? ToolChanged;

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

    // Il punto viene portato nello spazio locale dell'oggetto, così funziona anche con oggetti ruotati.
    static bool Contains(SceneObject o, Point p)
    {
        if (!Matrix3x2.Invert(o.Transform, out var inv)) return false;
        var l = Vector2.Transform(new((float)p.X, (float)p.Y), inv);
        if (o is EllipseObject)
        {
            var rx = o.Width / 2; var ry = o.Height / 2;
            var dx = (l.X - (o.X + rx)) / rx; var dy = (l.Y - (o.Y + ry)) / ry;
            return dx * dx + dy * dy <= 1;
        }
        return l.X >= o.X && l.X <= o.X + o.Width && l.Y >= o.Y && l.Y <= o.Y + o.Height;
    }

    Item? SnapshotSelected() =>
        SelectedId is { } id ? Bus?.Read(d => d.Objects.FirstOrDefault(o => o.Id == id) is { } o ? Item.From(o) : null) : null;

    /// <summary>Maniglia sotto il cursore (tolleranza in pixel schermo), se c'è una selezione.</summary>
    (Handle Handle, Item Item)? HitHandle(Point screen)
    {
        if (SnapshotSelected() is not { } it) return null;
        foreach (var h in HandlesOf(it, _zoom))
        {
            var sx = h.Pos.X * _zoom + _pan.X; var sy = h.Pos.Y * _zoom + _pan.Y;
            if (Math.Abs(sx - screen.X) <= 7 && Math.Abs(sy - screen.Y) <= 7) return (h, it);
        }
        return null;
    }

    double AngleFromCenter(Point doc) => Math.Atan2(doc.Y - _rotCenter.Y, doc.X - _rotCenter.X) * 180 / Math.PI;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var pt = e.GetCurrentPoint(this);
        _last = _start = pt.Position;

        if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
            _drag = Drag.Pan;
        else if (pt.Properties.IsLeftButtonPressed && Tool != Tool.Select)
        {
            _createStart = _createEnd = ToDoc(pt.Position);
            _drag = Drag.Create;
        }
        else if (pt.Properties.IsLeftButtonPressed)
        {
            if (HitHandle(pt.Position) is var (h, it))
            {
                _dragItem = it;
                _newW = it.W; _newH = it.H;
                if (h.Rotate)
                {
                    _drag = Drag.Rotate;
                    _rotDelta = 0;
                    _rotCenter = Vector2.Transform(new((float)(it.X + it.W / 2), (float)(it.Y + it.H / 2)), it.M);
                    _startAngle = AngleFromCenter(ToDoc(pt.Position));
                }
                else
                {
                    _drag = Drag.Resize;
                    (_handleX, _handleY) = (h.Hx, h.Hy);
                }
            }
            else
            {
                Select(HitTest(ToDoc(pt.Position)));
                _drag = SelectedId != null ? Drag.Move : Drag.None;
            }
        }
        if (_drag != Drag.None) e.Pointer.Capture(this);
    }

    // Cursori standard di Avalonia: li traduce per Windows, macOS e Linux.
    static readonly Cursor SizeWE = new(StandardCursorType.SizeWestEast), SizeNS = new(StandardCursorType.SizeNorthSouth),
        SizeNWSE = new(StandardCursorType.TopLeftCorner), SizeNESW = new(StandardCursorType.TopRightCorner),
        RotateCursor = new(StandardCursorType.Hand);

    // Cursore di ridimensionamento in base alla direzione reale della maniglia (anche per oggetti ruotati).
    static Cursor CursorFor(Handle h, Item it)
    {
        if (h.Rotate) return RotateCursor;
        var v = h.Pos - PointOn(it, .5, .5);
        var deg = Math.Atan2(v.Y, v.X) * 180 / Math.PI; // 0 = destra, 90 = giù
        var sector = (int)Math.Round((deg % 180 + 180) % 180 / 45) % 4; // 0=orizz., 1=diag. \, 2=vert., 3=diag. /
        return sector switch
        {
            0 => SizeWE, 1 => SizeNWSE, 2 => SizeNS, _ => SizeNESW,
        };
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_drag == Drag.None && Tool == Tool.Select) // nessun gesto: cursore in base a cosa c'è sotto
        {
            Cursor = HitHandle(pos) is var (hh, hit) ? CursorFor(hh, hit) : null;
            return;
        }
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (_drag == Drag.Pan)
        {
            _pan += pos - _last;
            ViewChanged?.Invoke();
        }
        else if (_drag == Drag.Move)
            _moveOffset = (pos - _start) / _zoom;
        else if (_drag == Drag.Create)
            _createEnd = ToDoc(pos);
        else if (_drag == Drag.Resize && _dragItem is { } it)
        {
            if (!Matrix3x2.Invert(it.M, out var inv)) return;
            var d = ToDoc(pos);
            var p = Vector2.Transform(new((float)d.X, (float)d.Y), inv);
            // L'ancora (lato o angolo opposto alla maniglia) resta ferma; la nuova dimensione è la distanza del mouse da essa.
            double ax = 1 - _handleX, ay = 1 - _handleY;
            double nw = _handleX == .5 ? it.W : Math.Max(1, (p.X - (it.X + ax * it.W)) * (_handleX == 1 ? 1 : -1));
            double nh = _handleY == .5 ? it.H : Math.Max(1, (p.Y - (it.Y + ay * it.H)) * (_handleY == 1 ? 1 : -1));
            if (shift && _handleX != .5 && _handleY != .5) // Shift: mantiene le proporzioni
            {
                var s = Math.Max(nw / it.W, nh / it.H);
                (nw, nh) = (it.W * s, it.H * s);
            }
            (_newW, _newH) = (nw, nh);
        }
        else if (_drag == Drag.Rotate)
        {
            var delta = AngleFromCenter(ToDoc(pos)) - _startAngle;
            delta = (delta % 360 + 540) % 360 - 180; // in (-180, 180]
            _rotDelta = shift ? Math.Round(delta / 15) * 15 : delta; // Shift: scatti di 15°
        }
        else
            return;
        _last = pos;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag == Drag.Create)
        {
            var b = CreateBox();
            if (b.Width * _zoom >= 3 && b.Height * _zoom >= 3 && Bus != null)
            {
                var r = Bus.Execute([new CreateShape
                {
                    Type = Tool == Tool.Ellipse ? "ellipse" : "rectangle",
                    X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
                    Fill = Fill ?? "none", Stroke = Stroke ?? "none", StrokeWidth = StrokeWidth,
                }]);
                Select(r.CreatedIds[0]);
                Tool = Tool.Select; // dopo aver disegnato si torna alla selezione
            }
        }
        else if (_drag == Drag.Move && SelectedId is { } id && (_moveOffset.X != 0 || _moveOffset.Y != 0))
        {
            var (dx, dy) = (_moveOffset.X, _moveOffset.Y);
            _drag = Drag.None; // l'anteprima non serve più: il documento sta per cambiare
            Bus?.Execute([new MoveObjects { Targets = [id], Dx = dx, Dy = dy }]); // un drag = un solo undo
        }
        else if (_drag == Drag.Resize && _dragItem is { } it)
        {
            _drag = Drag.None;
            Bus?.Execute([new ResizeObject
            {
                Targets = [it.Id], Width = _newW, Height = _newH, Anchor = Anchor.NameOf(1 - _handleX, 1 - _handleY),
            }]);
        }
        else if (_drag == Drag.Rotate && _dragItem is { } rit && Math.Abs(_rotDelta) > 0.01)
        {
            _drag = Drag.None;
            Bus?.Execute([new RotateObjects { Targets = [rit.Id], Degrees = _rotDelta }]);
        }
        _moveOffset = default;
        _dragItem = null;
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

    Box CreateBox() => new(Math.Min(_createStart.X, _createEnd.X), Math.Min(_createStart.Y, _createEnd.Y),
        Math.Abs(_createEnd.X - _createStart.X), Math.Abs(_createEnd.Y - _createStart.Y));

    // L'oggetto selezionato come apparirà a fine gesto (anteprima).
    Item Preview(Item it) => _drag switch
    {
        Drag.Move => it with { M = it.M * Matrix3x2.CreateTranslation((float)_moveOffset.X, (float)_moveOffset.Y) },
        Drag.Resize => ResizedPreview(it),
        Drag.Rotate => it with { M = it.M * Matrix3x2.CreateRotation((float)(_rotDelta * Math.PI / 180), _rotCenter) },
        _ => it,
    };

    Item ResizedPreview(Item it)
    {
        var b = ResizeObject.Resized(new(it.X, it.Y, it.W, it.H), 1 - _handleX, 1 - _handleY, _newW, _newH);
        return it with { X = b.X, Y = b.Y, W = b.Width, H = b.Height };
    }

    public override void Render(DrawingContext context)
    {
        if (Bus == null) return;
        // Senza uno sfondo (anche trasparente) il controllo non riceve gli eventi del mouse.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var items = Bus.Read(d => d.Objects.Select(Item.From).ToList());
        for (var i = 0; i < items.Count; i++)
            if (items[i].Id == SelectedId) items[i] = Preview(items[i]);
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var now = Environment.TickCount64;
        var glow = _highlights.ToDictionary(h => h.Key, h => (float)Math.Max(0, 1 - (now - h.Value) / (double)HighlightMs));
        context.Custom(new SceneOp(new Rect(Bounds.Size), items, SelectedId, _pan, _zoom, dark, glow,
            _drag == Drag.Create ? CreateBox() : null, Tool == Tool.Ellipse));
    }

    // Copia immutabile degli oggetti: il rendering gira su un altro thread.
    sealed record Item(string Id, bool IsEllipse, double X, double Y, double W, double H,
        Matrix3x2 M, string? Fill, string? Stroke, double StrokeWidth)
    {
        public Box Bounds => SceneObject.BoundsOf(X, Y, W, H, M);

        public static Item From(SceneObject o) => new(o.Id, o is EllipseObject, o.X, o.Y, o.Width, o.Height,
            o.Transform, o.Fill, o.Stroke, o.StrokeWidth);
    }

    /// <summary>Maniglia di selezione; Hx/Hy sono frazioni del rettangolo locale, Pos è in coordinate documento.</summary>
    sealed record Handle(double Hx, double Hy, bool Rotate, Vector2 Pos);

    static Vector2 PointOn(Item it, double fx, double fy) =>
        Vector2.Transform(new((float)(it.X + fx * it.W), (float)(it.Y + fy * it.H)), it.M);

    static List<Handle> HandlesOf(Item it, double zoom)
    {
        var list = new List<Handle>();
        foreach (var (_, hx, hy) in Anchor.All)
            if (!(hx == .5 && hy == .5)) list.Add(new(hx, hy, false, PointOn(it, hx, hy)));

        // Maniglia di rotazione: sopra il lato alto, a distanza fissa in pixel schermo.
        var top = PointOn(it, .5, 0);
        var dir = top - PointOn(it, .5, .5);
        dir = dir.LengthSquared() < 1e-6f ? new(0, -1) : Vector2.Normalize(dir);
        list.Add(new(.5, 0, true, top + dir * (float)(28 / zoom)));
        return list;
    }

    sealed class SceneOp(Rect bounds, List<Item> items, string? selected, Point pan, double zoom, bool dark,
        Dictionary<string, float> glow, Box? preview, bool previewEllipse)
        : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        static SKPoint ToSk(Vector2 v) => new(v.X, v.Y);

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

            if (preview is { } pv)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)(1.5 / zoom);
                paint.Color = new SKColor(0x3B, 0x82, 0xF6);
                var pr = SKRect.Create((float)pv.X, (float)pv.Y, (float)pv.Width, (float)pv.Height);
                if (previewEllipse) canvas.DrawOval(pr, paint); else canvas.DrawRect(pr, paint);
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

            // Selezione: contorno orientato e maniglie.
            if (items.FirstOrDefault(i => i.Id == selected) is { } sel && preview is null)
                DrawSelection(canvas, paint, sel);
            canvas.Restore();
        }

        void DrawSelection(SKCanvas canvas, SKPaint paint, Item sel)
        {
            var accent = new SKColor(0x3B, 0x82, 0xF6);
            using var outline = new SKPath();
            outline.MoveTo(ToSk(PointOn(sel, 0, 0)));
            outline.LineTo(ToSk(PointOn(sel, 1, 0)));
            outline.LineTo(ToSk(PointOn(sel, 1, 1)));
            outline.LineTo(ToSk(PointOn(sel, 0, 1)));
            outline.Close();
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)(1.5 / zoom);
            paint.Color = accent;
            canvas.DrawPath(outline, paint);

            var size = (float)(8 / zoom);
            foreach (var h in HandlesOf(sel, zoom))
            {
                var c = ToSk(h.Pos);
                if (h.Rotate)
                {
                    paint.Style = SKPaintStyle.Stroke; paint.Color = accent;
                    canvas.DrawLine(ToSk(PointOn(sel, .5, 0)), c, paint);
                }
                paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White;
                if (h.Rotate) canvas.DrawCircle(c, size * 0.65f, paint);
                else canvas.DrawRect(SKRect.Create(c.X - size / 2, c.Y - size / 2, size, size), paint);
                paint.Style = SKPaintStyle.Stroke; paint.Color = accent;
                if (h.Rotate) canvas.DrawCircle(c, size * 0.65f, paint);
                else canvas.DrawRect(SKRect.Create(c.X - size / 2, c.Y - size / 2, size, size), paint);
            }
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
