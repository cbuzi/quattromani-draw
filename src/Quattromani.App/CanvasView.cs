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

public enum Tool { Select, Node, Rectangle, Ellipse, Pen, Text, Fill }

/// <summary>Come si vede la pagina nell'area di lavoro.</summary>
public enum PageView { White, Outline, Hidden }

/// <summary>Richiesta di modificare un testo: un oggetto esistente (Id) oppure un testo nuovo in un punto, con larghezza d'a capo opzionale.</summary>
public sealed record TextEditRequest(string? Id, Point At, double? WrapWidth);

public enum NodeOp { ToLine, ToCurve, Cusp, Smooth, Symmetric, Delete }

/// <summary>
/// Canvas Skia con pan/zoom, selezione (anche multipla), spostamento, ridimensionamento, rotazione e penna.
/// Le modifiche passano solo dal CommandBus.
/// </summary>
public sealed class CanvasView : Control
{
    enum Drag { None, Pan, Move, Create, Resize, Rotate, Marquee, PenNode, NodeMove, NodeHandle, TextBox, FillHandle, FillDraw, GuideNew, GuideMove }
    enum FillPart { Start, End, Stop, Center, RadX, RadY }
    enum NodePart { Node, In, Out }

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
    /// <summary>Nodo della penna (coordinate documento); In/Out sono le maniglie della curva, assenti se il nodo è angolare.</summary>
    sealed class PenNode(Point p)
    {
        public Point P = p;
        public Point? In, Out;
    }

    readonly List<PenNode> _pen = [];

    // Strumento Nodi: selezione dei nodi (sottopath, indice) del path selezionato; durante un trascinamento
    // _nodeOrig è il modello a inizio gesto e _nodeWork quello modificato (anteprima: il documento cambia al rilascio).
    // Strumento di riempimento: _fillWork è il gradiente in modifica durante un trascinamento (anteprima; il documento
    // cambia al rilascio), _fillBase quello a inizio gesto; _fillSel è la sosta di colore selezionata.
    GradientSpec? _fillWork, _fillBase;
    FillHandle? _fillHit;
    Item? _fillItem;
    Point _fillStart;
    int _fillSel = -1;

    /// <summary>Soste di colore con cui nasce un nuovo gradiente trascinando sull'oggetto (le fornisce il pannello).</summary>
    public Func<List<GradientStop>>? DefaultStops { get; set; }
    public int FillSelectedStop => _fillSel;

    readonly HashSet<(int Sub, int Idx)> _nodeSel = [];
    List<SubPath>? _nodeOrig, _nodeWork;
    (int Sub, int Idx, NodePart Part) _nodeHit;
    double _nodeHandleX, _nodeHandleY;
    Point _penHover;

    // Ridimensionamento / rotazione in corso (anteprima: il documento cambia solo al rilascio).
    Item? _dragItem;
    double _handleX, _handleY; // maniglia trascinata, in frazioni del rettangolo
    double _newW, _newH;
    Vector2 _rotCenter;
    double _startAngle, _rotDelta;

    // Griglia, guide e aggancio (le guide stanno nel documento; il resto è stato di vista).
    const double RulerSize = 20;
    bool _showGrid;
    public bool ShowGrid { get => _showGrid; set { _showGrid = value; InvalidateVisual(); } }
    /// <summary>Passo della griglia in px di documento.</summary>
    public double GridSize { get; set; } = 10;
    public bool SnapOn { get; set; } = true;
    public bool SnapToGrid { get; set; } = true;
    public bool SnapToGuides { get; set; } = true;
    public bool SnapToObjects { get; set; } = true;
    readonly List<double> _snapXs = [], _snapYs = []; // guide, bordi e centri degli altri oggetti e della pagina
    double? _snapLineX, _snapLineY; // obiettivi agganciati dal gesto in corso, mostrati come linee
    Box _moveBounds;
    string _guideAxis = "x";
    double _guidePos, _guideFrom;
    bool _guideOut; // la guida è sopra un righello: al rilascio si elimina

    const long HighlightMs = 3000;
    readonly Dictionary<string, (long Stamp, string Agent)> _highlights = []; // id oggetto -> TickCount64 e agente della modifica
    readonly DispatcherTimer _fade;

    // Un pill per agente, sull'ultimo oggetto che ha toccato: resta fermo, poi sfuma.
    const long PillHoldMs = 1500, PillFadeMs = 500;
    const int MaxPills = 4;
    readonly Dictionary<string, (string Id, long Stamp)> _pills = [];
    readonly Dictionary<string, SKColor> _agentColors = []; // colore assegnato all'agente al suo primo intervento
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
    /// <summary>L'id selezionato, solo se la selezione è esattamente un oggetto.</summary>
    public string? SelectedId => _selection.Count == 1 ? _selection[0] : null;
    public double Zoom => _zoom;
    public int NodeSelectionCount => _nodeSel.Count;

    PageView _pageView = PageView.Outline;
    /// <summary>Pagina bianca con ombra, solo il contorno, oppure nascosta.</summary>
    public PageView PageViewMode { get => _pageView; set { _pageView = value; InvalidateVisual(); } }

    /// <summary>Misure mostrate all'utente: numero nell'unità scelta (le fornisce la finestra) e nome dell'unità.</summary>
    public Func<double, string> FormatLength { get; set; } = v => v.ToString("0.##");
    public string UnitLabel { get; set; } = "px";

    /// <summary>Testo informativo durante un gesto (dimensioni, posizione, angolo); null = fine del gesto.</summary>
    public event Action<string?>? InfoChanged;
    /// <summary>Posizione del cursore in coordinate documento; null quando esce dal canvas.</summary>
    public event Action<Point?>? CursorMoved;

    /// <summary>Riquadro della pagina in coordinate documento.</summary>
    Box PageBox() => Bus?.Read(d => new Box(0, 0, d.Pages[0].Width, d.Pages[0].Height)) ?? new Box(0, 0, 794, 1123);

    /// <summary>Inquadra un riquadro del documento, centrato e con un margine in pixel.</summary>
    public void ZoomToBox(Box b, double margin = 48)
    {
        if (Bounds.Width < 10 || Bounds.Height < 10 || b.Width <= 0 || b.Height <= 0) return;
        _zoom = Math.Clamp(Math.Min((Bounds.Width - 2 * margin) / b.Width, (Bounds.Height - 2 * margin) / b.Height), 0.02, 64);
        _pan = new((Bounds.Width - b.Width * _zoom) / 2 - b.X * _zoom, (Bounds.Height - b.Height * _zoom) / 2 - b.Y * _zoom);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    public void FitPage() => ZoomToBox(PageBox());

    /// <summary>Zoom al 100%, mantenendo fermo il centro della finestra.</summary>
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

    /// <summary>Oggetto da non disegnare (il testo che si sta modificando sul posto).</summary>
    string? _hiddenId;
    public string? HiddenId { get => _hiddenId; set { _hiddenId = value; InvalidateVisual(); } }

    public event Action<TextEditRequest>? TextEditRequested;

    public Point DocToScreen(Point p) => new(p.X * _zoom + _pan.X, p.Y * _zoom + _pan.Y);

    /// <summary>Dove mostrare l'editor di un testo: angolo in alto a sinistra sullo schermo e rotazione in gradi.</summary>
    public (Point TopLeft, double Rotation)? EditorPlacement(string id)
    {
        var it = Bus?.Read(d => d.Objects.FirstOrDefault(o => o.Id == id) is { } o ? Item.From(o) : null);
        if (it == null) return null;
        var p = PointOn(it, 0, 0);
        return (DocToScreen(new Point(p.X, p.Y)), Math.Atan2(it.M.M12, it.M.M11) * 180 / Math.PI);
    }

    bool IsText(string id) => Bus?.Read(d => d.Objects.FirstOrDefault(o => o.Id == id) is TextObject) ?? false;

    /// <summary>Modifica il testo selezionato (Invio / F2).</summary>
    public void EditSelectedText()
    {
        if (SelectedId is { } id && IsText(id)) TextEditRequested?.Invoke(new TextEditRequest(id, default, null));
    }

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
            if (_tool == Tool.Pen && _pen.Count >= 2) FinishPen(false, false); // cambiando strumento il path aperto si conserva
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

    // ---- Barre di scorrimento ------------------------------------------------------------------

    Box _scrollRange; // area scorrevole in coordinate documento, dall'ultimo ScrollInfo

    /// <summary>
    /// Misure per le barre di scorrimento, in pixel schermo. L'area scorrevole è il contenuto allargato di mezza finestra
    /// per lato (si può sempre uscire un po' dai bordi), unita alla finestra stessa se l'utente è andato oltre.
    /// </summary>
    public (double TotalW, double TotalH, double OffX, double OffY, double ViewW, double ViewH) ScrollInfo()
    {
        var boxes = Bus?.Read(d => d.Objects.Select(o => o.Bounds).ToList()) ?? [];
        var page = PageBox(); // l'area scorrevole comprende sempre la pagina
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

    /// <summary>Porta la finestra a una posizione (in pixel dall'inizio dell'area scorrevole); null = invariato.</summary>
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

    /// <summary>Toglie dalla selezione gli oggetti che non esistono più.</summary>
    public void PruneSelection(ICollection<string> valid) => SetSelection(_selection.Where(valid.Contains));

    /// <summary>Raccoglie gli obiettivi dell'aggancio per il gesto che inizia (gli oggetti in <paramref name="exclude"/> non contano: si stanno muovendo).</summary>
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
                foreach (var o in d.Objects)
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

    /// <summary>Correzione da sommare a <paramref name="v"/> per agganciarla all'obiettivo più vicino (entro 6 px schermo), e coordinata dell'obiettivo.</summary>
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

    /// <summary>Aggancia un punto (x e y indipendenti) e ricorda gli obiettivi trovati per mostrarli.</summary>
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

    // Spostamento: il riquadro della selezione aggancia bordo sinistro/centro/destro e alto/centro/basso.
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

    // ---- Righelli e guide ----

    bool InRuler(Point p) => p.X < RulerSize || p.Y < RulerSize;

    Guide? HitGuide(Point screen) =>
        Bus?.Read(d => d.Pages[0].Guides.FirstOrDefault(g =>
            Math.Abs(g.Axis == "x" ? g.Position * _zoom + _pan.X - screen.X : g.Position * _zoom + _pan.Y - screen.Y) <= 4));

    // Dai righelli si tirano fuori le guide; con lo strumento Selezione si spostano quelle esistenti.
    bool StartGuideDrag(Point pos)
    {
        if (InRuler(pos))
        {
            if (pos.X < RulerSize && pos.Y < RulerSize) return true; // angolo: niente
            _guideAxis = pos.Y < RulerSize ? "y" : "x"; // dal righello in alto una guida orizzontale
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
                using var path = SceneRenderer.BuildPath(po.Segments, po.Fit);
                if (po.Fill != null && path.Contains(l.X, l.Y)) return true;
                // Il tratto si "clicca" con una tolleranza minima, anche se è sottile o assente.
                using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)(2 * Math.Max(tolerance, po.StrokeWidth / 2)) };
                using var outline = paint.GetFillPath(path);
                return outline != null && outline.Contains(l.X, l.Y);
            }
            case TextObject { PathGlyphs: { } glyphs } t: // testo su tracciato: si clicca sulle lettere e tra una lettera e l'altra
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
                Select(textHit); // click su un testo esistente: lo si modifica
                TextEditRequested?.Invoke(new TextEditRequest(textHit, default, null));
                return;
            }
            // Click = testo "artistico" in quel punto; trascinando si disegna la larghezza per l'a capo automatico.
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
                if (e.ClickCount >= 2 && !shift && IsText(hit)) // doppio click su un testo: modifica
                {
                    Select(hit);
                    TextEditRequested?.Invoke(new TextEditRequest(hit, default, null));
                    return;
                }
                if (shift) // Shift+click: aggiunge o toglie dalla selezione
                    SetSelection(_selection.Contains(hit) ? _selection.Where(i => i != hit) : _selection.Append(hit));
                else
                {
                    if (!_selection.Contains(hit)) SetSelection([hit]);
                    _drag = Drag.Move; // sposta tutta la selezione
                    BeginMove();
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

    // Penna (come lo strumento Bézier degli editor vettoriali classici): click = nodo angolare; click e trascina = nodo liscio, con le
    // maniglie della curva simmetriche; click sul primo nodo chiude; doppio click o Invio termina il path aperto.
    void PenPress(Point screen, int clickCount)
    {
        if (clickCount >= 2) { FinishPen(false); return; }
        if (_pen.Count >= 2 && NearFirstNode(screen)) { FinishPen(true); return; }
        BeginSnap();
        _pen.Add(new PenNode(SnapPoint(ToDoc(screen))));
        _drag = Drag.PenNode; // trascinando si tirano fuori le maniglie
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

    /// <summary>Toglie l'ultimo nodo.</summary>
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
            if (nodes[^1].Out != null || nodes[0].In != null) Segment(nodes[^1], nodes[0]); // chiusura curva
            sb.Append(" Z");
        }

        var r = Bus.Execute([new CreateShape
        {
            Type = "path", D = sb.ToString(),
            Fill = closed ? Fill ?? "none" : "none", // un path aperto non si riempie
            Stroke = Stroke ?? "#1F2937", StrokeWidth = StrokeWidth > 0 ? StrokeWidth : 2,
        }]);
        Select(r.CreatedIds[0]);
        if (select) Tool = Tool.Select;
    }

    // ---- Strumento di riempimento -------------------------------------------------------------

    sealed record FillHandle(FillPart Part, int Index, Vector2 Pos, string Color);
    sealed record FillView(List<FillHandle> Handles, List<(Vector2 A, Vector2 B)> Lines, List<Vector2>? Ellipse, int Selected);

    // Il gradiente è relativo a questo riquadro, nello spazio locale dell'oggetto (per il testo: quello impaginato, poi stirato da Fit).
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
        if (hit != null && hit != SelectedId) { Select(hit); return; } // un altro oggetto: si lavora su quello
        if (it != null && it.Kind != Kind.Group && hit == it.Id)
        {
            // Trascinando sull'oggetto si disegna un nuovo gradiente (come lo strumento di riempimento interattivo degli editor vettoriali classici).
            _fillItem = it; _fillBase = g; _fillHit = null;
            _fillStart = ToDoc(screen);
            _drag = Drag.FillDraw;
        }
    }

    // Doppio click sulla linea del gradiente: aggiunge una sosta di colore in quel punto.
    bool AddStopAt(Item it, GradientSpec g, Point screen)
    {
        var box = GradBox(it);
        var d = ToDoc(screen);
        if (PageToFrac(it, new Vector2((float)d.X, (float)d.Y)) is not { } f) return false;
        double ax, ay, bx, by; // linea su cui stanno le soste, in frazioni del riquadro
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
        var g0 = _fillBase; // null se l'oggetto non aveva ancora un gradiente (si sta disegnando il primo)
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
                var r = Math.Sqrt(Math.Pow((f.X - s.X) * w, 2) + Math.Pow((f.Y - s.Y) * h, 2)); // raggio in unità documento
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
                    if (shift && w > 0 && h > 0) // Shift: angolo a scatti di 15°
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
                    _fillWork = g with { CenterX = f.X, CenterY = f.Y, Circle = false, Rx = rx, Ry = ry }; // il raggio resta lo stesso
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

    /// <summary>Con la sosta selezionata sul canvas, un colore della tavolozza la cambia. False se non c'è una sosta selezionata.</summary>
    public bool TrySetSelectedStopColor(string color)
    {
        if (Tool != Tool.Fill || _fillSel < 0 || SnapshotSelected() is not { } it || ParseFill(it.Fill) is not { } g || _fillSel >= g.Stops.Count) return false;
        var st = g.Stops.ToList();
        st[_fillSel] = st[_fillSel] with { Color = color.ToUpperInvariant() };
        CommitFill(Gradient.Format(g with { Stops = st }));
        return true;
    }

    /// <summary>Toglie la sosta selezionata (ne servono almeno due).</summary>
    public bool DeleteSelectedStop()
    {
        if (Tool != Tool.Fill || _fillSel < 0 || SnapshotSelected() is not { } it || ParseFill(it.Fill) is not { } g || g.Stops.Count <= 2 || _fillSel >= g.Stops.Count) return false;
        var st = g.Stops.ToList();
        st.RemoveAt(_fillSel);
        _fillSel = -1;
        CommitFill(Gradient.Format(g with { Stops = st }));
        return true;
    }

    // ---- Strumento Nodi ------------------------------------------------------------------------

    // Modello a nodi del path in coordinate pagina (la trasformazione è già applicata).
    static List<SubPath> ModelOf(Item it) => PathNodes.FromSegments(PathData.Transform(it.Segments!, it.Fit * it.M), it.NodeKinds);

    List<SubPath>? CurrentModel() =>
        Tool == Tool.Node && SnapshotSelected() is { Kind: Kind.Path } it ? ModelOf(it) : null;

    bool Near(Vector2 v, Point screen) =>
        Math.Abs(v.X * _zoom + _pan.X - screen.X) <= 7 && Math.Abs(v.Y * _zoom + _pan.Y - screen.Y) <= 7;

    (int Sub, int Idx, NodePart Part)? HitNodePart(List<SubPath> model, Point screen)
    {
        // Le maniglie dei nodi selezionati stanno sopra i nodi.
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
                    if (clickCount >= 2) // doppio click su un nodo: lo elimina
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

            // Doppio click sul tracciato: aggiunge un nodo senza cambiare la forma.
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
            if (obj != SelectedId) Select(obj); // un altro oggetto: mostra i suoi nodi
            return;
        }
        if (!shift) _nodeSel.Clear();
        _marqueeAdd = shift;
        _marqueeStart = _marqueeEnd = ToDoc(screen);
        _drag = Drag.Marquee;
    }

    // Anteprima del trascinamento di nodi o maniglie, a partire dal modello a inizio gesto.
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
            PathNodes.MoveHandle(n, _nodeHit.Part == NodePart.Out, new((float)d.X, (float)d.Y), alt); // Alt: maniglie indipendenti
        }
        _nodeWork = work;
    }

    void CommitNodes(List<SubPath> subs)
    {
        if (SelectedId is not { } id || Bus == null) return;
        try
        {
            Bus.Execute([new SetPath { Target = id, D = PathData.ToString(PathNodes.ToSegments(subs), 4), Kinds = PathNodes.KindsOf(subs) }]);
        }
        catch (CommandException) { }
    }

    /// <summary>Applica un'operazione ai nodi selezionati del path (un solo undo).</summary>
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

    protected override void OnPointerExited(PointerEventArgs e) => CursorMoved?.Invoke(null);

    // Misure in tempo reale durante un gesto: dimensioni mentre si disegna o ridimensiona, posizione mentre si sposta.
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
                n.Out = d; // la maniglia segue il mouse, l'altra è il suo specchio rispetto al nodo
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
        if (_drag == Drag.None && Tool == Tool.Select) // nessun gesto: cursore in base a cosa c'è sotto
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
            _createEnd = SnapPoint(ToDoc(pos));
        else if (_drag == Drag.Marquee)
            _marqueeEnd = ToDoc(pos);
        else if (_drag == Drag.Resize && _dragItem is { } it)
        {
            if (!Matrix3x2.Invert(it.M, out var inv)) return;
            var d = ToDoc(pos);
            _snapLineX = _snapLineY = null;
            if (Math.Abs(it.M.M12) < 1e-4 && Math.Abs(it.M.M21) < 1e-4) // l'aggancio vale solo per oggetti non ruotati
            {
                if (_handleX != .5 && SnapAxis(d.X, _snapXs) is { } sx) { d = new(d.X + sx.Delta, d.Y); _snapLineX = sx.Line; }
                if (_handleY != .5 && SnapAxis(d.Y, _snapYs) is { } sy) { d = new(d.X, d.Y + sy.Delta); _snapLineY = sy.Line; }
            }
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
            catch (CommandException) { } // per esempio una guida già presente nello stesso punto
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
            // Selezione a rettangolo dei nodi del path.
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
        if (_hiddenId != null) items.RemoveAll(i => i.Id == _hiddenId);
        for (var i = 0; i < items.Count; i++)
            if (_selection.Contains(items[i].Id)) items[i] = Preview(items[i]);

        // Strumento Nodi: modello da mostrare (quello in modifica, se c'è) e anteprima del path modificato.
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

        // Strumento di riempimento: anteprima del gradiente in modifica e maniglie.
        FillView? fillView = null;
        if (Tool == Tool.Fill && SelectedId != null && items.FindIndex(i => i.Id == SelectedId) is var fk and >= 0 && items[fk].Kind != Kind.Group)
        {
            var spec = _fillWork ?? ParseFill(items[fk].Fill);
            if (_fillWork != null) items[fk] = items[fk] with { Fill = Gradient.Format(_fillWork) };
            if (spec != null) fillView = BuildFillView(items[fk], spec);
        }

        // L'alone di un oggetto modificato dentro un gruppo si mostra sul gruppo.
        var now = Environment.TickCount64;
        var glow = new Dictionary<string, (float Alpha, SKColor Color)>();
        foreach (var it in items)
        {
            var best = _highlights.Where(h => HasId(it, h.Key))
                .Select(h => (Alpha: (float)Math.Max(0, 1 - (now - h.Value.Stamp) / (double)HighlightMs), h.Value.Agent))
                .OrderByDescending(h => h.Alpha).FirstOrDefault();
            if (best.Alpha > 0) glow[it.Id] = (best.Alpha, ColorOf(best.Agent));
        }

        // Pill: al massimo MaxPills, i più recenti; sullo stesso oggetto si impilano.
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

        var gesture = new Gesture(
            _drag is Drag.Create or Drag.TextBox ? CreateBox() : null, Tool == Tool.Ellipse,
            _drag == Drag.Marquee ? MarqueeBox() : null,
            Tool == Tool.Pen && _pen.Count > 0
                ? _pen.Select(n => new PenNodeView(V(n.P), n.In is { } i ? V(i) : null, n.Out is { } o ? V(o) : null)).ToList()
                : null,
            V(_penHover),
            Tool == Tool.Node,
            nodeModel?.SelectMany((sp, si) => sp.Nodes.Select((n, ni) => new NodeV(n.P, n.In, n.Out, _nodeSel.Contains((si, ni)), n.Kind))).ToList(),
            Tool == Tool.Pen && _pen.Count >= 2 && _drag != Drag.PenNode &&
                NearFirstNode(new Point(_penHover.X * _zoom + _pan.X, _penHover.Y * _zoom + _pan.Y)),
            Tool == Tool.Fill, fillView);
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

    // Gesti in corso da disegnare come anteprima.
    static Vector2 V(Point p) => new((float)p.X, (float)p.Y);

    /// <summary>Griglia (passo in px, 0 = nascosta), guide, linee d'aggancio in corso e unità dei righelli.</summary>
    sealed record Overlay(List<Guide> Guides, double Grid, double? SnapX, double? SnapY, string Unit);

    /// <summary>Etichetta dell'agente: nome, riquadro dell'oggetto (coordinate documento) e opacità.</summary>
    sealed record AgentPill(string Name, Box Bounds, float Alpha, SKColor Color, int Slot);

    sealed record PenNodeView(Vector2 P, Vector2? In, Vector2? Out);

    sealed record NodeV(Vector2 P, Vector2? In, Vector2? Out, bool Selected, NodeKind Kind);

    sealed record Gesture(Box? Create, bool CreateEllipse, Box? Marquee, List<PenNodeView>? Pen, Vector2 PenHover,
        bool NodeMode, List<NodeV>? Nodes, bool PenClosing, bool FillMode, FillView? FillOverlay);

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

        // Pill di dimensione fissa in pixel schermo, sopra l'angolo in alto a destra dell'oggetto (rientra nel viewport).
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
            canvas.Scale(1 / z); // torna in coordinate schermo
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

            // Oggetti toccati dagli agenti: alone nel colore dell'agente che sfuma.
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

            // Selezione: un oggetto = contorno orientato e maniglie; più oggetti = contorni e riquadro complessivo.
            var sel = items.Where(i => selection.Contains(i.Id)).ToList();
            if (gesture.FillMode)
            {
                foreach (var it in sel) DrawOutline(canvas, paint, it); // strumento di riempimento: contorno + maniglie del gradiente
                if (gesture.FillOverlay is { } fo) DrawFill(canvas, paint, fo);
            }
            else if (gesture.NodeMode)
            {
                // Strumento Nodi: niente maniglie di trasformazione; per un path si vedono i suoi nodi.
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

        // Area visibile in coordinate documento.
        (float X0, float Y0, float X1, float Y1) Visible() =>
            ((float)(-pan.X / zoom), (float)(-pan.Y / zoom), (float)((bounds.Width - pan.X) / zoom), (float)((bounds.Height - pan.Y) / zoom));

        void DrawGrid(SKCanvas canvas, SKPaint paint)
        {
            if (overlay.Grid <= 0) return;
            var g = overlay.Grid * Math.Max(1, Math.Ceiling(14 / (overlay.Grid * zoom))); // a zoom basso si disegna una linea ogni tante (l'aggancio usa comunque il passo vero)
            var (x0, y0, x1, y1) = Visible();
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)(1 / zoom);
            paint.Color = dark ? new SKColor(255, 255, 255, 34) : new SKColor(0, 0, 0, 32);
            for (var i = (long)Math.Ceiling(x0 / g); i * g <= x1; i++) canvas.DrawLine((float)(i * g), y0, (float)(i * g), y1, paint);
            for (var i = (long)Math.Ceiling(y0 / g); i * g <= y1; i++) canvas.DrawLine(x0, (float)(i * g), x1, (float)(i * g), paint);
        }

        // Guide (ciano) e obiettivi agganciati dal gesto in corso (magenta).
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

        // Righelli lungo i bordi alto e sinistro, nell'unità scelta; l'origine è l'angolo della pagina.
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

            var pxPerUnit = Units.ToPx(1, overlay.Unit); // px di documento per unità
            var raw = 60 / (zoom * pxPerUnit); // unità per almeno 60 px schermo tra due numeri
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

        // La pagina: bianca con un'ombra leggera, oppure solo il contorno.
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

        // Ogni elemento d'accento ha un alone bianco sotto: resta visibile su qualsiasi colore, anche azzurro come l'accento.
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
                // Segmento "elastico" verso il cursore (o verso il primo nodo, se sta per chiudere): curvo se l'ultimo nodo ha una maniglia.
                var target = gesture.PenClosing ? pen[0].P : gesture.PenHover;
                if (pen[^1].Out is { } lastOut) line.CubicTo(ToSk(lastOut), ToSk(target), ToSk(target));
                else line.LineTo(ToSk(target));
                Haloed(paint, w, () => canvas.DrawPath(line, paint));

                // Maniglie dell'ultimo nodo, come negli editor vettoriali classici.
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

        // Maniglie del gradiente: linee, ellisse del raggio e punti riempiti col colore della sosta.
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

        // La sosta selezionata ha un solo punto evidenziato: quello con lo stesso indice (per gli estremi, il primo che lo porta).
        static bool IsSelectedHandle(FillView v, FillHandle h) => v.Handles.First(x => x.Index == v.Selected) == h;

        // Nodi del path: la forma indica il tipo (quadrato = cuspide, cerchio = liscio, rombo = simmetrico);
        // i nodi selezionati sono pieni e mostrano le loro maniglie.
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

        // Contorno orientato del rettangolo dell'oggetto.
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
