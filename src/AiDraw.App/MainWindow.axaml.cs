using AiDraw.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GradientStop = AiDraw.Core.GradientStop;

namespace AiDraw.App;

public partial class MainWindow : Window
{
    readonly CommandBus _bus = App.Bus;
    List<string> _ids = [];
    bool _syncing, _syncingScroll, _syncingText, _syncingGrad;
    // Pannello "FILL TYPE": 0 = tinta unita, 1 = lineare, 2 = radiale, 3 = motivo. _gradArmed = quale colore sta per essere scelto dalla tavolozza.
    int _fillKind, _gradArmed = -1;
    string _solid = "#3B82F6";
    GradientSpec _spec = new(false, 90, false, 0.5, 0.5, [new("#3B82F6", 0), new("#FFFFFF", 1)]);
    PatternSpec _pat = new("stripes", "#3B82F6", "#FFFFFF", 10, 0);
    TextEditRequest? _edit;
    string _editOriginal = "";
    TextStyle _textDefaults = new("", 24, false, false, "left", 1); // stile dei testi nuovi; la famiglia si imposta all'avvio
    long _seenVersion;
    readonly HashSet<string> _aiTouched = []; // oggetti la cui ultima modifica è dell'agente
    readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _chipTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow()
    {
        InitializeComponent();
        Canvas.Bus = _bus;

        // Il bus può notificare da qualsiasi thread.
        _seenVersion = _bus.Version;
        _bus.Changed += () => Dispatcher.UIThread.Post(OnBusChanged);
        _chipTimer.Tick += (_, _) => { _chipTimer.Stop(); AgentChip.IsVisible = false; };
        Canvas.SelectionChanged += Refresh;
        Canvas.ViewChanged += () => { ZoomText.Text = $"{Canvas.Zoom:P0}"; UpdateScrollBars(); };
        Canvas.SizeChanged += (_, _) => UpdateScrollBars();
        HScroll.ValueChanged += (_, e) => { if (!_syncingScroll) Canvas.ScrollTo(e.NewValue, null); };
        VScroll.ValueChanged += (_, e) => { if (!_syncingScroll) Canvas.ScrollTo(null, e.NewValue); };
        ZoomText.Text = $"{Canvas.Zoom:P0}";

        WeldButton.Click += (_, _) => RunBoolean("weld");
        TrimButton.Click += (_, _) => RunBoolean("trim");
        IntersectButton.Click += (_, _) => RunBoolean("intersect");
        SubtractButton.Click += (_, _) => RunBoolean("subtract");
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); NoticeText.IsVisible = false; };

        UndoButton.Click += (_, _) => _bus.Undo();
        RedoButton.Click += (_, _) => _bus.Redo();
        ThemeButton.Click += (_, _) => Application.Current!.RequestedThemeVariant =
            ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;

        ObjectList.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var i = ObjectList.SelectedIndex;
            Canvas.Select(i >= 0 ? _ids[i] : null);
        };

        // Strumenti
        SelectTool.Click += (_, _) => { Canvas.Tool = Tool.Select; SyncToolButtons(); };
        RectTool.Click += (_, _) => { Canvas.Tool = Tool.Rectangle; SyncToolButtons(); };
        EllipseTool.Click += (_, _) => { Canvas.Tool = Tool.Ellipse; SyncToolButtons(); };
        PenTool.Click += (_, _) => { Canvas.Tool = Tool.Pen; SyncToolButtons(); };
        NodeTool.Click += (_, _) => { Canvas.Tool = Tool.Node; SyncToolButtons(); };
        TextTool.Click += (_, _) => { Canvas.Tool = Tool.Text; SyncToolButtons(); };

        // Testo: stile dei nuovi testi, barra del testo ed editor sul posto.
        var engine = _bus.Document.TextEngine;
        _textDefaults = _textDefaults with { Family = engine?.DefaultFamily ?? "Sans" };
        FontCombo.ItemsSource = engine?.Families.ToList() ?? [];
        FontCombo.SelectionChanged += (_, _) => { if (!_syncingText && FontCombo.SelectedItem is string f) ApplyTextStyle(family: f); };
        FontSizeBox.ValueChanged += (_, e) => { if (!_syncingText && e.NewValue is { } v) ApplyTextStyle(size: (double)v); };
        BoldButton.Click += (_, _) => ApplyTextStyle(bold: BoldButton.IsChecked == true);
        ItalicButton.Click += (_, _) => ApplyTextStyle(italic: ItalicButton.IsChecked == true);
        AlignLeftButton.Click += (_, _) => ApplyTextStyle(align: "left");
        AlignCenterButton.Click += (_, _) => ApplyTextStyle(align: "center");
        AlignRightButton.Click += (_, _) => ApplyTextStyle(align: "right");
        TextToCurvesButton.Click += (_, _) => ToCurves();
        Canvas.TextEditRequested += BeginTextEdit;
        TextEditor.LostFocus += (_, _) => CommitTextEdit();
        TextEditor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelTextEdit(); e.Handled = true; }
            else if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            {
                CommitTextEdit();
                Canvas.Focus();
                e.Handled = true;
            }
        };
        NodeLineButton.Click += (_, _) => Canvas.ApplyNodeOp(NodeOp.ToLine);
        NodeCurveButton.Click += (_, _) => Canvas.ApplyNodeOp(NodeOp.ToCurve);
        NodeCuspButton.Click += (_, _) => Canvas.ApplyNodeOp(NodeOp.Cusp);
        NodeSmoothButton.Click += (_, _) => Canvas.ApplyNodeOp(NodeOp.Smooth);
        NodeSymmetricButton.Click += (_, _) => Canvas.ApplyNodeOp(NodeOp.Symmetric);
        NodeDeleteButton.Click += (_, _) => Canvas.ApplyNodeOp(NodeOp.Delete);
        ToCurvesButton.Click += (_, _) => ToCurves();
        Canvas.ToolChanged += SyncToolButtons;

        // Tavolozza: stile delle nuove forme e, se c'è una selezione, dell'oggetto selezionato.
        BuildSwatches(PaletteSwatches, (c, stroke) =>
        {
            if (stroke) ApplyStyle(stroke: c);
            else if (c != "none" && Canvas.TrySetSelectedStopColor(c)) { } // sosta selezionata con lo strumento di riempimento
            else if (_gradArmed >= 0 && c != "none") PickGradientColor(c); // un colore del pannello è "in attesa"
            else ApplyStyle(fill: c);
        });

        // Riempimento: tinta unita, gradiente con più colori o motivo. Per gradienti particolari si può scrivere il testo CSS nel campo Fill.
        GradSolidButton.Click += (_, _) => SetFillKind(0);
        GradLinearButton.Click += (_, _) => SetFillKind(1);
        GradRadialButton.Click += (_, _) => SetFillKind(2);
        GradPatternButton.Click += (_, _) => SetFillKind(3);
        GradAddButton.Click += (_, _) => AddStop();
        GradSwapButton.Click += (_, _) =>
        {
            if (_fillKind is 1 or 2) _spec = _spec with { Stops = _spec.Stops.Select(s => s with { Position = 1 - s.Position }).Reverse().ToList() };
            ApplyFillFromPanel();
        };
        GradAngleBox.ValueChanged += (_, e) =>
        {
            if (_syncingGrad || e.NewValue is not { } v) return;
            _spec = _spec with { Angle = (double)v, X1 = null, Y1 = null, X2 = null, Y2 = null }; // l'angolo sostituisce i punti espliciti
            if (_fillKind == 1) ApplyFillFromPanel();
        };
        PatKindCombo.ItemsSource = Pattern.Kinds.ToList();
        PatKindCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingGrad || PatKindCombo.SelectedItem is not string k) return;
            _pat = _pat with { Kind = k };
            if (_fillKind == 3) ApplyFillFromPanel();
        };
        PatSizeBox.ValueChanged += (_, e) =>
        {
            if (_syncingGrad || e.NewValue is not { } v) return;
            _pat = _pat with { Size = (double)v };
            if (_fillKind == 3) ApplyFillFromPanel();
        };
        PatAngleBox.ValueChanged += (_, e) =>
        {
            if (_syncingGrad || e.NewValue is not { } v) return;
            _pat = _pat with { Angle = (double)v };
            if (_fillKind == 3) ApplyFillFromPanel();
        };

        // Strumento di riempimento sul canvas.
        FillTool.Click += (_, _) => { Canvas.Tool = Tool.Fill; SyncToolButtons(); };
        Canvas.DefaultStops = () => _fillKind is 1 or 2
            ? _spec.Stops.ToList()
            : [new(_fillKind == 3 ? _pat.Fg : _solid, 0), new(_fillKind == 3 ? _pat.Bg : _spec.Stops[^1].Color, 1)];
        FillHex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyStyle(fill: NormalizeHex(FillHex.Text)); };
        StrokeHex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyStyle(stroke: NormalizeHex(StrokeHex.Text)); };
        WidthBox.ValueChanged += (_, e) =>
        {
            if (!_syncing && e.NewValue is { } w) ApplyStyle(width: (double)w);
        };

        KeyDown += OnKeyDown;
        Refresh();
    }

    static readonly string[] Palette =
    [
        "#000000", "#FFFFFF", "#6B7280", "#EF4444", "#F97316", "#FFC800",
        "#22C55E", "#14B8A6", "#3B82F6", "#6366F1", "#A855F7", "#EC4899", "none",
    ];

    /// <summary>Come negli editor vettoriali classici: click sinistro = fill, click destro = stroke.</summary>
    static void BuildSwatches(WrapPanel panel, Action<string, bool> pick)
    {
        foreach (var c in Palette)
        {
            var b = new Button
            {
                Width = 24, Height = 24, Margin = new(0, 0, 6, 6), Padding = new(0), CornerRadius = new(12),
                BorderBrush = new SolidColorBrush(Color.Parse("#80808080")), BorderThickness = new(1),
                Background = c == "none" ? Brushes.Transparent : new SolidColorBrush(Color.Parse(c)),
                Content = c == "none"
                    ? new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M5,19 L19,5"), Stroke = Brushes.Red, StrokeThickness = 2, Width = 24, Height = 24, Stretch = Stretch.None }
                    : null,
            };
            ToolTip.SetTip(b, c);
            // Button gestisce solo il click sinistro: ascolto il rilascio per distinguere i due tasti.
            b.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                if (!new Rect(b.Bounds.Size).Contains(e.GetPosition(b))) return;
                if (e.InitialPressMouseButton == MouseButton.Left) pick(c, false);
                else if (e.InitialPressMouseButton == MouseButton.Right) pick(c, true);
            }, handledEventsToo: true);
            panel.Children.Add(b);
        }
    }

    // Accetta "#RRGGBB", "RRGGBB" e "none"; il resto viene rifiutato dal comando con un errore esplicito.
    static string NormalizeHex(string? t)
    {
        t = (t ?? "").Trim();
        return t.Length is 6 or 8 && !t.StartsWith('#') && t.All(Uri.IsHexDigit) ? "#" + t : t;
    }

    void SyncToolButtons()
    {
        SelectTool.IsChecked = Canvas.Tool == Tool.Select;
        RectTool.IsChecked = Canvas.Tool == Tool.Rectangle;
        EllipseTool.IsChecked = Canvas.Tool == Tool.Ellipse;
        PenTool.IsChecked = Canvas.Tool == Tool.Pen;
        TextTool.IsChecked = Canvas.Tool == Tool.Text;
        FillTool.IsChecked = Canvas.Tool == Tool.Fill;
        NodeTool.IsChecked = Canvas.Tool == Tool.Node;
        // Con lo strumento Nodi la barra mostra le operazioni sui nodi al posto di quelle booleane.
        UpdateBars();
    }

    /// <summary>Aggiorna lo stile corrente (nuove forme) e lo applica alla selezione tramite il bus.</summary>
    void ApplyStyle(string? fill = null, string? stroke = null, double? width = null)
    {
        if (fill != null) Canvas.Fill = fill == "none" ? null : fill;
        if (stroke != null) Canvas.Stroke = stroke == "none" ? null : stroke;
        if (width != null) Canvas.StrokeWidth = width.Value;

        if (Canvas.SelectedIds.Count == 0) return;
        try
        {
            _bus.Execute([new SetStyle { Targets = Canvas.SelectedIds.ToList(), Fill = fill, Stroke = stroke, StrokeWidth = width }]);
        }
        catch (CommandException)
        {
            Refresh(); // colore non valido: ripristina i campi dall'oggetto
        }
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return; // Backspace/lettere nei campi di testo non sono scorciatoie
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (cmd && e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _bus.Redo(); else _bus.Undo();
            e.Handled = true;
        }
        else if (cmd && e.Key == Key.Y)
        {
            _bus.Redo();
            e.Handled = true;
        }
        else if (cmd && e.Key == Key.G)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Ungroup(); else Group();
            e.Handled = true;
        }
        else if (cmd && e.Key == Key.Q)
        {
            ToCurves();
            e.Handled = true;
        }
        else if (!cmd && e.Key is Key.V or Key.R or Key.E or Key.P or Key.N or Key.F10 or Key.T or Key.G)
        {
            Canvas.Tool = e.Key switch
            {
                Key.R => Tool.Rectangle, Key.E => Tool.Ellipse, Key.P => Tool.Pen, Key.N or Key.F10 => Tool.Node, Key.T => Tool.Text, Key.G => Tool.Fill, _ => Tool.Select,
            };
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Canvas.PenActive)
        {
            Canvas.FinishPen(false); // termina il path aperto
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.F2 && Canvas.SelectedId != null)
        {
            Canvas.EditSelectedText(); // Invio o F2 su un testo selezionato: lo si modifica
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Canvas.PenActive)
        {
            Canvas.CancelPen();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && Canvas.PenActive)
        {
            Canvas.PenBack(); // durante il disegno toglie l'ultimo nodo
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && Canvas.Tool == Tool.Node && Canvas.NodeSelectionCount > 0)
        {
            Canvas.ApplyNodeOp(NodeOp.Delete); // con lo strumento Nodi Delete toglie i nodi, non l'oggetto
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && Canvas.Tool == Tool.Fill && Canvas.DeleteSelectedStop())
        {
            e.Handled = true; // con lo strumento di riempimento Delete toglie la sosta di colore selezionata
        }
        else if (e.Key is Key.Delete or Key.Back && Canvas.SelectedIds.Count > 0)
        {
            _bus.Execute([new DeleteObjects { Targets = Canvas.SelectedIds.ToList() }]);
            e.Handled = true;
        }
    }

    void UpdateScrollBars()
    {
        var s = Canvas.ScrollInfo();
        _syncingScroll = true;
        HScroll.Maximum = Math.Max(0, s.TotalW - s.ViewW);
        HScroll.ViewportSize = s.ViewW;
        HScroll.Value = Math.Clamp(s.OffX, 0, HScroll.Maximum);
        VScroll.Maximum = Math.Max(0, s.TotalH - s.ViewH);
        VScroll.ViewportSize = s.ViewH;
        VScroll.Value = Math.Clamp(s.OffY, 0, VScroll.Maximum);
        _syncingScroll = false;
    }

    // ---- Testo ----------------------------------------------------------------------------------

    /// <summary>Barra in alto: nodi con lo strumento Nodi, stile con lo strumento Testo o un testo selezionato, altrimenti booleane.</summary>
    void UpdateBars()
    {
        var node = Canvas.Tool == Tool.Node;
        var text = !node && (Canvas.Tool == Tool.Text || SelectedTextStyle() != null);
        NodeBar.IsVisible = node;
        TextBar.IsVisible = text;
        BoolBar.IsVisible = !node && !text;
    }

    /// <summary>Stile del testo selezionato (se è un solo testo).</summary>
    TextStyle? SelectedTextStyle() =>
        Canvas.SelectedId is { } id ? _bus.Read(d => (d.Objects.FirstOrDefault(o => o.Id == id) as TextObject)?.Style) : null;

    void SyncTextBar()
    {
        var st = SelectedTextStyle() ?? _textDefaults;
        _syncingText = true;
        FontCombo.SelectedItem = (FontCombo.ItemsSource as List<string>)?.FirstOrDefault(f => f.Equals(st.Family, StringComparison.OrdinalIgnoreCase));
        FontSizeBox.Value = (decimal)st.Size;
        BoldButton.IsChecked = st.Bold;
        ItalicButton.IsChecked = st.Italic;
        AlignLeftButton.IsChecked = st.Align == "left";
        AlignCenterButton.IsChecked = st.Align == "center";
        AlignRightButton.IsChecked = st.Align == "right";
        _syncingText = false;
    }

    // Cambia lo stile dei testi nuovi e applica la modifica ai testi selezionati (un solo undo).
    void ApplyTextStyle(string? family = null, double? size = null, bool? bold = null, bool? italic = null, string? align = null)
    {
        _textDefaults = _textDefaults with
        {
            Family = family ?? _textDefaults.Family, Size = size ?? _textDefaults.Size, Bold = bold ?? _textDefaults.Bold,
            Italic = italic ?? _textDefaults.Italic, Align = align ?? _textDefaults.Align,
        };
        var ids = _bus.Read(d => Canvas.SelectedIds.Where(id => d.Objects.FirstOrDefault(o => o.Id == id) is TextObject).ToList());
        if (ids.Count > 0)
        {
            try { _bus.Execute([new SetText { Targets = ids, FontFamily = family, FontSize = size, Bold = bold, Italic = italic, Align = align }]); }
            catch (CommandException ex) { ShowNotice(ex.Message); }
        }
        SyncTextBar();
    }

    // Editor sul posto: una TextBox sopra il canvas, con lo stesso font; l'oggetto originale resta nascosto.
    void BeginTextEdit(TextEditRequest r)
    {
        CommitTextEdit();
        var style = _textDefaults;
        string text = "", fill = Canvas.Fill ?? "#000000";
        double? wrap = r.WrapWidth;
        Point at;
        double rotation = 0;

        if (r.Id is { } id)
        {
            var info = _bus.Read(d => d.Objects.FirstOrDefault(o => o.Id == id) is TextObject t ? (t.Text, t.Style, t.Fill, t.WrapWidth) : ((string, TextStyle, string?, double?)?)null);
            if (info is not { } i || Canvas.EditorPlacement(id) is not { } place) return;
            (text, style, wrap) = (i.Item1, i.Item2, i.Item4);
            fill = i.Item3 ?? "#000000";
            (at, rotation) = (place.TopLeft, place.Rotation);
            Canvas.HiddenId = id;
        }
        else at = Canvas.DocToScreen(r.At);

        _edit = r;
        _editOriginal = text;
        var zoom = Canvas.Zoom;
        TextEditor.Text = text;
        TextEditor.FontFamily = new FontFamily(style.Family);
        TextEditor.FontSize = style.Size * zoom;
        TextEditor.FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal;
        TextEditor.FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal;
        TextEditor.TextAlignment = style.Align switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left };
        TextEditor.Foreground = Color.TryParse(fill.Length == 7 ? fill : "#000000", out var c) ? new SolidColorBrush(c) : Brushes.Black;
        TextEditor.TextWrapping = wrap != null ? TextWrapping.Wrap : TextWrapping.NoWrap;
        TextEditor.Width = wrap != null ? wrap.Value * zoom + 2 : double.NaN;
        TextEditor.Margin = new Thickness(at.X - 1, at.Y - 1, 0, 0);
        TextEditor.RenderTransformOrigin = RelativePoint.TopLeft;
        TextEditor.RenderTransform = Math.Abs(rotation) > 0.01 ? new RotateTransform(rotation) : null;
        TextEditor.IsVisible = true;
        TextEditor.Focus();
        if (r.Id != null) TextEditor.SelectAll(); else TextEditor.CaretIndex = text.Length;
    }

    void CancelTextEdit()
    {
        _edit = null;
        TextEditor.IsVisible = false;
        Canvas.HiddenId = null;
        Canvas.Focus();
    }

    void CommitTextEdit()
    {
        if (_edit is not { } e) return;
        _edit = null; // prima di nascondere l'editor: nascondendolo scatta LostFocus
        var text = TextEditor.Text ?? "";
        TextEditor.IsVisible = false;
        Canvas.HiddenId = null;
        try
        {
            if (e.Id is { } id)
            {
                if (text.Length == 0) _bus.Execute([new DeleteObjects { Targets = [id] }]); // un testo svuotato sparisce
                else if (text != _editOriginal) _bus.Execute([new SetText { Targets = [id], Text = text }]);
            }
            else if (text.Trim().Length > 0)
            {
                var s = _textDefaults;
                var r = _bus.Execute([new CreateShape
                {
                    Type = "text", Text = text, X = e.At.X, Y = e.At.Y, Width = e.WrapWidth ?? 0,
                    FontFamily = s.Family, FontSize = s.Size, Bold = s.Bold, Italic = s.Italic, Align = s.Align,
                    Fill = Canvas.Fill ?? "#000000",
                }]);
                Canvas.Select(r.CreatedIds[0]);
                Canvas.Tool = Tool.Select; // dopo aver scritto si torna alla selezione
                SyncToolButtons();
            }
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    // ---- Riempimento (tinta unita, gradiente, motivo) -----------------------------------------

    string BuildFill() => _fillKind switch
    {
        1 => Gradient.Format(_spec with { Radial = false }),
        2 => Gradient.Format(_spec with { Radial = true, X1 = null, Y1 = null, X2 = null, Y2 = null }),
        3 => Pattern.Format(_pat),
        _ => _solid,
    };

    void ApplyFillFromPanel()
    {
        _gradArmed = -1;
        ApplyStyle(fill: BuildFill());
    }

    // Primo e ultimo colore del riempimento attuale: passando da un tipo all'altro i colori restano.
    (string First, string Last) FillColors() => _fillKind switch
    {
        1 or 2 => (_spec.Stops[0].Color, _spec.Stops[^1].Color),
        3 => (_pat.Fg, _pat.Bg),
        _ => (_solid, _spec.Stops[^1].Color),
    };

    void SetFillKind(int kind)
    {
        var (first, last) = FillColors();
        switch (kind)
        {
            case 0: _solid = first; break;
            case 1 or 2:
                if (_fillKind is 0 or 3) _spec = _spec with { Stops = [new(first, 0), new(last, 1)] };
                break;
            case 3: _pat = _pat with { Fg = first, Bg = last }; break;
        }
        _fillKind = kind;
        ApplyFillFromPanel();
    }

    void AddStop()
    {
        if (_fillKind is not (1 or 2)) return;
        var st = _spec.Stops;
        var gap = Enumerable.Range(0, st.Count - 1).MaxBy(i => st[i + 1].Position - st[i].Position);
        var t = (st[gap].Position + st[gap + 1].Position) / 2;
        var list = st.ToList();
        list.Insert(gap + 1, new GradientStop(Gradient.ColorAt(st, t), t));
        _spec = _spec with { Stops = list };
        ApplyFillFromPanel();
    }

    void PickGradientColor(string color)
    {
        var i = _gradArmed;
        _gradArmed = -1;
        switch (_fillKind)
        {
            case 1 or 2 when i < _spec.Stops.Count:
                var st = _spec.Stops.ToList();
                st[i] = st[i] with { Color = color.ToUpperInvariant() };
                _spec = _spec with { Stops = st };
                break;
            case 3: _pat = i == 0 ? _pat with { Fg = color.ToUpperInvariant() } : _pat with { Bg = color.ToUpperInvariant() }; break;
            default: _solid = color; break;
        }
        ApplyFillFromPanel();
    }

    // Colore del documento ("#RRGGBB" o "#RRGGBBAA") come colore Avalonia.
    static Color DocColor(string s)
    {
        var v = Convert.ToUInt32(s[1..], 16);
        return s.Length == 7 ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v) : Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8));
    }

    void SyncFillPanel(string? fill)
    {
        _syncingGrad = true;
        if (fill != null && Pattern.TryParse(fill, out var pat)) { _fillKind = 3; _pat = pat; }
        else if (fill != null && Gradient.TryParse(fill, out var g)) { _fillKind = g.Radial ? 2 : 1; _spec = g; }
        else
        {
            _fillKind = 0;
            if (fill is { Length: 7 or 9 } && fill[0] == '#') _solid = fill;
        }
        _gradArmed = -1;
        UpdateFillUi();
        _syncingGrad = false;
    }

    void UpdateFillUi()
    {
        var prev = _syncingGrad;
        _syncingGrad = true;
        GradSolidButton.IsChecked = _fillKind == 0;
        GradLinearButton.IsChecked = _fillKind == 1;
        GradRadialButton.IsChecked = _fillKind == 2;
        GradPatternButton.IsChecked = _fillKind == 3;
        StopsPanel.IsVisible = _fillKind != 0;
        StopsButtons.IsVisible = _fillKind is 1 or 2;
        GradAngleBox.IsVisible = _fillKind == 1;
        PatternPanel.IsVisible = _fillKind == 3;
        GradHint.IsVisible = _gradArmed >= 0;
        GradAngleBox.Value = (decimal)Math.Round(_spec.Angle, 2);
        PatKindCombo.SelectedItem = _pat.Kind;
        PatSizeBox.Value = (decimal)_pat.Size;
        PatAngleBox.Value = (decimal)_pat.Angle;
        RebuildStopRows();
        GradPreview.Background = PreviewBrush();
        _syncingGrad = prev;
    }

    // Una riga per ogni colore: campione (si clicca e poi si sceglie dalla tavolozza), posizione in % e rimozione.
    void RebuildStopRows()
    {
        StopsPanel.Children.Clear();
        if (_fillKind is 1 or 2)
            for (var i = 0; i < _spec.Stops.Count; i++) StopsPanel.Children.Add(StopRow(i, null, _spec.Stops[i].Color, _spec.Stops[i].Position, _spec.Stops.Count > 2));
        else if (_fillKind == 3)
        {
            StopsPanel.Children.Add(StopRow(0, "Pattern", _pat.Fg, null, false));
            StopsPanel.Children.Add(StopRow(1, "Background", _pat.Bg, null, false));
        }
    }

    Control StopRow(int index, string? label, string color, double? position, bool removable)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        var chip = new Button
        {
            Width = 40, Height = 26, Padding = new(0), BorderThickness = new(2),
            Background = new SolidColorBrush(DocColor(color)),
            BorderBrush = _gradArmed == index ? new SolidColorBrush(Color.Parse("#3B82F6")) : Brushes.Gray,
        };
        ToolTip.SetTip(chip, "Click, then pick a color from the palette");
        chip.Click += (_, _) => { _gradArmed = index; UpdateFillUi(); };
        row.Children.Add(chip);
        if (label != null) row.Children.Add(new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontSize = 12, Opacity = 0.7 });
        if (position is { } p)
        {
            var box = new NumericUpDown
            {
                Width = 96, Height = 26, Minimum = 0, Maximum = 100, Increment = 5, FormatString = "0.#", Value = (decimal)Math.Round(p * 100, 1),
            };
            box.ValueChanged += (_, e) =>
            {
                if (_syncingGrad || e.NewValue is not { } v) return;
                var st = _spec.Stops.ToList();
                st[index] = st[index] with { Position = (double)v / 100 };
                _spec = _spec with { Stops = st.OrderBy(x => x.Position).ToList() };
                ApplyFillFromPanel();
            };
            row.Children.Add(box);
        }
        if (removable)
        {
            var remove = new Button { Content = "✕", Width = 26, Height = 26, Padding = new(0) };
            ToolTip.SetTip(remove, "Remove this color");
            remove.Click += (_, _) =>
            {
                var st = _spec.Stops.ToList();
                st.RemoveAt(index);
                _spec = _spec with { Stops = st };
                ApplyFillFromPanel();
            };
            row.Children.Add(remove);
        }
        return row;
    }

    // Anteprima nel pannello, con la stessa geometria del documento (punti e raggi come frazioni del riquadro).
    IBrush PreviewBrush()
    {
        var box = new Box(0, 0, 200, 22);
        var stops = new GradientStops();
        switch (_fillKind)
        {
            case 1:
            {
                foreach (var s in _spec.Stops) stops.Add(new Avalonia.Media.GradientStop(DocColor(s.Color), s.Position));
                var (x1, y1, x2, y2) = Gradient.LinearPoints(_spec with { Radial = false }, box);
                return new LinearGradientBrush
                {
                    GradientStops = stops, StartPoint = new RelativePoint(x1, y1, RelativeUnit.Relative), EndPoint = new RelativePoint(x2, y2, RelativeUnit.Relative),
                };
            }
            case 2:
            {
                foreach (var s in _spec.Stops) stops.Add(new Avalonia.Media.GradientStop(DocColor(s.Color), s.Position));
                var (rx, ry) = Gradient.RadialRadii(_spec with { Radial = true }, box);
                var centre = new RelativePoint(_spec.CenterX, _spec.CenterY, RelativeUnit.Relative);
                return new RadialGradientBrush
                {
                    GradientStops = stops, Center = centre, GradientOrigin = centre,
                    RadiusX = new RelativeScalar(rx, RelativeUnit.Relative), RadiusY = new RelativeScalar(ry, RelativeUnit.Relative),
                };
            }
            case 3: // motivo: metà colore del motivo, metà sfondo
                stops.Add(new Avalonia.Media.GradientStop(DocColor(_pat.Fg), 0));
                stops.Add(new Avalonia.Media.GradientStop(DocColor(_pat.Fg), 0.5));
                stops.Add(new Avalonia.Media.GradientStop(DocColor(_pat.Bg), 0.5));
                stops.Add(new Avalonia.Media.GradientStop(DocColor(_pat.Bg), 1));
                return new LinearGradientBrush { GradientStops = stops, StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative) };
            default:
                return new SolidColorBrush(DocColor(_solid));
        }
    }

    void ToCurves()
    {
        if (Canvas.SelectedIds.Count == 0) return;
        try
        {
            _bus.Execute([new ConvertToPath { Targets = Canvas.SelectedIds.ToList() }]);
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    void ShowNotice(string message)
    {
        NoticeText.Text = message.Replace("shape.boolean: ", "").Replace("shape.toPath: ", "");
        NoticeText.IsVisible = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    void RunBoolean(string op)
    {
        try
        {
            var r = _bus.Execute([new BooleanOp { Op = op, Targets = Canvas.SelectedIds.ToList() }]);
            Canvas.Select(r.CreatedIds[0]);
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    void Group()
    {
        var ids = Canvas.SelectedIds.ToList();
        if (ids.Count < 2) return;
        try
        {
            var r = _bus.Execute([new GroupObjects { Targets = ids }]);
            Canvas.Select(r.CreatedIds[0]);
        }
        catch (CommandException) { }
    }

    void Ungroup()
    {
        // Gli id dei figli vanno letti prima: dopo il comando il gruppo non esiste più.
        var groups = _bus.Read(d => Canvas.SelectedIds
            .Select(id => d.Objects.FirstOrDefault(o => o.Id == id)).OfType<GroupObject>()
            .Select(g => (g.Id, Children: g.Children.Select(c => c.Id).ToList())).ToList());
        if (groups.Count == 0) return;
        _bus.Execute([new UngroupObjects { Targets = groups.Select(g => g.Id).ToList() }]);
        Canvas.SetSelection(groups.SelectMany(g => g.Children));
    }

    /// <summary>Scopre cosa è cambiato dall'ultima volta e segnala il lavoro dell'agente.</summary>
    void OnBusChanged()
    {
        var (version, changes) = _bus.GetChanges(_seenVersion);
        _seenVersion = version;

        foreach (var change in changes)
        {
            var ids = change.Command.AffectedIds.ToList();
            if (!change.Author.StartsWith("llm:"))
            {
                _aiTouched.ExceptWith(ids);
                continue;
            }
            _aiTouched.UnionWith(ids);
            Canvas.Highlight(ids);
            AgentText.Text = $"{change.Author[4..]} is drawing";
            AgentChip.IsVisible = true;
            _chipTimer.Stop();
            _chipTimer.Start();
        }
        Refresh();
    }

    /// <summary>Riallinea elenco oggetti e canvas al documento (thread UI).</summary>
    void Refresh()
    {
        var items = _bus.Read(d => d.Objects.Reverse().Select(o => (o.Id, Label: $"{o.Name ?? (o is TextObject tx ? "“" + (tx.Text.Length > 24 ? tx.Text[..24].Replace('\n', ' ') + "…" : tx.Text.Replace('\n', ' ')) + "”" : o.Type)}   ·   {o.Id}{(Document.SelfAndDescendants(o).Any(x => _aiTouched.Contains(x.Id)) ? "   ·   AI" : "")}")).ToList());
        _ids = items.Select(i => i.Id).ToList();

        Canvas.PruneSelection(_ids);

        _syncing = true;
        ObjectList.ItemsSource = items.Select(i => i.Label).ToList();
        ObjectList.SelectedIndex = Canvas.SelectedId is { } s ? _ids.IndexOf(s) : -1;

        // Il pannello mostra lo stile dell'oggetto selezionato (e diventa lo stile delle nuove forme).
        if (Canvas.SelectedId is { } cur &&
            _bus.Read(d => d.Objects.FirstOrDefault(o => o.Id == cur)) is { } obj)
        {
            Canvas.Fill = obj.Fill; Canvas.Stroke = obj.Stroke; Canvas.StrokeWidth = obj.StrokeWidth;
            FillHex.Text = obj.Fill ?? "none";
            StrokeHex.Text = obj.Stroke ?? "none";
            WidthBox.Value = (decimal)obj.StrokeWidth;
            SyncFillPanel(obj.Fill);
        }
        else SyncFillPanel(Canvas.Fill);
        _syncing = false;

        var many = Canvas.SelectedIds.Count >= 2;
        WeldButton.IsEnabled = TrimButton.IsEnabled = IntersectButton.IsEnabled = SubtractButton.IsEnabled = many;

        Canvas.InvalidateVisual();
        UpdateScrollBars(); // il contenuto può essere cambiato
        UpdateBars();
        SyncTextBar();
    }
}
