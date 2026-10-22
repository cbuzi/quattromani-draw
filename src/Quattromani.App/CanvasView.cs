using System.Globalization;
using System.Numerics;
using Quattromani.Core;
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
using GradientStop = Quattromani.Core.GradientStop;

namespace Quattromani.App;

public enum Tool { Select, Node, Rectangle, Ellipse, Polygon, Star, Line, Pen, Pencil, Text, Fill, Eyedropper }

/// <summary>How the page is shown in the work area.</summary>
public enum PageView { White, Outline, Hidden }

/// <summary>Request to edit a text: an existing object (Id) or a new text at a point, with optional wrapping width.</summary>
public sealed record TextEditRequest(string? Id, Point At, double? WrapWidth);

public enum NodeOp { ToLine, ToCurve, Cusp, Smooth, Symmetric, Delete }

/// <summary>
/// Skia canvas with pan/zoom, selection (also multiple), move, resize, rotation and pen.
/// Changes go only through the CommandBus.
/// </summary>
public sealed class CanvasView : Control
{
    enum Drag { None, Pan, Move, Create, Pencil, Resize, Rotate, Marquee, PenNode, NodeMove, NodeHandle, TextBox, FillHandle, FillDraw, GuideNew, GuideMove }
    enum FillPart { Start, End, Stop, Center, RadX, RadY }
    enum NodePart { Node, In, Out }

    Point _pan = new(120, 90); // screen position of the document origin
    double _zoom = 1;
    Drag _drag;
    Point _last, _start;
    Avalonia.Vector _moveOffset; // move in progress, in document coordinates (preview)
    Point _createStart, _createEnd; // rectangle being created, in document coordinates
    Point _marqueeStart, _marqueeEnd; // rectangle selection, in document coordinates
    bool _marqueeAdd;
    Tool _tool;

    readonly List<string> _selection = [];
    /// <summary>Pen node (document coordinates); In/Out are the curve handles, absent if the node is a corner.</summary>
    sealed class PenNode(Point p)
    {
        public Point P = p;
        public Point? In, Out;
    }

    readonly List<PenNode> _pen = [];

    // Node tool: selection of the nodes (subpath, index) of the selected path; during a drag
    // _nodeOrig is the model at the start of the gesture and _nodeWork the edited one (preview: the document changes on release).
    // Fill tool: _fillWork is the gradient being edited during a drag (preview; the document
    // changes on release), _fillBase the one at the start of the gesture; _fillSel is the selected color stop.
    GradientSpec? _fillWork, _fillBase;
    FillHandle? _fillHit;
    Item? _fillItem;
    Point _fillStart;
    int _fillSel = -1;

    /// <summary>Color stops a new gradient starts with when dragging over the object (provided by the panel).</summary>
    public Func<List<GradientStop>>? DefaultStops { get; set; }
    public int FillSelectedStop => _fillSel;

    readonly HashSet<(int Sub, int Idx)> _nodeSel = [];
    List<SubPath>? _nodeOrig, _nodeWork;
    (int Sub, int Idx, NodePart Part) _nodeHit;
    double _nodeHandleX, _nodeHandleY;
    Point _penHover;

    // Resize / rotation in progress (preview: the document changes only on release).
    Item? _dragItem;
    double _handleX, _handleY; // dragged handle, as fractions of the rectangle
    double _newW, _newH;
    Vector2 _rotCenter;
    double _startAngle, _rotDelta;

    // Grid, guides and snapping (guides live in the document; the rest is view state).
    const double RulerSize = 20;
    bool _showGrid;
    public bool ShowGrid { get => _showGrid; set { _showGrid = value; InvalidateVisual(); } }
    /// <summary>Grid step in document px.</summary>
    public double GridSize { get; set; } = 10;
    public bool SnapOn { get; set; } = true;
    public bool SnapToGrid { get; set; } = true;
    public bool SnapToGuides { get; set; } = true;
    public bool SnapToObjects { get; set; } = true;
    readonly List<double> _snapXs = [], _snapYs = []; // guides, edges and centers of the other objects and of the page
    double? _snapLineX, _snapLineY; // targets snapped to by the current gesture, shown as lines
    Box _moveBounds;
    string _guideAxis = "x";
    double _guidePos, _guideFrom;
    bool _guideOut; // the guide is over a ruler: on release it is deleted

    const long HighlightMs = 3000;
    readonly Dictionary<string, (long Stamp, string Agent)> _highlights = []; // object id -> TickCount64 and agent of the change
    readonly DispatcherTimer _fade;

    // One pill per agent, on the last object it touched: it stays still, then fades.
    const long PillHoldMs = 1500, PillFadeMs = 500;
    const int MaxPills = 4;
    readonly Dictionary<string, (string Id, long Stamp)> _pills = [];
    readonly Dictionary<string, SKColor> _agentColors = []; // color assigned to the agent at its first intervention
    static readonly SKColor[] AgentPalette =
        [new(0xA8, 0x55, 0xF7), new(0x14, 0xB8, 0xA6), new(0xF9, 0x73, 0x16), new(0xEC, 0x48, 0x99), new(0x84, 0xCC, 0x16)];

    SKColor ColorOf(string agent)
    {
        if (!_agentColors.TryGetValue(agent, out var c))
            _agentColors[agent] = c = AgentPalette[_agentColors.Count % AgentPalette.Length];
        return c;
    }

    public bool ShowAgentPill { get => _showPill; set { _showPill = value; InvalidateVisual(); } }
    bool _showPill = true;

    public CommandBus? Bus { get; set; }
    public IReadOnlyList<string> SelectedIds => _selection;
    /// <summary>The selected id, only if the selection is exactly one object.</summary>
    public string? SelectedId => _selection.Count == 1 ? _selection[0] : null;
    public double Zoom => _zoom;
    public int NodeSelectionCount => _nodeSel.Count;

    PageView _pageView = PageView.Outline;
    /// <summary>White page with shadow, outline only, or hidden.</summary>
    public PageView PageViewMode { get => _pageView; set { _pageView = value; InvalidateVisual(); } }

    /// <summary>Measures shown to the user: number in the chosen unit (provided by the window) and the unit's name.</summary>
    public Func<double, string> FormatLength { get; set; } = v => v.ToString("0.##");
    public string UnitLabel { get; set; } = "px";

    /// <summary>Informational text during a gesture (size, position, angle); null = end of the gesture.</summary>
    public event Action<string?>? InfoChanged;
    /// <summary>Cursor position in document coordinates; null when it leaves the canvas.</summary>
    public event Action<Point?>? CursorMoved;

    /// <summary>Box of the page in document coordinates.</summary>
    Box PageBox() => Bus?.Read(d => new Box(0, 0, d.Pages[0].Width, d.Pages[0].Height)) ?? new Box(0, 0, 794, 1123);

    /// <summary>Frames a box of the document, centered and with a margin in pixels.</summary>
    public void ZoomToBox(Box b, double margin = 48)
    {
        if (Bounds.Width < 10 || Bounds.Height < 10 || b.Width <= 0 || b.Height <= 0) return;
        _zoom = Math.Clamp(Math.Min((Bounds.Width - 2 * margin) / b.Width, (Bounds.Height - 2 * margin) / b.Height), 0.02, 64);
        _pan = new((Bounds.Width - b.Width * _zoom) / 2 - b.X * _zoom, (Bounds.Height - b.Height * _zoom) / 2 - b.Y * _zoom);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void FitPage() => ZoomToBox(PageBox());

    /// <summary>Zoom to 100%, keeping the window's center fixed.</summary>
    public void ZoomActualSize()
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var doc = ToDoc(c);
        _zoom = 1;
        _pan = new(c.X - doc.X, c.Y - doc.Y);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    void RaiseInfo(string? text) => InfoChanged?.Invoke(text);

    string Size(Box b) => $"{FormatLength(b.Width)} × {FormatLength(b.Height)} {UnitLabel}";

    /// <summary>Object not to be drawn (the text being edited in place).</summary>
    string? _hiddenId;
    public string? HiddenId { get => _hiddenId; set { _hiddenId = value; InvalidateVisual(); } }

    public event Action<TextEditRequest>? TextEditRequested;

    public Point DocToScreen(Point p) => new(p.X * _zoom + _pan.X, p.Y * _zoom + _pan.Y);

    /// <summary>Where to show a text's editor: top-left corner on screen and rotation in degrees.</summary>
    public (Point TopLeft, double Rotation)? EditorPlacement(string id)
    {
        var it = Bus?.Read(d => d.Objects.FirstOrDefault(o => o.Id == id) is { } o ? Item.From(o) : null);
        if (it == null) return null;
        var p = PointOn(it, 0, 0);
        return (DocToScreen(new Point(p.X, p.Y)), Math.Atan2(it.M.M12, it.M.M11) * 180 / Math.PI);
    }

    bool IsText(string id) => Bus?.Read(d => d.Objects.FirstOrDefault(o => o.Id == id) is TextObject) ?? false;

    /// <summary>Edits the selected text (Enter / F2).</summary>
    public void EditSelectedText()
    {
        if (SelectedId is { } id && IsText(id)) TextEditRequested?.Invoke(new TextEditRequest(id, default, null));
    }

    /// <summary>Stroke style and opacity of new shapes (the values SetStyle/CreateShape take; "solid" = no dash).</summary>
    public string NewDash { get; set; } = "solid";
    public string NewCap { get; set; } = "butt";
    public string NewJoin { get; set; } = "miter";
    public double NewOpacity { get; set; } = 1;

    CreateShape WithNewStyle(CreateShape shape)
    {
        shape.Dash = NewDash == "solid" ? null : NewDash;
        shape.Cap = NewCap == "butt" ? null : NewCap;
        shape.Join = NewJoin == "miter" ? null : NewJoin;
        shape.Opacity = NewOpacity >= 1 ? null : NewOpacity;
        return shape;
    }

    /// <summary>Options of the Polygon and Star tools.</summary>
    public int PolygonSides { get; set; } = 5;
    public int StarPoints { get; set; } = 5;
    public double StarInner { get; set; } = 0.5;

    /// <summary>The Eyedropper picked a color (fill or stroke of the clicked object; "none" if it has none).</summary>
    public event Action<string, bool>? ColorPicked;

    readonly List<Point> _pencil = []; // points of the freehand stroke in progress, in document coordinates

    /// <summary>Style applied to new shapes.</summary>
    public string? Fill { get; set; } = "#3B82F6";
    public string? Stroke { get; set; } = "#1F2937";
    public double StrokeWidth { get; set; } = 2;

    public Tool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            if (_tool == Tool.Pen && _pen.Count >= 2) FinishPen(false, false); // when switching tool the open path is kept
            _tool = value;
            if (value != Tool.Pen) _pen.Clear();
            _nodeSel.Clear();
            _nodeWork = null;
            _fillWork = null;
            _fillSel = -1;
            Cursor = value is Tool.Select or Tool.Node ? null : new Cursor(value == Tool.Text ? StandardCursorType.Ibeam : StandardCursorType.Cross);
            ToolChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// <summary>A user command was rejected (for example because the object is locked): the text explains why.</summary>
    public event Action<string>? Notice;

    // User gestures go through here: a rejected command becomes a notice, not an exception in the mouse handler.
    BatchResult? Try(params Command[] commands)
    {
        try { return Bus?.Execute(commands); }
        catch (CommandException ex) { Notice?.Invoke(ex.Message); return null; }
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

    /// <summary>Highlights for a few seconds the objects created or modified by the agent.</summary>
    public void Highlight(IEnumerable<string> ids, string agent)
    {
        var now = Environment.TickCount64;
        foreach (var id in ids)
        {
            _highlights[id] = (now, agent);
            _pills[agent] = (id, now);
        }
        if (_highlights.Count > 0) _fade.Start();
        InvalidateVisual();
    }

    void FadeTick()
    {
        var now = Environment.TickCount64;
        foreach (var id in _highlights.Where(h => now - h.Value.Stamp > HighlightMs).Select(h => h.Key).ToList())
            _highlights.Remove(id);
        if (_highlights.Count == 0) _fade.Stop();
        InvalidateVisual();
    }

    // ---- Scroll bars ------------------------------------------------------------------

    Box _scrollRange; // scrollable area in document coordinates, from the last ScrollInfo

    /// <summary>
    /// Measures for the scroll bars, in screen pixels. The scrollable area is the content enlarged by half a window
    /// per side (one can always go a bit past the edges), united with the window itself if the user went further.
    /// </summary>
    public (double TotalW, double TotalH, double OffX, double OffY, double ViewW, double ViewH) ScrollInfo()
    {
        var boxes = Bus?.Read(d => d.RenderObjects.Select(o => o.Bounds).ToList()) ?? [];
        var page = PageBox(); // the scrollable area always includes the page
        double cx0 = page.X, cy0 = page.Y, cx1 = page.X + page.Width, cy1 = page.Y + page.Height;
        if (boxes.Count > 0)
        {
            cx0 = Math.Min(cx0, boxes.Min(b => b.X)); cy0 = Math.Min(cy0, boxes.Min(b => b.Y));
            cx1 = Math.Max(cx1, boxes.Max(b => b.X + b.Width)); cy1 = Math.Max(cy1, boxes.Max(b => b.Y + b.Height));
        }
        double vw = Bounds.Width / _zoom, vh = Bounds.Height / _zoom, vx = -_pan.X / _zoom, vy = -_pan.Y / _zoom;
        double x0 = Math.Min(cx0 - vw / 2, vx), y0 = Math.Min(cy0 - vh / 2, vy);
        double x1 = Math.Max(cx1 + vw / 2, vx + vw), y1 = Math.Max(cy1 + vh / 2, vy + vh);
        _scrollRange = new(x0, y0, x1 - x0, y1 - y0);
        return ((x1 - x0) * _zoom, (y1 - y0) * _zoom, (vx - x0) * _zoom, (vy - y0) * _zoom, Bounds.Width, Bounds.Height);
    }

    /// <summary>Moves the window to a position (in pixels from the start of the scrollable area); null = unchanged.</summary>
    public void ScrollTo(double? offX, double? offY)
    {
        if (offX is { } x) _pan = new(-(_scrollRange.X * _zoom + x), _pan.Y);
        if (offY is { } y) _pan = new(_pan.X, -(_scrollRange.Y * _zoom + y));
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void Select(string? id) => SetSelection(id is null ? [] : [id]);

    public void SetSelection(IEnumerable<string> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.SequenceEqual(_selection)) return;
        _selection.Clear();
        _selection.AddRange(list);
        _nodeSel.Clear();
        _fillSel = -1;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Removes from the selection the objects that no longer exist.</summary>
    public void PruneSelection(ICollection<string> valid) => SetSelection(_selection.Where(valid.Contains));

    /// <summary>Collects the snap targets for the gesture that starts (objects in <paramref name="exclude"/> do not count: they are moving).</summary>
    void BeginSnap(IReadOnlyCollection<string>? exclude = null)
    {
        _snapXs.Clear(); _snapYs.Clear();
        _snapLineX = _snapLineY = null;
        if (!SnapOn || Bus == null) return;
        Bus.Read(d =>
        {
            var page = d.Pages[0];
            if (SnapToGuides)
                foreach (var g in page.Guides) (g.Axis == "x" ? _snapXs : _snapYs).Add(g.Position);
            if (SnapToObjects)
            {
                _snapXs.AddRange([0, page.Width / 2, page.Width]);
                _snapYs.AddRange([0, page.Height / 2, page.Height]);
                foreach (var o in d.RenderObjects)
                {
                    if (exclude?.Contains(o.Id) == true) continue;
                    var b = d.PageBounds(o);
                    _snapXs.AddRange([b.X, b.X + b.Width / 2, b.X + b.Width]);
                    _snapYs.AddRange([b.Y, b.Y + b.Height / 2, b.Y + b.Height]);
                }
            }
            return 0;
        });
    }

    /// <summary>Correction to add to <paramref name="v"/> to snap it to the nearest target (within 6 screen px), and the target's coordinate.</summary>
    (double Delta, double Line)? SnapAxis(double v, List<double> targets)
    {
        if (!SnapOn) return null;
        var tol = 6 / _zoom;
        (double Delta, double Line)? best = null;
        void Try(double t)
        {
            var d = t - v;
            if (Math.Abs(d) <= tol && (best is null || Math.Abs(d) < Math.Abs(best.Value.Delta))) best = (d, t);
        }
        foreach (var t in targets) Try(t);
        if (SnapToGrid && _showGrid && GridSize > 0) Try(Math.Round(v / GridSize) * GridSize);
        return best;
    }

    /// <summary>Snaps a point (x and y independent) and remembers the targets found to show them.</summary>
    Point SnapPoint(Point p)
    {
        var sx = SnapAxis(p.X, _snapXs);
        var sy = SnapAxis(p.Y, _snapYs);
        _snapLineX = sx?.Line; _snapLineY = sy?.Line;
        return new(p.X + (sx?.Delta ?? 0), p.Y + (sy?.Delta ?? 0));
    }

    void BeginMove()
    {
        var ids = _selection.ToList();
        _moveBounds = Bus?.Read(d =>
        {
            var boxes = ids.Select(id => d.Objects.FirstOrDefault(o => o.Id == id)).OfType<SceneObject>().Select(d.PageBounds).ToList();
            if (boxes.Count == 0) return default;
            var x0 = boxes.Min(b => b.X); var y0 = boxes.Min(b => b.Y);
            return new Box(x0, y0, boxes.Max(b => b.X + b.Width) - x0, boxes.Max(b => b.Y + b.Height) - y0);
        }) ?? default;
        BeginSnap(ids);
    }

    // Move: the selection's box snaps left/center/right edge and top/center/bottom.
    void SnapMove()
    {
        _snapLineX = _snapLineY = null;
        if (!SnapOn) return;
        var b = _moveBounds; var o = _moveOffset;
        (double Delta, double Line)? Best(params (double, double)?[] c) => c.Where(x => x != null).Select(x => x!.Value).OrderBy(x => Math.Abs(x.Item1)).Cast<(double, double)?>().FirstOrDefault();
        var sx = Best(SnapAxis(b.X + o.X, _snapXs), SnapAxis(b.X + b.Width / 2 + o.X, _snapXs), SnapAxis(b.X + b.Width + o.X, _snapXs));
        var sy = Best(SnapAxis(b.Y + o.Y, _snapYs), SnapAxis(b.Y + b.Height / 2 + o.Y, _snapYs), SnapAxis(b.Y + b.Height + o.Y, _snapYs));
        _snapLineX = sx?.Line; _snapLineY = sy?.Line;
        _moveOffset = new(o.X + (sx?.Delta ?? 0), o.Y + (sy?.Delta ?? 0));
    }

    // ---- Rulers and guides ----

    bool InRuler(Point p) => p.X < RulerSize || p.Y < RulerSize;

    Guide? HitGuide(Point screen) =>
        Bus?.Read(d => d.Pages[0].Guides.FirstOrDefault(g =>
            Math.Abs(g.Axis == "x" ? g.Position * _zoom + _pan.X - screen.X : g.Position * _zoom + _pan.Y - screen.Y) <= 4));

    // Guides are pulled out of the rulers; with the Select tool existing ones are moved.
    bool StartGuideDrag(Point pos)
    {
        if (InRuler(pos))
        {
            if (pos.X < RulerSize && pos.Y < RulerSize) return true; // corner: nothing
            _guideAxis = pos.Y < RulerSize ? "y" : "x"; // a horizontal guide from the top ruler
            _drag = Drag.GuideNew;
            BeginSnap();
            GuideDragMove(pos);
            return true;
        }
        if (Tool == Tool.Select && HitHandle(pos) == null && HitGuide(pos) is { } g)
        {
            (_guideAxis, _guideFrom) = (g.Axis, g.Position);
            _drag = Drag.GuideMove;
            BeginSnap();
            GuideDragMove(pos);
            return true;
        }
        return false;
    }

    void GuideDragMove(Point pos)
    {
        var d = ToDoc(pos);
        var v = _guideAxis == "x" ? d.X : d.Y;
        _guidePos = v + (SnapAxis(v, _guideAxis == "x" ? _snapXs : _snapYs)?.Delta ?? 0);
        _guideOut = InRuler(pos);
    }

    /// <summary>Document point under a screen point, and center of the view.</summary>
    public Point DocPoint(Point screen) => ToDoc(screen);
    public Point ViewCenter => ToDoc(new(Bounds.Width / 2, Bounds.Height / 2));

    Point ToDoc(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);

    string? HitTest(Point doc) =>
        Bus?.Read(d => d.PickableObjects.Reverse().FirstOrDefault(o => Contains(o, new((float)doc.X, (float)doc.Y), 5 / _zoom))?.Id);

    // The point is brought into the object's local space, so it also works with rotated objects and inside groups.
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
                using var path = SceneRenderer.BuildPath(po.Segments, po.Fit);
                if (po.Fill != null && path.Contains(l.X, l.Y)) return true;
                // The stroke is "clickable" with a minimum tolerance, even if it is thin or absent.
                using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)(2 * Math.Max(tolerance, po.StrokeWidth / 2)) };
                using var outline = paint.GetFillPath(path);
                return outline != null && outline.Contains(l.X, l.Y);
            }
            case TextObject { PathGlyphs: { } glyphs } t: // text on a path: one clicks on the letters and between one letter and the next
            {
                using var path = SceneRenderer.BuildPath(glyphs, t.Fit);
                using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)(2 * Math.Max(tolerance, t.FontSize * 0.3)) };
                using var outline = paint.GetFillPath(path);
                return path.Contains(l.X, l.Y) || (outline != null && outline.Contains(l.X, l.Y));
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

    /// <summary>Handle under the cursor (tolerance in screen pixels), if a single object is selected.</summary>
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

    // Fill and stroke of an object, or of the first shape inside it if it is a group.
    static (string? Fill, string? Stroke)? FirstStyled(SceneObject o) =>
        o is GroupObject g ? g.Children.Reverse<SceneObject>().Select(FirstStyled).FirstOrDefault(s => s != null) : (o.Fill, o.Stroke);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var pt = e.GetCurrentPoint(this);
        _last = _start = pt.Position;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (pt.Properties.IsLeftButtonPressed && StartGuideDrag(pt.Position))
        {
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }
        if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
            _drag = Drag.Pan;
        else if (pt.Properties.IsLeftButtonPressed && Tool == Tool.Pen)
        {
            PenPress(pt.Position, e.ClickCount);
            if (_drag != Drag.None) e.Pointer.Capture(this);
            return;
        }
        else if (pt.Properties.IsLeftButtonPressed && Tool == Tool.Text)
        {
            if (HitTest(ToDoc(pt.Position)) is { } textHit && IsText(textHit))
            {
                Select(textHit); // click on an existing text: edit it
                TextEditRequested?.Invoke(new TextEditRequest(textHit, default, null));
                return;
            }
            // Click = "artistic" text at that point; dragging draws the width for automatic wrapping.
            BeginSnap();
            _createStart = _createEnd = SnapPoint(ToDoc(pt.Position));
            _drag = Drag.TextBox;
        }
        else if (pt.Properties.IsLeftButtonPressed && Tool == Tool.Fill)
        {
            FillPress(pt.Position, e.ClickCount);
            if (_drag != Drag.None) e.Pointer.Capture(this);
            return;
        }
        else if (pt.Properties.IsLeftButtonPressed && Tool == Tool.Node)
        {
            NodePress(pt.Position, e.ClickCount, shift);
            if (_drag != Drag.None) e.Pointer.Capture(this);
            return;
        }
        else if (Tool == Tool.Eyedropper && (pt.Properties.IsLeftButtonPressed || pt.Properties.IsRightButtonPressed))
        {
            // Left click takes the fill, right click (or Alt) the stroke.
            var stroke = pt.Properties.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if (HitTest(ToDoc(pt.Position)) is { } hit)
            {
                var colors = Bus?.Read(d => d.AllObjects.FirstOrDefault(o => o.Id == hit) is { } o ? FirstStyled(o) : null);
                if (colors is { } c) ColorPicked?.Invoke((stroke ? c.Stroke ?? c.Fill : c.Fill ?? c.Stroke) ?? "none", stroke);
            }
            return;
        }
        else if (pt.Properties.IsLeftButtonPressed && Tool == Tool.Pencil)
        {
            _pencil.Clear();
            _pencil.Add(ToDoc(pt.Position));
            _drag = Drag.Pencil;
        }
        else if (pt.Properties.IsLeftButtonPressed && Tool != Tool.Select)
        {
            BeginSnap();
            _createStart = _createEnd = SnapPoint(ToDoc(pt.Position));
            _drag = Drag.Create;
        }
        else if (pt.Properties.IsLeftButtonPressed)
        {
            if (HitHandle(pt.Position) is var (h, it))
            {
                _dragItem = it;
                _newW = it.W; _newH = it.H;
                BeginSnap([it.Id]);
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
                if (e.ClickCount >= 2 && !shift && IsText(hit)) // double-click on a text: edit
                {
                    Select(hit);
                    TextEditRequested?.Invoke(new TextEditRequest(hit, default, null));
                    return;
                }
                if (shift) // Shift+click: adds to or removes from the selection
                    SetSelection(_selection.Contains(hit) ? _selection.Where(i => i != hit) : _selection.Append(hit));
                else
                {
                    if (!_selection.Contains(hit)) SetSelection([hit]);
                    _drag = Drag.Move; // moves the whole selection
                    BeginMove();
                }
            }
            else
            {
                // Background: rectangle selection (Shift to add).
                if (!shift) SetSelection([]);
                _marqueeAdd = shift;
                _marqueeStart = _marqueeEnd = ToDoc(pt.Position);
                _drag = Drag.Marquee;
            }
        }
        if (_drag != Drag.None) e.Pointer.Capture(this);
    }

    // Pen (like the Bézier tool of classic vector editors): click = corner node; click and drag = smooth node, with symmetric
    // curve handles; click on the first node closes; double-click or Enter ends the open path.
    void PenPress(Point screen, int clickCount)
    {
        if (clickCount >= 2) { FinishPen(false); return; }
        if (_pen.Count >= 2 && NearFirstNode(screen)) { FinishPen(true); return; }
        BeginSnap();
        _pen.Add(new PenNode(SnapPoint(ToDoc(screen))));
        _drag = Drag.PenNode; // dragging pulls out the handles
        InvalidateVisual();
    }

    bool NearFirstNode(Point screen)
    {
        var f = _pen[0].P;
        return Math.Abs(f.X * _zoom + _pan.X - screen.X) <= 8 && Math.Abs(f.Y * _zoom + _pan.Y - screen.Y) <= 8;
    }

    public bool PenActive => _pen.Count > 0;

    public void CancelPen()
    {
        _pen.Clear();
        InvalidateVisual();
    }

    /// <summary>Removes the last node.</summary>
    public void PenBack()
    {
        if (_pen.Count > 0) _pen.RemoveAt(_pen.Count - 1);
        InvalidateVisual();
    }

    public void FinishPen(bool closed, bool select = true)
    {
        var nodes = _pen.ToList();
        _pen.Clear();
        InvalidateVisual();
        if (nodes.Count < 2 || Bus == null) return;

        static string F(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        static string Pt(Point p) => $"{F(p.X)} {F(p.Y)}";
        var sb = new System.Text.StringBuilder("M " + Pt(nodes[0].P));
        void Segment(PenNode a, PenNode b)
        {
            if (a.Out is null && b.In is null) sb.Append($" L {Pt(b.P)}");
            else sb.Append($" C {Pt(a.Out ?? a.P)} {Pt(b.In ?? b.P)} {Pt(b.P)}");
        }
        for (var i = 1; i < nodes.Count; i++) Segment(nodes[i - 1], nodes[i]);
        if (closed)
        {
            if (nodes[^1].Out != null || nodes[0].In != null) Segment(nodes[^1], nodes[0]); // curve closing
            sb.Append(" Z");
        }

        if (Try(WithNewStyle(new CreateShape
        {
            Type = "path", D = sb.ToString(),
            Fill = closed ? Fill ?? "none" : "none", // an open path is not filled
            Stroke = Stroke ?? "#1F2937", StrokeWidth = StrokeWidth > 0 ? StrokeWidth : 2,
        })) is { } r) Select(r.CreatedIds[0]);
        if (select) Tool = Tool.Select;
    }

    // ---- Fill tool -------------------------------------------------------------

    sealed record FillHandle(FillPart Part, int Index, Vector2 Pos, string Color);
    sealed record FillView(List<FillHandle> Handles, List<(Vector2 A, Vector2 B)> Lines, List<Vector2>? Ellipse, int Selected);

    // The gradient is relative to this box, in the object's local space (for text: the laid-out one, then stretched by Fit).
    static Box GradBox(Item it) => it.Kind == Kind.Text ? it.Source : new Box(it.X, it.Y, it.W, it.H);

    static Vector2 FracToPage(Item it, double fx, double fy)
    {
        var b = GradBox(it);
        var p = new Vector2((float)(b.X + fx * b.Width), (float)(b.Y + fy * b.Height));
        if (it.Kind == Kind.Text) p = Vector2.Transform(p, it.Fit);
        return Vector2.Transform(p, it.M);
    }

    static (double X, double Y)? PageToFrac(Item it, Vector2 page)
    {
        if (!Matrix3x2.Invert(it.M, out var inv)) return null;
        var p = Vector2.Transform(page, inv);
        if (it.Kind == Kind.Text)
        {
            if (!Matrix3x2.Invert(it.Fit, out var fitInv)) return null;
            p = Vector2.Transform(p, fitInv);
        }
        var b = GradBox(it);
        return b.Width > 0 && b.Height > 0 ? ((p.X - b.X) / b.Width, (p.Y - b.Y) / b.Height) : null;
    }

    static GradientSpec? ParseFill(string? fill) => fill != null && Gradient.TryParse(fill, out var g) ? g : null;

    FillView BuildFillView(Item it, GradientSpec g)
    {
        var box = GradBox(it);
        var handles = new List<FillHandle>();
        var lines = new List<(Vector2, Vector2)>();
        List<Vector2>? ellipse = null;
        var last = g.Stops.Count - 1;

        if (!g.Radial)
        {
            var (x1, y1, x2, y2) = Gradient.LinearPoints(g, box);
            Vector2 a = FracToPage(it, x1, y1), b = FracToPage(it, x2, y2);
            lines.Add((a, b));
            handles.Add(new(FillPart.Start, 0, a, g.Stops[0].Color));
            handles.Add(new(FillPart.End, last, b, g.Stops[last].Color));
            for (var i = 1; i < last; i++)
            {
                var t = g.Stops[i].Position;
                handles.Add(new(FillPart.Stop, i, FracToPage(it, x1 + (x2 - x1) * t, y1 + (y2 - y1) * t), g.Stops[i].Color));
            }
        }
        else
        {
            var (rx, ry) = Gradient.RadialRadii(g, box);
            double cx = g.CenterX, cy = g.CenterY;
            Vector2 c = FracToPage(it, cx, cy), hx = FracToPage(it, cx + rx, cy), hy = FracToPage(it, cx, cy + ry);
            lines.Add((c, hx));
            lines.Add((c, hy));
            handles.Add(new(FillPart.Center, 0, c, g.Stops[0].Color));
            handles.Add(new(FillPart.RadX, last, hx, g.Stops[last].Color));
            handles.Add(new(FillPart.RadY, last, hy, g.Stops[last].Color));
            for (var i = 1; i < last; i++)
                handles.Add(new(FillPart.Stop, i, FracToPage(it, cx + rx * g.Stops[i].Position, cy), g.Stops[i].Color));
            ellipse = Enumerable.Range(0, 49).Select(k => FracToPage(it, cx + rx * Math.Cos(k * Math.PI / 24), cy + ry * Math.Sin(k * Math.PI / 24))).ToList();
        }
        return new FillView(handles, lines, ellipse, _fillSel);
    }

    FillHandle? FillHitHandle(Point screen)
    {
        if (SnapshotSelected() is not { } it || it.Kind == Kind.Group || ParseFill(it.Fill) is not { } g) return null;
        return BuildFillView(it, g).Handles.Where(h => Near(h.Pos, screen)).OrderBy(h => h.Part == FillPart.Stop ? 0 : 1).FirstOrDefault();
    }

    void FillPress(Point screen, int clickCount)
    {
        var it = SnapshotSelected();
        var g = it is { Kind: not Kind.Group } ? ParseFill(it.Fill) : null;
        if (it != null && g != null)
        {
            if (FillHitHandle(screen) is { } h)
            {
                _fillItem = it; _fillBase = g; _fillHit = h;
                _fillSel = h.Part is FillPart.Start or FillPart.End or FillPart.Stop or FillPart.Center or FillPart.RadX or FillPart.RadY ? h.Index : -1;
                _drag = Drag.FillHandle;
                InvalidateVisual();
                return;
            }
            if (clickCount >= 2 && AddStopAt(it, g, screen)) return;
        }

        var hit = HitTest(ToDoc(screen));
        if (hit != null && hit != SelectedId) { Select(hit); return; } // another object: we work on that one
        if (it != null && it.Kind != Kind.Group && hit == it.Id)
        {
            // Dragging over the object draws a new gradient (like the interactive fill tool of classic vector editors).
            _fillItem = it; _fillBase = g; _fillHit = null;
            _fillStart = ToDoc(screen);
            _drag = Drag.FillDraw;
        }
    }

    // Double-click on the gradient line: adds a color stop at that point.
    bool AddStopAt(Item it, GradientSpec g, Point screen)
    {
        var box = GradBox(it);
        var d = ToDoc(screen);
        if (PageToFrac(it, new Vector2((float)d.X, (float)d.Y)) is not { } f) return false;
        double ax, ay, bx, by; // line the stops lie on, as fractions of the box
        if (!g.Radial)
        {
            var (x1, y1, x2, y2) = Gradient.LinearPoints(g, box);
            (ax, ay, bx, by) = (x1, y1, x2, y2);
        }
        else
        {
            var (rx, _) = Gradient.RadialRadii(g, box);
            (ax, ay, bx, by) = (g.CenterX, g.CenterY, g.CenterX + rx, g.CenterY);
        }
        double w = box.Width, h = box.Height, dx = (bx - ax) * w, dy = (by - ay) * h, len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return false;
        var t = ((f.X - ax) * w * dx + (f.Y - ay) * h * dy) / len2;
        if (t <= 0.02 || t >= 0.98) return false;
        var px = ax + (bx - ax) * t; var py = ay + (by - ay) * t;
        var onLine = FracToPage(it, px, py);
        if (!Near(onLine, screen) && Vector2.Distance(onLine, new Vector2((float)d.X, (float)d.Y)) * _zoom > 6) return false;

        var stops = g.Stops.ToList();
        var index = stops.FindIndex(s => s.Position > t);
        if (index < 0) index = stops.Count - 1;
        stops.Insert(index, new GradientStop(Gradient.ColorAt(g.Stops, t), t));
        _fillSel = index;
        CommitFill(Gradient.Format(g with { Stops = stops }));
        return true;
    }

    void FillDragMove(Point pos, bool shift)
    {
        if (_fillItem is not { } it) return;
        var g0 = _fillBase; // null if the object did not have a gradient yet (the first one is being drawn)
        var box = GradBox(it);
        var d = ToDoc(pos);
        if (PageToFrac(it, new Vector2((float)d.X, (float)d.Y)) is not { } f) return;
        double w = box.Width, h = box.Height;

        if (_drag == Drag.FillDraw)
        {
            if (PageToFrac(it, new Vector2((float)_fillStart.X, (float)_fillStart.Y)) is not { } s) return;
            if (Math.Abs(pos.X - (_fillStart.X * _zoom + _pan.X)) + Math.Abs(pos.Y - (_fillStart.Y * _zoom + _pan.Y)) < 3) { _fillWork = null; return; }
            var stops = (_fillBase?.Stops ?? DefaultStops?.Invoke() ?? [new("#3B82F6", 0), new("#FFFFFF", 1)]).ToList();
            if (_fillBase is { Radial: true } rb)
            {
                var r = Math.Sqrt(Math.Pow((f.X - s.X) * w, 2) + Math.Pow((f.Y - s.Y) * h, 2)); // radius in document units
                _fillWork = rb with { CenterX = s.X, CenterY = s.Y, Circle = false, Rx = r / w, Ry = r / h, Stops = stops };
            }
            else
                _fillWork = new GradientSpec(false, 0, false, 0.5, 0.5, stops, s.X, s.Y, f.X, f.Y);
            return;
        }

        if (g0 == null || _fillHit == null) return;
        var g = g0;
        var hit = _fillHit;
        var st = g.Stops.ToList();
        var last = st.Count - 1;
        if (!g.Radial)
        {
            var (x1, y1, x2, y2) = Gradient.LinearPoints(g, box);
            switch (hit.Part)
            {
                case FillPart.Start:
                case FillPart.End:
                {
                    var start = hit.Part == FillPart.Start;
                    var (ox, oy) = start ? (x2, y2) : (x1, y1);
                    var (nx, ny) = (f.X, f.Y);
                    if (shift && w > 0 && h > 0) // Shift: angle in steps of 15°
                    {
                        double vx = (nx - ox) * w, vy = (ny - oy) * h, len = Math.Sqrt(vx * vx + vy * vy);
                        var ang = Math.Round(Math.Atan2(vy, vx) / (Math.PI / 12)) * (Math.PI / 12);
                        (nx, ny) = (ox + len * Math.Cos(ang) / w, oy + len * Math.Sin(ang) / h);
                    }
                    _fillWork = start ? g with { X1 = nx, Y1 = ny, X2 = x2, Y2 = y2 } : g with { X1 = x1, Y1 = y1, X2 = nx, Y2 = ny };
                    break;
                }
                case FillPart.Stop:
                {
                    double ax = (x2 - x1) * w, ay = (y2 - y1) * h, len2 = ax * ax + ay * ay;
                    if (len2 < 1e-9) return;
                    var t = ((f.X - x1) * w * ax + (f.Y - y1) * h * ay) / len2;
                    st[hit.Index] = st[hit.Index] with { Position = Math.Clamp(t, st[hit.Index - 1].Position + 0.001, st[hit.Index + 1].Position - 0.001) };
                    _fillWork = g with { Stops = st };
                    break;
                }
            }
        }
        else
        {
            var (rx, ry) = Gradient.RadialRadii(g, box);
            switch (hit.Part)
            {
                case FillPart.Center:
                    _fillWork = g with { CenterX = f.X, CenterY = f.Y, Circle = false, Rx = rx, Ry = ry }; // the radius stays the same
                    break;
                case FillPart.RadX:
                    _fillWork = g with { Circle = false, Rx = Math.Max(0.005, Math.Abs(f.X - g.CenterX)), Ry = ry };
                    break;
                case FillPart.RadY:
                    _fillWork = g with { Circle = false, Rx = rx, Ry = Math.Max(0.005, Math.Abs(f.Y - g.CenterY)) };
                    break;
                case FillPart.Stop:
                {
                    var t = rx > 1e-9 ? (f.X - g.CenterX) / rx : 0;
                    st[hit.Index] = st[hit.Index] with { Position = Math.Clamp(t, st[hit.Index - 1].Position + 0.001, st[hit.Index + 1].Position - 0.001) };
                    _fillWork = g with { Stops = st };
                    break;
                }
            }
        }
    }

    void CommitFill(string fill)
    {
        if (SelectedId is { } id && Bus != null)
            try { Bus.Execute([new SetStyle { Targets = [id], Fill = fill }]); } catch (CommandException) { }
    }

    /// <summary>With the stop selected on the canvas, a palette color changes it. False if no stop is selected.</summary>
    public bool TrySetSelectedStopColor(string color)
    {
        if (Tool != Tool.Fill || _fillSel < 0 || SnapshotSelected() is not { } it || ParseFill(it.Fill) is not { } g || _fillSel >= g.Stops.Count) return false;
        var st = g.Stops.ToList();
        st[_fillSel] = st[_fillSel] with { Color = color.ToUpperInvariant() };
        CommitFill(Gradient.Format(g with { Stops = st }));
        return true;
    }

    /// <summary>Removes the selected stop (at least two are needed).</summary>
    public bool DeleteSelectedStop()
    {
        if (Tool != Tool.Fill || _fillSel < 0 || SnapshotSelected() is not { } it || ParseFill(it.Fill) is not { } g || g.Stops.Count <= 2 || _fillSel >= g.Stops.Count) return false;
        var st = g.Stops.ToList();
        st.RemoveAt(_fillSel);
        _fillSel = -1;
        CommitFill(Gradient.Format(g with { Stops = st }));
        return true;
    }

    // ---- Node tool ------------------------------------------------------------------------

    // Node model of the path in page coordinates (the transform is already applied).
    static List<SubPath> ModelOf(Item it) => PathNodes.FromSegments(PathData.Transform(it.Segments!, it.Fit * it.M), it.NodeKinds);

    List<SubPath>? CurrentModel() =>
        Tool == Tool.Node && SnapshotSelected() is { Kind: Kind.Path } it ? ModelOf(it) : null;

    bool Near(Vector2 v, Point screen) =>
        Math.Abs(v.X * _zoom + _pan.X - screen.X) <= 7 && Math.Abs(v.Y * _zoom + _pan.Y - screen.Y) <= 7;

    (int Sub, int Idx, NodePart Part)? HitNodePart(List<SubPath> model, Point screen)
    {
        // The handles of the selected nodes sit above the nodes.
        foreach (var (si, ni) in _nodeSel)
        {
            if (si >= model.Count || ni >= model[si].Nodes.Count) continue;
            var n = model[si].Nodes[ni];
            if (n.In is { } i && Near(i, screen)) return (si, ni, NodePart.In);
            if (n.Out is { } o && Near(o, screen)) return (si, ni, NodePart.Out);
        }
        for (var si = 0; si < model.Count; si++)
            for (var ni = 0; ni < model[si].Nodes.Count; ni++)
                if (Near(model[si].Nodes[ni].P, screen)) return (si, ni, NodePart.Node);
        return null;
    }

    void NodePress(Point screen, int clickCount, bool shift)
    {
        var model = CurrentModel();
        if (model != null)
        {
            if (HitNodePart(model, screen) is { } hit)
            {
                if (hit.Part == NodePart.Node)
                {
                    if (clickCount >= 2) // double-click on a node: deletes it
                    {
                        _nodeSel.Clear(); _nodeSel.Add((hit.Sub, hit.Idx));
                        ApplyNodeOp(NodeOp.Delete);
                        return;
                    }
                    if (shift) { if (!_nodeSel.Remove((hit.Sub, hit.Idx))) _nodeSel.Add((hit.Sub, hit.Idx)); }
                    else if (!_nodeSel.Contains((hit.Sub, hit.Idx))) { _nodeSel.Clear(); _nodeSel.Add((hit.Sub, hit.Idx)); }
                }
                _nodeOrig = PathNodes.Clone(model);
                _nodeHit = hit;
                _drag = hit.Part == NodePart.Node ? Drag.NodeMove : Drag.NodeHandle;
                InvalidateVisual();
                return;
            }

            // Double-click on the path: adds a node without changing the shape.
            var d = ToDoc(screen);
            if (clickCount >= 2 && PathNodes.Nearest(model, new((float)d.X, (float)d.Y)) is { } near && near.Dist * _zoom <= 6)
            {
                var m = PathNodes.Clone(model);
                var idx = PathNodes.InsertNode(m[near.Sub], near.Seg, near.T);
                CommitNodes(m);
                _nodeSel.Clear(); _nodeSel.Add((near.Sub, idx));
                InvalidateVisual();
                return;
            }
        }

        if (HitTest(ToDoc(screen)) is { } obj)
        {
            if (obj != SelectedId) Select(obj); // another object: show its nodes
            return;
        }
        if (!shift) _nodeSel.Clear();
        _marqueeAdd = shift;
        _marqueeStart = _marqueeEnd = ToDoc(screen);
        _drag = Drag.Marquee;
    }

    // Preview of the drag of nodes or handles, starting from the model at the start of the gesture.
    void NodeDragMove(Point pos, bool alt)
    {
        if (_nodeOrig == null) return;
        if (Math.Abs(pos.X - _start.X) + Math.Abs(pos.Y - _start.Y) < 3) { _nodeWork = null; return; }
        var work = PathNodes.Clone(_nodeOrig);
        var delta = new Vector2((float)((pos.X - _start.X) / _zoom), (float)((pos.Y - _start.Y) / _zoom));
        if (_drag == Drag.NodeMove)
        {
            foreach (var (si, ni) in _nodeSel)
            {
                if (si >= work.Count || ni >= work[si].Nodes.Count) continue;
                var n = work[si].Nodes[ni];
                n.P += delta;
                if (n.In is { } i) n.In = i + delta;
                if (n.Out is { } o) n.Out = o + delta;
            }
        }
        else
        {
            var d = ToDoc(pos);
            var n = work[_nodeHit.Sub].Nodes[_nodeHit.Idx];
            PathNodes.MoveHandle(n, _nodeHit.Part == NodePart.Out, new((float)d.X, (float)d.Y), alt); // Alt: independent handles
        }
        _nodeWork = work;
    }

    void CommitNodes(List<SubPath> subs)
    {
        if (SelectedId is not { } id || Bus == null) return;
        try
        {
            Try(new SetPath { Target = id, D = PathData.ToString(PathNodes.ToSegments(subs), 4), Kinds = PathNodes.KindsOf(subs) });
        }
        catch (CommandException) { }
    }

    /// <summary>Applies an operation to the selected nodes of the path (a single undo).</summary>
    public void ApplyNodeOp(NodeOp op)
    {
        if (CurrentModel() is not { } model || _nodeSel.Count == 0) return;
        var m = PathNodes.Clone(model);
        switch (op)
        {
            case NodeOp.ToLine: PathNodes.SegmentsToLine(m, _nodeSel); break;
            case NodeOp.ToCurve: PathNodes.SegmentsToCurve(m, _nodeSel); break;
            case NodeOp.Cusp: PathNodes.SetKind(m, _nodeSel, NodeKind.Cusp); break;
            case NodeOp.Smooth: PathNodes.SetKind(m, _nodeSel, NodeKind.Smooth); break;
            case NodeOp.Symmetric: PathNodes.SetKind(m, _nodeSel, NodeKind.Symmetric); break;
            case NodeOp.Delete:
                foreach (var g in _nodeSel.GroupBy(n => n.Sub))
                    foreach (var idx in g.Select(n => n.Idx).OrderDescending())
                        if (g.Key < m.Count && idx < m[g.Key].Nodes.Count) PathNodes.RemoveNode(m[g.Key], idx);
                _nodeSel.Clear();
                break;
        }
        CommitNodes(m);
        InvalidateVisual();
    }

    // Avalonia's standard cursors: it translates them for Windows, macOS and Linux.
    static readonly Cursor SizeWE = new(StandardCursorType.SizeWestEast), SizeNS = new(StandardCursorType.SizeNorthSouth),
        SizeNWSE = new(StandardCursorType.TopLeftCorner), SizeNESW = new(StandardCursorType.TopRightCorner),
        RotateCursor = new(StandardCursorType.Hand);

    // Resize cursor based on the handle's real direction (also for rotated objects).
    static Cursor CursorFor(Handle h, Item it)
    {
        if (h.Rotate) return RotateCursor;
        var v = h.Pos - PointOn(it, .5, .5);
        var deg = Math.Atan2(v.Y, v.X) * 180 / Math.PI; // 0 = right, 90 = down
        var sector = (int)Math.Round((deg % 180 + 180) % 180 / 45) % 4; // 0=horiz., 1=diag. \, 2=vert., 3=diag. /
        return sector switch
        {
            0 => SizeWE, 1 => SizeNWSE, 2 => SizeNS, _ => SizeNESW,
        };
    }

    protected override void OnPointerExited(PointerEventArgs e) => CursorMoved?.Invoke(null);

    // Live measures during a gesture: size while drawing or resizing, position while moving.
    void UpdateLiveInfo()
    {
        switch (_drag)
        {
            case Drag.Create or Drag.TextBox: RaiseInfo(Size(CreateBox())); break;
            case Drag.Resize when _dragItem is { } it: RaiseInfo(Size(ResizedPreview(it).Bounds)); break;
            case Drag.Rotate: RaiseInfo($"{_rotDelta:0.#}°"); break;
            case Drag.Move when SnapshotSelected() is { } mv:
                var b = mv.Bounds;
                RaiseInfo($"{Size(b)}   ·   x {FormatLength(b.X + _moveOffset.X)}   y {FormatLength(b.Y + _moveOffset.Y)} {UnitLabel}");
                break;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        CursorMoved?.Invoke(ToDoc(pos));
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (_drag == Drag.None && Tool == Tool.Pen)
        {
            _penHover = SnapPoint(ToDoc(pos));
            if (_pen.Count > 0) InvalidateVisual();
            return;
        }
        if (_drag is Drag.GuideNew or Drag.GuideMove)
        {
            GuideDragMove(pos);
            InvalidateVisual();
            return;
        }
        if (_drag == Drag.PenNode)
        {
            var n = _pen[^1];
            var d = ToDoc(pos);
            _penHover = d;
            if (Math.Abs(d.X - n.P.X) * _zoom + Math.Abs(d.Y - n.P.Y) * _zoom > 3)
            {
                n.Out = d; // the handle follows the mouse, the other is its mirror with respect to the node
                n.In = new Point(2 * n.P.X - d.X, 2 * n.P.Y - d.Y);
            }
            else n.In = n.Out = null;
            InvalidateVisual();
            return;
        }
        if (_drag == Drag.None && Tool == Tool.Fill)
        {
            Cursor = FillHitHandle(pos) != null ? new Cursor(StandardCursorType.SizeAll) : null;
            return;
        }
        if (_drag is Drag.FillHandle or Drag.FillDraw)
        {
            FillDragMove(pos, shift);
            InvalidateVisual();
            return;
        }
        if (_drag == Drag.None && Tool == Tool.Node)
        {
            Cursor = CurrentModel() is { } hm && HitNodePart(hm, pos) != null ? new Cursor(StandardCursorType.SizeAll) : null;
            return;
        }
        if (_drag is Drag.NodeMove or Drag.NodeHandle)
        {
            NodeDragMove(pos, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            InvalidateVisual();
            return;
        }
        if (_drag == Drag.None && Tool == Tool.Select) // no gesture: cursor based on what is underneath
        {
            Cursor = HitHandle(pos) is var (hh, hit) ? CursorFor(hh, hit)
                : HitGuide(pos) is { } hg ? new Cursor(hg.Axis == "x" ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth) : null;
            return;
        }
        if (_drag == Drag.Pan)
        {
            _pan += pos - _last;
            ViewChanged?.Invoke();
        }
        else if (_drag == Drag.Move)
        {
            _moveOffset = (pos - _start) / _zoom;
            SnapMove();
        }
        else if (_drag is Drag.Create or Drag.TextBox)
            _createEnd = ConstrainCreate(SnapPoint(ToDoc(pos)), shift);
        else if (_drag == Drag.Pencil)
        {
            var d = ToDoc(pos);
            var last = _pencil[^1];
            if ((Math.Abs(d.X - last.X) + Math.Abs(d.Y - last.Y)) * _zoom >= 2) _pencil.Add(d);
        }
        else if (_drag == Drag.Marquee)
            _marqueeEnd = ToDoc(pos);
        else if (_drag == Drag.Resize && _dragItem is { } it)
        {
            if (!Matrix3x2.Invert(it.M, out var inv)) return;
            var d = ToDoc(pos);
            _snapLineX = _snapLineY = null;
            if (Math.Abs(it.M.M12) < 1e-4 && Math.Abs(it.M.M21) < 1e-4) // snapping applies only to non-rotated objects
            {
                if (_handleX != .5 && SnapAxis(d.X, _snapXs) is { } sx) { d = new(d.X + sx.Delta, d.Y); _snapLineX = sx.Line; }
                if (_handleY != .5 && SnapAxis(d.Y, _snapYs) is { } sy) { d = new(d.X, d.Y + sy.Delta); _snapLineY = sy.Line; }
            }
            var p = Vector2.Transform(new((float)d.X, (float)d.Y), inv);
            // The anchor (side or corner opposite the handle) stays fixed; the new size is the mouse's distance from it.
            double ax = 1 - _handleX, ay = 1 - _handleY;
            double nw = _handleX == .5 ? it.W : Math.Max(1, (p.X - (it.X + ax * it.W)) * (_handleX == 1 ? 1 : -1));
            double nh = _handleY == .5 ? it.H : Math.Max(1, (p.Y - (it.Y + ay * it.H)) * (_handleY == 1 ? 1 : -1));
            if (shift && _handleX != .5 && _handleY != .5 && it.W > 0 && it.H > 0) // Shift: keeps the proportions
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
            _rotDelta = shift ? Math.Round(delta / 15) * 15 : delta; // Shift: steps of 15°
        }
        else
            return;
        _last = pos;
        UpdateLiveInfo();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_drag is Drag.GuideNew or Drag.GuideMove)
        {
            var (axis, pos, from, isNew, del) = (_guideAxis, Math.Round(_guidePos, 3), _guideFrom, _drag == Drag.GuideNew, _guideOut);
            _drag = Drag.None;
            e.Pointer.Capture(null);
            try
            {
                if (isNew) { if (!del) Bus?.Execute([new AddGuide { Axis = axis, Position = pos }]); }
                else if (del) Bus?.Execute([new DeleteGuide { Axis = axis, Position = from }]);
                else if (Math.Abs(pos - from) > 1e-6) Bus?.Execute([new MoveGuide { Axis = axis, From = from, To = pos }]);
            }
            catch (CommandException) { } // for example a guide already present at the same point
            InvalidateVisual();
            return;
        }
        if (_drag == Drag.PenNode)
        {
            _drag = Drag.None;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }
        if (_drag is Drag.FillHandle or Drag.FillDraw)
        {
            var work = _fillWork;
            _fillWork = null; _fillBase = null; _drag = Drag.None;
            e.Pointer.Capture(null);
            if (work != null) CommitFill(Gradient.Format(work));
            InvalidateVisual();
            return;
        }
        if (_drag is Drag.NodeMove or Drag.NodeHandle)
        {
            var work = _nodeWork;
            _nodeWork = null; _nodeOrig = null; _drag = Drag.None;
            e.Pointer.Capture(null);
            if (work != null) CommitNodes(work);
            InvalidateVisual();
            return;
        }
        if (_drag == Drag.Create)
        {
            var b = CreateBox();
            var big = Tool == Tool.Line ? Math.Max(b.Width, b.Height) * _zoom >= 3 : b.Width * _zoom >= 3 && b.Height * _zoom >= 3;
            if (big && Bus != null)
            {
                var shape = new CreateShape
                {
                    Type = Tool switch { Tool.Ellipse => "ellipse", Tool.Polygon => "polygon", Tool.Star => "star", Tool.Line => "line", _ => "rectangle" },
                    X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
                    Fill = Fill ?? "none", Stroke = Stroke ?? "none", StrokeWidth = StrokeWidth,
                };
                if (Tool == Tool.Polygon) shape.Sides = PolygonSides;
                if (Tool == Tool.Star) (shape.Points, shape.InnerRatio) = (StarPoints, StarInner);
                if (Tool == Tool.Line) // from where the mouse went down to where it is now (the box loses the direction)
                    (shape.X, shape.Y, shape.Width, shape.Height, shape.Fill, shape.Stroke, shape.StrokeWidth) =
                        (_createStart.X, _createStart.Y, _createEnd.X - _createStart.X, _createEnd.Y - _createStart.Y, null, Stroke ?? "#1F2937", StrokeWidth > 0 ? StrokeWidth : 2);
                if (Try(WithNewStyle(shape)) is { } r) Select(r.CreatedIds[0]);
                Tool = Tool.Select; // after drawing we go back to selection
            }
        }
        else if (_drag == Drag.Pencil)
        {
            var pts = _pencil.ToList();
            _pencil.Clear();
            _drag = Drag.None;
            var length = pts.Zip(pts.Skip(1), (a, b) => Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y)).Sum();
            if (pts.Count >= 2 && length * _zoom >= 4 && Bus != null)
            {
                // Curves through the points with an error of about 2 screen pixels; the tool stays active for the next stroke.
                var segs = ImageTrace.FitOpen(pts.Select(p => (p.X, p.Y)).ToList(), 2 / _zoom);
                if (segs.Count > 1 && Try(WithNewStyle(new CreateShape
                {
                    Type = "path", D = PathData.ToString(segs, 3), Fill = "none",
                    Stroke = Stroke ?? "#1F2937", StrokeWidth = StrokeWidth > 0 ? StrokeWidth : 2,
                })) is { } r) Select(r.CreatedIds[0]);
            }
        }
        else if (_drag == Drag.TextBox)
        {
            var b = CreateBox();
            _drag = Drag.None;
            TextEditRequested?.Invoke(b.Width * _zoom >= 12
                ? new TextEditRequest(null, new Point(b.X, b.Y), b.Width)
                : new TextEditRequest(null, _createStart, null));
        }
        else if (_drag == Drag.Marquee && Tool == Tool.Node)
        {
            // Rectangle selection of the path's nodes.
            var m = MarqueeBox();
            if (CurrentModel() is { } model && (m.Width * _zoom >= 3 || m.Height * _zoom >= 3))
            {
                if (!_marqueeAdd) _nodeSel.Clear();
                for (var si = 0; si < model.Count; si++)
                    for (var ni = 0; ni < model[si].Nodes.Count; ni++)
                    {
                        var p = model[si].Nodes[ni].P;
                        if (p.X >= m.X && p.X <= m.X + m.Width && p.Y >= m.Y && p.Y <= m.Y + m.Height) _nodeSel.Add((si, ni));
                    }
            }
        }
        else if (_drag == Drag.Marquee)
        {
            var m = MarqueeBox();
            if ((m.Width * _zoom >= 3 || m.Height * _zoom >= 3) && Bus != null)
            {
                // Objects entirely inside the rectangle are selected, as in classic vector editors.
                var hits = Bus.Read(d => d.PickableObjects.Where(o =>
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
            _drag = Drag.None; // the preview is no longer needed: the document is about to change
            Try(new MoveObjects { Targets = ids, Dx = dx, Dy = dy }); // one drag = one undo
        }
        else if (_drag == Drag.Resize && _dragItem is { } it)
        {
            _drag = Drag.None;
            Try(new ResizeObject
            {
                Targets = [it.Id], Width = _newW, Height = _newH, Anchor = Anchor.NameOf(1 - _handleX, 1 - _handleY),
            });
        }
        else if (_drag == Drag.Rotate && _dragItem is { } rit && Math.Abs(_rotDelta) > 0.01)
        {
            _drag = Drag.None;
            Try(new RotateObjects { Targets = [rit.Id], Degrees = _rotDelta });
        }
        _moveOffset = default;
        _snapLineX = _snapLineY = null;
        _dragItem = null;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        RaiseInfo(null);
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

    // Shift: a line snaps to 15° steps; the other shapes become square.
    Point ConstrainCreate(Point end, bool shift)
    {
        if (!shift || _drag != Drag.Create) return end;
        double dx = end.X - _createStart.X, dy = end.Y - _createStart.Y;
        if (Tool == Tool.Line)
        {
            var step = Math.PI / 12;
            var angle = Math.Round(Math.Atan2(dy, dx) / step) * step;
            var len = Math.Sqrt(dx * dx + dy * dy);
            return new(_createStart.X + len * Math.Cos(angle), _createStart.Y + len * Math.Sin(angle));
        }
        var m = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new(_createStart.X + (dx < 0 ? -m : m), _createStart.Y + (dy < 0 ? -m : m));
    }

    // The outline of what the Line, Polygon and Star tools would create now; null with the other tools.
    List<PathSeg>? CreatePreview()
    {
        if (_drag == Drag.Pencil) return _pencil.Count < 2 ? null : _pencil.Select((p, i) => new PathSeg(i == 0 ? 'M' : 'L', X: p.X, Y: p.Y)).ToList();
        if (_drag != Drag.Create) return null;
        var b = CreateBox();
        return Tool switch
        {
            Tool.Line => [new('M', X: _createStart.X, Y: _createStart.Y), new('L', X: _createEnd.X, Y: _createEnd.Y)],
            Tool.Polygon when b.Width > 0 && b.Height > 0 => PathData.PolygonSegments(b.X, b.Y, b.Width, b.Height, PolygonSides),
            Tool.Star when b.Width > 0 && b.Height > 0 => PathData.PolygonSegments(b.X, b.Y, b.Width, b.Height, StarPoints, StarInner),
            _ => null,
        };
    }
    Box MarqueeBox() => BoxOf(_marqueeStart, _marqueeEnd);

    // The selected object as it will appear at the end of the gesture (preview).
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
        // Without a background (even transparent) the control does not receive mouse events.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var items = Bus.Read(d => d.RenderObjects.Select(Item.From).ToList());
        if (_hiddenId != null) items.RemoveAll(i => i.Id == _hiddenId);
        for (var i = 0; i < items.Count; i++)
            if (_selection.Contains(items[i].Id)) items[i] = Preview(items[i]);

        // Node tool: model to show (the one being edited, if any) and preview of the edited path.
        List<SubPath>? nodeModel = null;
        if (Tool == Tool.Node && SelectedId != null)
        {
            if (_nodeWork != null) nodeModel = _nodeWork;
            else if (items.FirstOrDefault(i => i.Id == SelectedId) is { Kind: Kind.Path } pi) nodeModel = ModelOf(pi);
            if (_nodeWork != null)
            {
                var segs = PathNodes.ToSegments(_nodeWork);
                var box = PathData.Bounds(segs)!.Value;
                var k = items.FindIndex(i => i.Id == SelectedId);
                if (k >= 0)
                    items[k] = items[k] with { Segments = segs, Source = box, X = box.X, Y = box.Y, W = box.Width, H = box.Height, M = Matrix3x2.Identity };
            }
        }

        // Fill tool: preview of the gradient being edited and handles.
        FillView? fillView = null;
        if (Tool == Tool.Fill && SelectedId != null && items.FindIndex(i => i.Id == SelectedId) is var fk and >= 0 && items[fk].Kind != Kind.Group)
        {
            var spec = _fillWork ?? ParseFill(items[fk].Fill);
            if (_fillWork != null) items[fk] = items[fk] with { Fill = Gradient.Format(_fillWork) };
            if (spec != null) fillView = BuildFillView(items[fk], spec);
        }

        // The halo of a modified object inside a group is shown on the group.
        var now = Environment.TickCount64;
        var glow = new Dictionary<string, (float Alpha, SKColor Color)>();
        foreach (var it in items)
        {
            var best = _highlights.Where(h => HasId(it, h.Key))
                .Select(h => (Alpha: (float)Math.Max(0, 1 - (now - h.Value.Stamp) / (double)HighlightMs), h.Value.Agent))
                .OrderByDescending(h => h.Alpha).FirstOrDefault();
            if (best.Alpha > 0) glow[it.Id] = (best.Alpha, ColorOf(best.Agent));
        }

        // Pills: at most MaxPills, the most recent; on the same object they stack.
        var pills = new List<AgentPill>();
        if (_showPill)
        {
            var slots = new Dictionary<string, int>();
            foreach (var (agent, (id, stamp)) in _pills.OrderByDescending(p => p.Value.Stamp).Take(MaxPills).ToList())
            {
                var age = now - stamp;
                if (age >= PillHoldMs + PillFadeMs || items.FirstOrDefault(i => HasId(i, id)) is not { } target) continue;
                slots[target.Id] = slots.GetValueOrDefault(target.Id);
                pills.Add(new(agent, target.Bounds, (float)Math.Min(1, 1 - (age - PillHoldMs) / (double)PillFadeMs),
                    ColorOf(agent), slots[target.Id]++));
            }
        }

        var createPath = CreatePreview();
        var gesture = new Gesture(
            _drag is Drag.Create or Drag.TextBox && createPath == null ? CreateBox() : null, Tool == Tool.Ellipse,
            _drag == Drag.Marquee ? MarqueeBox() : null,
            Tool == Tool.Pen && _pen.Count > 0
                ? _pen.Select(n => new PenNodeView(V(n.P), n.In is { } i ? V(i) : null, n.Out is { } o ? V(o) : null)).ToList()
                : null,
            V(_penHover),
            Tool == Tool.Node,
            nodeModel?.SelectMany((sp, si) => sp.Nodes.Select((n, ni) => new NodeV(n.P, n.In, n.Out, _nodeSel.Contains((si, ni)), n.Kind))).ToList(),
            Tool == Tool.Pen && _pen.Count >= 2 && _drag != Drag.PenNode &&
                NearFirstNode(new Point(_penHover.X * _zoom + _pan.X, _penHover.Y * _zoom + _pan.Y)),
            Tool == Tool.Fill, fillView, createPath);
        var guides = Bus.Read(d => d.Pages[0].Guides.ToList());
        if (_drag is Drag.GuideNew or Drag.GuideMove)
        {
            if (_drag == Drag.GuideMove) guides.RemoveAll(g => g.Is(_guideAxis, _guideFrom));
            if (!_guideOut) guides.Add(new Guide(_guideAxis, _guidePos));
        }
        var overlay = new Overlay(guides, _showGrid ? GridSize : 0, _snapLineX, _snapLineY, UnitLabel);
        context.Custom(new SceneOp(new Rect(Bounds.Size), items, new HashSet<string>(_selection), _pan, _zoom,
            ActualThemeVariant == ThemeVariant.Dark, glow, gesture, PageBox(), _pageView, overlay, pills));
    }

    // Gestures in progress to draw as a preview.
    static Vector2 V(Point p) => new((float)p.X, (float)p.Y);

    /// <summary>Grid (step in px, 0 = hidden), guides, snap lines in progress and ruler unit.</summary>
    sealed record Overlay(List<Guide> Guides, double Grid, double? SnapX, double? SnapY, string Unit);

    /// <summary>Agent label: name, object box (document coordinates) and opacity.</summary>
    sealed record AgentPill(string Name, Box Bounds, float Alpha, SKColor Color, int Slot);

    sealed record PenNodeView(Vector2 P, Vector2? In, Vector2? Out);

    sealed record NodeV(Vector2 P, Vector2? In, Vector2? Out, bool Selected, NodeKind Kind);

    sealed record Gesture(Box? Create, bool CreateEllipse, Box? Marquee, List<PenNodeView>? Pen, Vector2 PenHover,
        bool NodeMode, List<NodeV>? Nodes, bool PenClosing, bool FillMode, FillView? FillOverlay, List<PathSeg>? CreatePath);

    /// <summary>Selection handle; Hx/Hy are fractions of the local rectangle, Pos is in document coordinates.</summary>
    sealed record Handle(double Hx, double Hy, bool Rotate, Vector2 Pos);

    static Vector2 PointOn(Item it, double fx, double fy) =>
        Vector2.Transform(new((float)(it.X + fx * it.W), (float)(it.Y + fy * it.H)), it.M);

    static List<Handle> HandlesOf(Item it, double zoom)
    {
        var list = new List<Handle>();
        foreach (var (_, hx, hy) in Anchor.All)
            if (!(hx == .5 && hy == .5)) list.Add(new(hx, hy, false, PointOn(it, hx, hy)));

        // Rotation handle: above the top side, at a fixed distance in screen pixels.
        var top = PointOn(it, .5, 0);
        var dir = top - PointOn(it, .5, .5);
        dir = dir.LengthSquared() < 1e-6f ? new(0, -1) : Vector2.Normalize(dir);
        list.Add(new(.5, 0, true, top + dir * (float)(28 / zoom)));
        return list;
    }

    sealed class SceneOp(Rect bounds, List<Item> items, HashSet<string> selection, Point pan, double zoom, bool dark,
        Dictionary<string, (float Alpha, SKColor Color)> glow, Gesture gesture, Box page, PageView pageView, Overlay overlay, List<AgentPill> pills)
        : ICustomDrawOperation
    {
        static readonly SKColor Accent = new(0x3B, 0x82, 0xF6);

        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        static SKPoint ToSk(Vector2 v) => new(v.X, v.Y);

        // Fixed-size pill in screen pixels, above the object's top-right corner (kept inside the viewport).
        void DrawPill(SKCanvas canvas, SKPaint paint, AgentPill pill)
        {
            var z = (float)zoom;
            using var font = new SKFont(SKTypeface.Default, 11);
            var w = font.MeasureText(pill.Name) + 26;
            const float h = 20;
            var x = Math.Clamp((float)(pill.Bounds.X + pill.Bounds.Width) * z + (float)pan.X - w + 8, 4, (float)bounds.Width - w - 4);
            var y = Math.Clamp((float)pill.Bounds.Y * z + (float)pan.Y - h - 6 - pill.Slot * (h + 3), 4, (float)bounds.Height - h - 4);
            var a = pill.Alpha;

            canvas.Save();
            canvas.Scale(1 / z); // back to screen coordinates
            canvas.Translate(-(float)pan.X, -(float)pan.Y);
            var r = new SKRect(x, y, x + w, y + h);
            paint.Style = SKPaintStyle.Fill;
            paint.Color = new SKColor(0, 0, 0, (byte)(40 * a));
            canvas.DrawRoundRect(r with { Top = r.Top + 1.5f, Bottom = r.Bottom + 1.5f }, h / 2, h / 2, paint);
            paint.Color = pill.Color.WithAlpha((byte)(255 * a));
            canvas.DrawRoundRect(r, h / 2, h / 2, paint);
            paint.Color = new SKColor(255, 255, 255, (byte)(255 * a));
            canvas.DrawCircle(x + 11, y + h / 2, 3, paint);
            canvas.DrawText(pill.Name, x + 19, y + h / 2 + 4, SKTextAlign.Left, font, paint);
            canvas.Restore();
        }

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
            DrawPage(canvas, paint);
            DrawGrid(canvas, paint);
            foreach (var it in items) SceneRenderer.DrawItem(canvas, paint, it);
            DrawGuides(canvas, paint);

            DrawGesture(canvas, paint);

            // Objects touched by agents: halo in the agent's color that fades.
            foreach (var it in items)
            {
                if (!glow.TryGetValue(it.Id, out var g)) continue;
                var (a, color) = g;
                var pad = 4 / zoom;
                var r = SKRect.Create((float)(it.Bounds.X - pad), (float)(it.Bounds.Y - pad),
                    (float)(it.Bounds.Width + 2 * pad), (float)(it.Bounds.Height + 2 * pad));
                paint.Style = SKPaintStyle.Fill;
                paint.Color = color.WithAlpha((byte)(50 * a));
                canvas.DrawRect(r, paint);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)(2.5 / zoom);
                paint.Color = color.WithAlpha((byte)(255 * a));
                canvas.DrawRect(r, paint);
            }

            foreach (var pill in pills) DrawPill(canvas, paint, pill);

            // Selection: one object = oriented outline and handles; several objects = outlines and overall box.
            var sel = items.Where(i => selection.Contains(i.Id)).ToList();
            if (gesture.FillMode)
            {
                foreach (var it in sel) DrawOutline(canvas, paint, it); // fill tool: outline + gradient handles
                if (gesture.FillOverlay is { } fo) DrawFill(canvas, paint, fo);
            }
            else if (gesture.NodeMode)
            {
                // Node tool: no transform handles; for a path its nodes are shown.
                if (gesture.Nodes != null) DrawNodes(canvas, paint, gesture.Nodes);
                else foreach (var it in sel) DrawOutline(canvas, paint, it);
            }
            else if (sel.Count == 1 && gesture.Create is null) DrawSelection(canvas, paint, sel[0]);
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
            DrawRulers(canvas, paint);
        }

        // Visible area in document coordinates.
        (float X0, float Y0, float X1, float Y1) Visible() =>
            ((float)(-pan.X / zoom), (float)(-pan.Y / zoom), (float)((bounds.Width - pan.X) / zoom), (float)((bounds.Height - pan.Y) / zoom));

        void DrawGrid(SKCanvas canvas, SKPaint paint)
        {
            if (overlay.Grid <= 0) return;
            var g = overlay.Grid * Math.Max(1, Math.Ceiling(14 / (overlay.Grid * zoom))); // at low zoom one line out of many is drawn (snapping still uses the real step)
            var (x0, y0, x1, y1) = Visible();
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)(1 / zoom);
            paint.Color = dark ? new SKColor(255, 255, 255, 34) : new SKColor(0, 0, 0, 32);
            for (var i = (long)Math.Ceiling(x0 / g); i * g <= x1; i++) canvas.DrawLine((float)(i * g), y0, (float)(i * g), y1, paint);
            for (var i = (long)Math.Ceiling(y0 / g); i * g <= y1; i++) canvas.DrawLine(x0, (float)(i * g), x1, (float)(i * g), paint);
        }

        // Guides (cyan) and targets snapped to by the current gesture (magenta).
        void DrawGuides(SKCanvas canvas, SKPaint paint)
        {
            var (x0, y0, x1, y1) = Visible();
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)(1 / zoom);
            paint.Color = new SKColor(0x06, 0xB6, 0xD4);
            foreach (var g in overlay.Guides)
                if (g.Axis == "x") canvas.DrawLine((float)g.Position, y0, (float)g.Position, y1, paint);
                else canvas.DrawLine(x0, (float)g.Position, x1, (float)g.Position, paint);
            paint.Color = new SKColor(0xEC, 0x48, 0x99);
            if (overlay.SnapX is { } sx) canvas.DrawLine((float)sx, y0, (float)sx, y1, paint);
            if (overlay.SnapY is { } sy) canvas.DrawLine(x0, (float)sy, x1, (float)sy, paint);
        }

        // Rulers along the top and left edges, in the chosen unit; the origin is the page's corner.
        void DrawRulers(SKCanvas canvas, SKPaint paint)
        {
            var r = (float)RulerSize;
            var w = (float)bounds.Width; var h = (float)bounds.Height;
            paint.Style = SKPaintStyle.Fill;
            paint.Color = dark ? new SKColor(0x2A, 0x2B, 0x30) : new SKColor(0xF7, 0xF8, 0xFA);
            canvas.DrawRect(0, 0, w, r, paint);
            canvas.DrawRect(0, 0, r, h, paint);
            var fg = dark ? new SKColor(0xA0, 0xA6, 0xB0) : new SKColor(0x6B, 0x72, 0x80);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1;
            paint.Color = new SKColor(fg.Red, fg.Green, fg.Blue, 90);
            canvas.DrawLine(0, r, w, r, paint);
            canvas.DrawLine(r, 0, r, h, paint);

            var pxPerUnit = Units.ToPx(1, overlay.Unit); // document px per unit
            var raw = 60 / (zoom * pxPerUnit); // units for at least 60 screen px between two numbers
            var pow = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var m = raw / pow;
            var major = (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * pow;
            var minor = major / 5;
            using var font = new SKFont(SKTypeface.Default, 10);
            paint.Color = fg;
            paint.StrokeWidth = 1;

            void Axis(bool horizontal)
            {
                var origin = horizontal ? pan.X : pan.Y;
                var length = horizontal ? w : h;
                var from = (long)Math.Floor((-origin / zoom / pxPerUnit) / minor);
                for (var i = from; ; i++)
                {
                    var pos = (float)(i * minor * pxPerUnit * zoom + origin);
                    if (pos > length) break;
                    if (pos < r) continue;
                    var isMajor = i % 5 == 0;
                    var len = isMajor ? r * 0.65f : r * 0.3f;
                    if (horizontal) canvas.DrawLine(pos, r, pos, r - len, paint); else canvas.DrawLine(r, pos, r - len, pos, paint);
                    if (!isMajor) continue;
                    var label = (i * minor).ToString("0.###", CultureInfo.InvariantCulture);
                    paint.Style = SKPaintStyle.Fill;
                    if (horizontal) canvas.DrawText(label, pos + 3, 10, SKTextAlign.Left, font, paint);
                    else
                    {
                        canvas.Save();
                        canvas.Translate(10, pos - 3);
                        canvas.RotateDegrees(-90);
                        canvas.DrawText(label, 0, 0, SKTextAlign.Left, font, paint);
                        canvas.Restore();
                    }
                    paint.Style = SKPaintStyle.Stroke;
                }
            }
            canvas.Save();
            canvas.ClipRect(new SKRect(r, 0, w, r)); Axis(true);
            canvas.Restore();
            canvas.Save();
            canvas.ClipRect(new SKRect(0, r, r, h)); Axis(false);
            canvas.Restore();
        }

        // The page: white with a light shadow, or just the outline.
        void DrawPage(SKCanvas canvas, SKPaint paint)
        {
            if (pageView == PageView.Hidden) return;
            var r = SKRect.Create(0, 0, (float)page.Width, (float)page.Height);
            if (pageView == PageView.White)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new SKColor(0, 0, 0, dark ? (byte)120 : (byte)55);
                paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(5 / zoom));
                var shadow = r; shadow.Offset(0, (float)(3 / zoom));
                canvas.DrawRect(shadow, paint);
                paint.MaskFilter = null;
                paint.Color = SKColors.White;
                canvas.DrawRect(r, paint);
            }
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)(1 / zoom);
            paint.Color = pageView == PageView.Outline ? (dark ? new SKColor(0x9C, 0xA3, 0xAF) : new SKColor(0x6B, 0x72, 0x80)) : new SKColor(0xCB, 0xD5, 0xE1);
            canvas.DrawRect(r, paint);
        }

        // Every accent element has a white halo underneath: it stays visible on any color, even a light blue like the accent.
        void Haloed(SKPaint paint, float width, Action draw)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = width + (float)(1.8 / zoom);
            paint.Color = new SKColor(255, 255, 255, 235);
            draw();
            paint.StrokeWidth = width;
            paint.Color = Accent;
            draw();
        }

        void DrawGesture(SKCanvas canvas, SKPaint paint)
        {
            var w = (float)(1.5 / zoom);

            if (gesture.CreatePath is { } cp)
            {
                using var preview = SceneRenderer.BuildPath(cp, Matrix3x2.Identity);
                Haloed(paint, w, () => canvas.DrawPath(preview, paint));
            }
            if (gesture.Create is { } c)
            {
                var r = SKRect.Create((float)c.X, (float)c.Y, (float)c.Width, (float)c.Height);
                Haloed(paint, w, () => { if (gesture.CreateEllipse) canvas.DrawOval(r, paint); else canvas.DrawRect(r, paint); });
            }
            if (gesture.Marquee is { } mq)
            {
                var r = SKRect.Create((float)mq.X, (float)mq.Y, (float)mq.Width, (float)mq.Height);
                paint.Style = SKPaintStyle.Fill; paint.Color = Accent.WithAlpha(30);
                canvas.DrawRect(r, paint);
                Haloed(paint, (float)(1 / zoom), () => canvas.DrawRect(r, paint));
            }
            if (gesture.Pen is { } pen)
            {
                using var line = new SKPath();
                line.MoveTo(ToSk(pen[0].P));
                for (var i = 1; i < pen.Count; i++) PenSegment(line, pen[i - 1], pen[i]);
                // "Rubber band" segment towards the cursor (or towards the first node, if about to close): curved if the last node has a handle.
                var target = gesture.PenClosing ? pen[0].P : gesture.PenHover;
                if (pen[^1].Out is { } lastOut) line.CubicTo(ToSk(lastOut), ToSk(target), ToSk(target));
                else line.LineTo(ToSk(target));
                Haloed(paint, w, () => canvas.DrawPath(line, paint));

                // Handles of the last node, as in classic vector editors.
                var size = (float)(8 / zoom);
                var last = pen[^1];
                foreach (var h in new[] { last.In, last.Out })
                {
                    if (h is not { } hp) continue;
                    Haloed(paint, (float)(1.25 / zoom), () =>
                    {
                        canvas.DrawLine(ToSk(last.P), ToSk(hp), paint);
                        canvas.DrawCircle(ToSk(hp), size * 0.4f, paint);
                    });
                    paint.Style = SKPaintStyle.Fill; paint.Color = Accent;
                    canvas.DrawCircle(ToSk(hp), size * 0.4f, paint);
                }

                for (var i = 0; i < pen.Count; i++)
                {
                    var p = ToSk(pen[i].P);
                    var first = i == 0 && pen.Count >= 2;
                    Haloed(paint, (float)(1.25 / zoom), () =>
                    {
                        if (first) canvas.DrawCircle(p, size * 0.8f, paint); else canvas.DrawRect(SKRect.Create(p.X - size / 2, p.Y - size / 2, size, size), paint);
                    });
                    paint.Style = SKPaintStyle.Fill; paint.Color = first && gesture.PenClosing ? Accent : SKColors.White;
                    if (first) canvas.DrawCircle(p, size * 0.8f, paint); else canvas.DrawRect(SKRect.Create(p.X - size / 2, p.Y - size / 2, size, size), paint);
                }
            }
        }

        static void PenSegment(SKPath path, PenNodeView a, PenNodeView b)
        {
            if (a.Out is null && b.In is null) path.LineTo(ToSk(b.P));
            else path.CubicTo(ToSk(a.Out ?? a.P), ToSk(b.In ?? b.P), ToSk(b.P));
        }

        // Gradient handles: lines, radius ellipse and points filled with the stop's color.
        void DrawFill(SKCanvas canvas, SKPaint paint, FillView v)
        {
            var w = (float)(1.5 / zoom);
            if (v.Ellipse is { } ell)
            {
                using var path = new SKPath();
                path.MoveTo(ToSk(ell[0]));
                foreach (var p in ell.Skip(1)) path.LineTo(ToSk(p));
                Haloed(paint, w, () => canvas.DrawPath(path, paint));
            }
            foreach (var (a, b) in v.Lines) Haloed(paint, w, () => canvas.DrawLine(ToSk(a), ToSk(b), paint));

            var size = (float)(10 / zoom);
            for (var i = 0; i < v.Handles.Count; i++)
            {
                var h = v.Handles[i];
                var c = ToSk(h.Pos);
                var selected = v.Selected == h.Index && h.Part is FillPart.Stop or FillPart.Start or FillPart.End or FillPart.Center or FillPart.RadX or FillPart.RadY && v.Selected >= 0
                               && IsSelectedHandle(v, h);
                var round = h.Part is FillPart.Stop or FillPart.RadX or FillPart.RadY;
                using var shape = new SKPath();
                var r = SKRect.Create(c.X - size / 2, c.Y - size / 2, size, size);
                if (round) shape.AddOval(r); else shape.AddRect(r);
                Haloed(paint, (float)(2.2 / zoom), () => canvas.DrawPath(shape, paint));
                paint.Style = SKPaintStyle.Fill;
                paint.Color = SceneRenderer.ParseColor(h.Color) is { } col ? col.WithAlpha(255) : SKColors.White;
                canvas.DrawPath(shape, paint);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)((selected ? 3 : 1.5) / zoom);
                paint.Color = selected ? Accent : SKColors.White;
                canvas.DrawPath(shape, paint);
            }
        }

        // The selected stop has a single highlighted point: the one with the same index (for the ends, the first that carries it).
        static bool IsSelectedHandle(FillView v, FillHandle h) => v.Handles.First(x => x.Index == v.Selected) == h;

        // Path nodes: the shape shows the kind (square = cusp, circle = smooth, diamond = symmetric);
        // selected nodes are filled and show their handles.
        void DrawNodes(SKCanvas canvas, SKPaint paint, List<NodeV> nodes)
        {
            var size = (float)(8 / zoom);
            var w = (float)(1.25 / zoom);
            foreach (var n in nodes.Where(n => n.Selected))
                foreach (var h in new[] { n.In, n.Out })
                {
                    if (h is not { } hp) continue;
                    Haloed(paint, w, () =>
                    {
                        canvas.DrawLine(ToSk(n.P), ToSk(hp), paint);
                        canvas.DrawCircle(ToSk(hp), size * 0.55f, paint);
                    });
                    paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White;
                    canvas.DrawCircle(ToSk(hp), size * 0.55f, paint);
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = w; paint.Color = Accent;
                    canvas.DrawCircle(ToSk(hp), size * 0.55f, paint);
                }
            foreach (var n in nodes)
            {
                var r = SKRect.Create(n.P.X - size / 2, n.P.Y - size / 2, size, size);
                using var shape = new SKPath();
                switch (n.Kind)
                {
                    case NodeKind.Smooth: shape.AddOval(r); break;
                    case NodeKind.Symmetric:
                        var hh = size * 0.72f;
                        shape.MoveTo(n.P.X, n.P.Y - hh); shape.LineTo(n.P.X + hh, n.P.Y); shape.LineTo(n.P.X, n.P.Y + hh); shape.LineTo(n.P.X - hh, n.P.Y);
                        shape.Close();
                        break;
                    default: shape.AddRect(r); break;
                }
                Haloed(paint, w, () => canvas.DrawPath(shape, paint));
                paint.Style = SKPaintStyle.Fill; paint.Color = n.Selected ? Accent : SKColors.White;
                canvas.DrawPath(shape, paint);
                paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = w; paint.Color = Accent;
                canvas.DrawPath(shape, paint);
            }
        }

        // Oriented outline of the object's rectangle.
        void DrawOutline(SKCanvas canvas, SKPaint paint, Item it)
        {
            using var outline = new SKPath();
            outline.MoveTo(ToSk(PointOn(it, 0, 0)));
            outline.LineTo(ToSk(PointOn(it, 1, 0)));
            outline.LineTo(ToSk(PointOn(it, 1, 1)));
            outline.LineTo(ToSk(PointOn(it, 0, 1)));
            outline.Close();
            Haloed(paint, (float)(1.5 / zoom), () => canvas.DrawPath(outline, paint));
        }

        void DrawSelection(SKCanvas canvas, SKPaint paint, Item sel)
        {
            DrawOutline(canvas, paint, sel);

            var size = (float)(8 / zoom);
            var w = (float)(1.5 / zoom);
            foreach (var h in HandlesOf(sel, zoom))
            {
                var c = ToSk(h.Pos);
                if (h.Rotate) Haloed(paint, w, () => canvas.DrawLine(ToSk(PointOn(sel, .5, 0)), c, paint));
                Haloed(paint, w, () =>
                {
                    if (h.Rotate) canvas.DrawCircle(c, size * 0.65f, paint);
                    else canvas.DrawRect(SKRect.Create(c.X - size / 2, c.Y - size / 2, size, size), paint);
                });
                paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White;
                if (h.Rotate) canvas.DrawCircle(c, size * 0.65f, paint);
                else canvas.DrawRect(SKRect.Create(c.X - size / 2, c.Y - size / 2, size, size), paint);
                paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = w; paint.Color = Accent;
                if (h.Rotate) canvas.DrawCircle(c, size * 0.65f, paint);
                else canvas.DrawRect(SKRect.Create(c.X - size / 2, c.Y - size / 2, size, size), paint);
            }
        }
    }
}
