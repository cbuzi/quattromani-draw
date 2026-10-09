using System.Globalization;
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

public enum Tool { Select, Rectangle, Ellipse, Pen }

/// <summary>
/// Canvas Skia con pan/zoom, selezione (anche multipla), spostamento, ridimensionamento, rotazione e penna.
/// Le modifiche passano solo dal CommandBus.
/// </summary>
public sealed class CanvasView : Control
{
    enum Drag { None, Pan, Move, Create, Resize, Rotate, Marquee }
    enum Kind { Rectangle, Ellipse, Path, Group }

    Point _pan = new(120, 90); // posizione schermo dell'origine del documento
    double _zoom = 1;
    Drag _drag;
    Point _last, _start;
    Avalonia.Vector _moveOffset; // spostamento in corso, in coordinate documento (anteprima)
    Point _createStart, _createEnd; // rettangolo in creazione, in coordinate documento
    Point _marqueeStart, _marqueeEnd; // selezione a rettangolo, in coordinate documento
    bool _marqueeAdd;
    Tool _tool;

    readonly List<string> _selection = [];
    readonly List<Point> _pen = []; // punti della penna, in coordinate documento
    Point _penHover;

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
    public IReadOnlyList<string> SelectedIds => _selection;
    /// <summary>L'id selezionato, solo se la selezione è esattamente un oggetto.</summary>
    public string? SelectedId => _selection.Count == 1 ? _selection[0] : null;
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
            if (value != Tool.Pen) _pen.Clear();
            Cursor = value == Tool.Select ? null : new Cursor(StandardCursorType.Cross);
            ToolChanged?.Invoke();
            InvalidateVisual();
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

    public void Select(string? id) => SetSelection(id is null ? [] : [id]);

    public void SetSelection(IEnumerable<string> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.SequenceEqual(_selection)) return;
        _selection.Clear();
        _selection.AddRange(list);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Toglie dalla selezione gli oggetti che non esistono più.</summary>
    public void PruneSelection(ICollection<string> valid) => SetSelection(_selection.Where(valid.Contains));

    Point ToDoc(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);

    string? HitTest(Point doc) =>
        Bus?.Read(d => d.Objects.Reverse().FirstOrDefault(o => Contains(o, new((float)doc.X, (float)doc.Y), 5 / _zoom))?.Id);

    // Il punto viene portato nello spazio locale dell'oggetto, così funziona anche con oggetti ruotati e dentro i gruppi.
    static bool Contains(SceneObject o, Vector2 p, double tolerance)
    {
        if (!Matrix3x2.Invert(o.Transform, out var inv)) return false;
        var l = Vector2.Transform(p, inv);
        switch (o)
        {
            case GroupObject g:
                if (!Matrix3x2.Invert(g.Fit, out var fitInv)) return false;
                var c = Vector2.Transform(l, fitInv);
                return g.Children.Any(ch => Contains(ch, c, tolerance));
            case PathObject po:
            {
                using var path = BuildPath(po.Segments, po.Fit);
                if (po.Fill != null && path.Contains(l.X, l.Y)) return true;
                // Il tratto si "clicca" con una tolleranza minima, anche se è sottile o assente.
                using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)(2 * Math.Max(tolerance, po.StrokeWidth / 2)) };
                using var outline = paint.GetFillPath(path);
                return outline != null && outline.Contains(l.X, l.Y);
            }
            case EllipseObject:
            {
                var rx = o.Width / 2; var ry = o.Height / 2;
                var dx = (l.X - (o.X + rx)) / rx; var dy = (l.Y - (o.Y + ry)) / ry;
                return dx * dx + dy * dy <= 1;
            }
            default:
                return l.X >= o.X && l.X <= o.X + o.Width && l.Y >= o.Y && l.Y <= o.Y + o.Height;
        }
    }

    Item? SnapshotSelected() =>
        SelectedId is { } id ? Bus?.Read(d => d.Objects.FirstOrDefault(o => o.Id == id) is { } o ? Item.From(o) : null) : null;

    /// <summary>Maniglia sotto il cursore (tolleranza in pixel schermo), se c'è un solo oggetto selezionato.</summary>
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
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
            _drag = Drag.Pan;
        else if (pt.Properties.IsLeftButtonPressed && Tool == Tool.Pen)
        {
            PenClick(pt.Position, e.ClickCount);
            return;
        }
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
            else if (HitTest(ToDoc(pt.Position)) is { } hit)
            {
                if (shift) // Shift+click: aggiunge o toglie dalla selezione
                    SetSelection(_selection.Contains(hit) ? _selection.Where(i => i != hit) : _selection.Append(hit));
                else
                {
                    if (!_selection.Contains(hit)) SetSelection([hit]);
                    _drag = Drag.Move; // sposta tutta la selezione
                }
            }
            else
            {
                // Sfondo: selezione a rettangolo (Shift per aggiungere).
                if (!shift) SetSelection([]);
                _marqueeAdd = shift;
                _marqueeStart = _marqueeEnd = ToDoc(pt.Position);
                _drag = Drag.Marquee;
            }
        }
        if (_drag != Drag.None) e.Pointer.Capture(this);
    }

    // Penna: click aggiunge un punto; click sul primo punto chiude; doppio click (o Invio) termina il path aperto.
    void PenClick(Point screen, int clickCount)
    {
        if (clickCount >= 2) { FinishPen(false); return; }
        if (_pen.Count >= 2)
        {
            var first = new Point(_pen[0].X * _zoom + _pan.X, _pen[0].Y * _zoom + _pan.Y);
            if (Math.Abs(first.X - screen.X) <= 8 && Math.Abs(first.Y - screen.Y) <= 8) { FinishPen(true); return; }
        }
        _pen.Add(ToDoc(screen));
        InvalidateVisual();
    }

    public bool PenActive => _pen.Count > 0;

    public void CancelPen()
    {
        _pen.Clear();
        InvalidateVisual();
    }

    public void FinishPen(bool closed)
    {
        var pts = _pen.ToList();
        _pen.Clear();
        InvalidateVisual();
        if (pts.Count < 2 || Bus == null) return;

        static string F(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        var d = $"M {F(pts[0].X)} {F(pts[0].Y)} " + string.Join(" ", pts.Skip(1).Select(p => $"L {F(p.X)} {F(p.Y)}")) + (closed ? " Z" : "");
        var r = Bus.Execute([new CreateShape
        {
            Type = "path", D = d,
            Fill = closed ? Fill ?? "none" : "none", // un path aperto non si riempie
            Stroke = Stroke ?? "#1F2937", StrokeWidth = StrokeWidth > 0 ? StrokeWidth : 2,
        }]);
        Select(r.CreatedIds[0]);
        Tool = Tool.Select;
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
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (_drag == Drag.None && Tool == Tool.Pen)
        {
            _penHover = ToDoc(pos);
            if (_pen.Count > 0) InvalidateVisual();
            return;
        }
        if (_drag == Drag.None && Tool == Tool.Select) // nessun gesto: cursore in base a cosa c'è sotto
        {
            Cursor = HitHandle(pos) is var (hh, hit) ? CursorFor(hh, hit) : null;
            return;
        }
        if (_drag == Drag.Pan)
        {
            _pan += pos - _last;
            ViewChanged?.Invoke();
        }
        else if (_drag == Drag.Move)
            _moveOffset = (pos - _start) / _zoom;
        else if (_drag == Drag.Create)
            _createEnd = ToDoc(pos);
        else if (_drag == Drag.Marquee)
            _marqueeEnd = ToDoc(pos);
        else if (_drag == Drag.Resize && _dragItem is { } it)
        {
            if (!Matrix3x2.Invert(it.M, out var inv)) return;
            var d = ToDoc(pos);
            var p = Vector2.Transform(new((float)d.X, (float)d.Y), inv);
            // L'ancora (lato o angolo opposto alla maniglia) resta ferma; la nuova dimensione è la distanza del mouse da essa.
            double ax = 1 - _handleX, ay = 1 - _handleY;
            double nw = _handleX == .5 ? it.W : Math.Max(1, (p.X - (it.X + ax * it.W)) * (_handleX == 1 ? 1 : -1));
            double nh = _handleY == .5 ? it.H : Math.Max(1, (p.Y - (it.Y + ay * it.H)) * (_handleY == 1 ? 1 : -1));
            if (shift && _handleX != .5 && _handleY != .5 && it.W > 0 && it.H > 0) // Shift: mantiene le proporzioni
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
        else if (_drag == Drag.Marquee)
        {
            var m = MarqueeBox();
            if ((m.Width * _zoom >= 3 || m.Height * _zoom >= 3) && Bus != null)
            {
                // Vengono selezionati gli oggetti interamente dentro il rettangolo, come negli editor vettoriali classici.
                var hits = Bus.Read(d => d.Objects.Where(o =>
                {
                    var b = o.Bounds;
                    return b.X >= m.X && b.Y >= m.Y && b.X + b.Width <= m.X + m.Width && b.Y + b.Height <= m.Y + m.Height;
                }).Select(o => o.Id).ToList());
                SetSelection(_marqueeAdd ? _selection.Concat(hits) : hits);
            }
        }
        else if (_drag == Drag.Move && _selection.Count > 0 && (_moveOffset.X != 0 || _moveOffset.Y != 0))
        {
            var (dx, dy) = (_moveOffset.X, _moveOffset.Y);
            var ids = _selection.ToList();
            _drag = Drag.None; // l'anteprima non serve più: il documento sta per cambiare
            Bus?.Execute([new MoveObjects { Targets = ids, Dx = dx, Dy = dy }]); // un drag = un solo undo
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

    static Box BoxOf(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    Box CreateBox() => BoxOf(_createStart, _createEnd);
    Box MarqueeBox() => BoxOf(_marqueeStart, _marqueeEnd);

    // L'oggetto selezionato come apparirà a fine gesto (anteprima).
    Item Preview(Item it) => _drag switch
    {
        Drag.Move => it with { M = it.M * Matrix3x2.CreateTranslation((float)_moveOffset.X, (float)_moveOffset.Y) },
        Drag.Resize when _dragItem?.Id == it.Id => ResizedPreview(it),
        Drag.Rotate when _dragItem?.Id == it.Id =>
            it with { M = it.M * Matrix3x2.CreateRotation((float)(_rotDelta * Math.PI / 180), _rotCenter) },
        _ => it,
    };

    Item ResizedPreview(Item it)
    {
        var b = ResizeObject.Resized(new(it.X, it.Y, it.W, it.H), 1 - _handleX, 1 - _handleY, _newW, _newH);
        return it with { X = b.X, Y = b.Y, W = b.Width, H = b.Height };
    }

    static bool HasId(Item it, string id) => it.Id == id || (it.Children?.Any(c => HasId(c, id)) ?? false);

    public override void Render(DrawingContext context)
    {
        if (Bus == null) return;
        // Senza uno sfondo (anche trasparente) il controllo non riceve gli eventi del mouse.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var items = Bus.Read(d => d.Objects.Select(Item.From).ToList());
        for (var i = 0; i < items.Count; i++)
            if (_selection.Contains(items[i].Id)) items[i] = Preview(items[i]);

        // L'alone di un oggetto modificato dentro un gruppo si mostra sul gruppo.
        var now = Environment.TickCount64;
        var glow = new Dictionary<string, float>();
        foreach (var it in items)
        {
            var a = _highlights.Where(h => HasId(it, h.Key)).Select(h => (float)Math.Max(0, 1 - (now - h.Value) / (double)HighlightMs))
                .DefaultIfEmpty(0).Max();
            if (a > 0) glow[it.Id] = a;
        }

        var gesture = new Gesture(
            _drag == Drag.Create ? CreateBox() : null, Tool == Tool.Ellipse,
            _drag == Drag.Marquee ? MarqueeBox() : null,
            Tool == Tool.Pen && _pen.Count > 0 ? _pen.Select(p => new Vector2((float)p.X, (float)p.Y)).ToList() : null,
            new Vector2((float)_penHover.X, (float)_penHover.Y));
        context.Custom(new SceneOp(new Rect(Bounds.Size), items, new HashSet<string>(_selection), _pan, _zoom,
            ActualThemeVariant == ThemeVariant.Dark, glow, gesture));
    }

    // Gesti in corso da disegnare come anteprima.
    sealed record Gesture(Box? Create, bool CreateEllipse, Box? Marquee, List<Vector2>? Pen, Vector2 PenHover);

    // Copia immutabile degli oggetti: il rendering gira su un altro thread.
    sealed record Item(string Id, Kind Kind, double X, double Y, double W, double H, Matrix3x2 M,
        string? Fill, string? Stroke, double StrokeWidth, Box Source, List<PathSeg>? Segments, List<Item>? Children)
    {
        public Box Bounds => SceneObject.BoundsOf(X, Y, W, H, M);
        public Matrix3x2 Fit => SceneObject.FitMatrix(Source, new(X, Y, W, H));

        public static Item From(SceneObject o) => o switch
        {
            PathObject p => new(o.Id, Kind.Path, o.X, o.Y, o.Width, o.Height, o.Transform, o.Fill, o.Stroke, o.StrokeWidth,
                p.Source, p.Segments, null),
            GroupObject g => new(o.Id, Kind.Group, o.X, o.Y, o.Width, o.Height, o.Transform, o.Fill, o.Stroke, o.StrokeWidth,
                g.Source, null, g.Children.Select(From).ToList()),
            _ => new(o.Id, o is EllipseObject ? Kind.Ellipse : Kind.Rectangle, o.X, o.Y, o.Width, o.Height, o.Transform,
                o.Fill, o.Stroke, o.StrokeWidth, default, null, null),
        };
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

    // Path Skia dai segmenti, già stirati con `fit` (così il tratto resta uniforme).
    static SKPath BuildPath(List<PathSeg> segs, Matrix3x2 fit)
    {
        static SKPoint P(double x, double y, Matrix3x2 m)
        {
            var v = Vector2.Transform(new((float)x, (float)y), m);
            return new(v.X, v.Y);
        }

        var path = new SKPath();
        foreach (var s in segs)
            switch (s.Op)
            {
                case 'M': path.MoveTo(P(s.X, s.Y, fit)); break;
                case 'L': path.LineTo(P(s.X, s.Y, fit)); break;
                case 'C': path.CubicTo(P(s.X1, s.Y1, fit), P(s.X2, s.Y2, fit), P(s.X, s.Y, fit)); break;
                case 'Z': path.Close(); break;
            }
        return path;
    }

    sealed class SceneOp(Rect bounds, List<Item> items, HashSet<string> selection, Point pan, double zoom, bool dark,
        Dictionary<string, float> glow, Gesture gesture)
        : ICustomDrawOperation
    {
        static readonly SKColor Accent = new(0x3B, 0x82, 0xF6);

        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        static SKPoint ToSk(Vector2 v) => new(v.X, v.Y);
        static SKMatrix ToSk(Matrix3x2 m) => new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);

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
            foreach (var it in items) DrawItem(canvas, paint, it);

            DrawGesture(canvas, paint);

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

            // Selezione: un oggetto = contorno orientato e maniglie; più oggetti = contorni e riquadro complessivo.
            var sel = items.Where(i => selection.Contains(i.Id)).ToList();
            if (sel.Count == 1 && gesture.Create is null) DrawSelection(canvas, paint, sel[0]);
            else if (sel.Count > 1)
            {
                foreach (var it in sel) DrawOutline(canvas, paint, it);
                var x0 = sel.Min(i => i.Bounds.X); var y0 = sel.Min(i => i.Bounds.Y);
                var x1 = sel.Max(i => i.Bounds.X + i.Bounds.Width); var y1 = sel.Max(i => i.Bounds.Y + i.Bounds.Height);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)(1 / zoom);
                paint.Color = Accent;
                paint.PathEffect = SKPathEffect.CreateDash([(float)(4 / zoom), (float)(3 / zoom)], 0);
                canvas.DrawRect(SKRect.Create((float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0)), paint);
                paint.PathEffect = null;
            }
            canvas.Restore();
        }

        void DrawItem(SKCanvas canvas, SKPaint paint, Item it)
        {
            canvas.Save();
            var m = ToSk(it.M);
            canvas.Concat(in m);
            if (it.Kind == Kind.Group)
            {
                var fit = ToSk(it.Fit);
                canvas.Concat(in fit);
                foreach (var child in it.Children!) DrawItem(canvas, paint, child);
            }
            else
            {
                if (ParseColor(it.Fill) is { } fill)
                {
                    paint.Style = SKPaintStyle.Fill; paint.Color = fill;
                    DrawShape(canvas, paint, it);
                }
                if (ParseColor(it.Stroke) is { } stroke && it.StrokeWidth > 0)
                {
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = (float)it.StrokeWidth; paint.Color = stroke;
                    DrawShape(canvas, paint, it);
                }
            }
            canvas.Restore();
        }

        static void DrawShape(SKCanvas canvas, SKPaint paint, Item it)
        {
            if (it.Kind == Kind.Path)
            {
                using var path = BuildPath(it.Segments!, it.Fit);
                canvas.DrawPath(path, paint);
                return;
            }
            var r = SKRect.Create((float)it.X, (float)it.Y, (float)it.W, (float)it.H);
            if (it.Kind == Kind.Ellipse) canvas.DrawOval(r, paint); else canvas.DrawRect(r, paint);
        }

        void DrawGesture(SKCanvas canvas, SKPaint paint)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)(1.5 / zoom);
            paint.Color = Accent;

            if (gesture.Create is { } c)
            {
                var r = SKRect.Create((float)c.X, (float)c.Y, (float)c.Width, (float)c.Height);
                if (gesture.CreateEllipse) canvas.DrawOval(r, paint); else canvas.DrawRect(r, paint);
            }
            if (gesture.Marquee is { } mq)
            {
                var r = SKRect.Create((float)mq.X, (float)mq.Y, (float)mq.Width, (float)mq.Height);
                paint.Style = SKPaintStyle.Fill; paint.Color = Accent.WithAlpha(30);
                canvas.DrawRect(r, paint);
                paint.Style = SKPaintStyle.Stroke; paint.Color = Accent;
                paint.StrokeWidth = (float)(1 / zoom);
                canvas.DrawRect(r, paint);
            }
            if (gesture.Pen is { } pen)
            {
                using var line = new SKPath();
                line.MoveTo(ToSk(pen[0]));
                foreach (var p in pen.Skip(1)) line.LineTo(ToSk(p));
                line.LineTo(ToSk(gesture.PenHover)); // segmento "elastico" verso il cursore
                canvas.DrawPath(line, paint);

                var size = (float)(8 / zoom);
                for (var i = 0; i < pen.Count; i++)
                {
                    var p = ToSk(pen[i]);
                    paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White;
                    if (i == 0 && pen.Count >= 2) canvas.DrawCircle(p, size * 0.8f, paint); else canvas.DrawRect(SKRect.Create(p.X - size / 2, p.Y - size / 2, size, size), paint);
                    paint.Style = SKPaintStyle.Stroke; paint.Color = Accent;
                    if (i == 0 && pen.Count >= 2) canvas.DrawCircle(p, size * 0.8f, paint); else canvas.DrawRect(SKRect.Create(p.X - size / 2, p.Y - size / 2, size, size), paint);
                }
            }
        }

        // Contorno orientato del rettangolo dell'oggetto.
        void DrawOutline(SKCanvas canvas, SKPaint paint, Item it)
        {
            using var outline = new SKPath();
            outline.MoveTo(ToSk(PointOn(it, 0, 0)));
            outline.LineTo(ToSk(PointOn(it, 1, 0)));
            outline.LineTo(ToSk(PointOn(it, 1, 1)));
            outline.LineTo(ToSk(PointOn(it, 0, 1)));
            outline.Close();
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)(1.5 / zoom);
            paint.Color = Accent;
            canvas.DrawPath(outline, paint);
        }

        void DrawSelection(SKCanvas canvas, SKPaint paint, Item sel)
        {
            DrawOutline(canvas, paint, sel);

            var size = (float)(8 / zoom);
            foreach (var h in HandlesOf(sel, zoom))
            {
                var c = ToSk(h.Pos);
                if (h.Rotate)
                {
                    paint.Style = SKPaintStyle.Stroke; paint.Color = Accent;
                    canvas.DrawLine(ToSk(PointOn(sel, .5, 0)), c, paint);
                }
                paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White;
                if (h.Rotate) canvas.DrawCircle(c, size * 0.65f, paint);
                else canvas.DrawRect(SKRect.Create(c.X - size / 2, c.Y - size / 2, size, size), paint);
                paint.Style = SKPaintStyle.Stroke; paint.Color = Accent;
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
