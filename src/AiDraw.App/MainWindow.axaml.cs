using AiDraw.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AiDraw.App;

public partial class MainWindow : Window
{
    readonly CommandBus _bus = App.Bus;
    List<string> _ids = [];
    bool _syncing, _syncingScroll;
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
        NodeLineButton.Click += (_, _) => Canvas.ApplyNodeOp(NodeOp.ToLine);
        NodeCurveButton.Click += (_, _) => Canvas.ApplyNodeOp(NodeOp.ToCurve);
        NodeSmoothButton.Click += (_, _) => Canvas.ApplyNodeOp(NodeOp.Smooth);
        NodeDeleteButton.Click += (_, _) => Canvas.ApplyNodeOp(NodeOp.Delete);
        ToCurvesButton.Click += (_, _) => ToCurves();
        Canvas.ToolChanged += SyncToolButtons;

        // Tavolozza: stile delle nuove forme e, se c'è una selezione, dell'oggetto selezionato.
        BuildSwatches(PaletteSwatches, (c, stroke) =>
        {
            if (stroke) ApplyStyle(stroke: c); else ApplyStyle(fill: c);
        });
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
        NodeTool.IsChecked = Canvas.Tool == Tool.Node;
        // Con lo strumento Nodi la barra mostra le operazioni sui nodi al posto di quelle booleane.
        NodeBar.IsVisible = Canvas.Tool == Tool.Node;
        BoolBar.IsVisible = Canvas.Tool != Tool.Node;
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
        else if (!cmd && e.Key is Key.V or Key.R or Key.E or Key.P or Key.N or Key.F10)
        {
            Canvas.Tool = e.Key switch
            {
                Key.R => Tool.Rectangle, Key.E => Tool.Ellipse, Key.P => Tool.Pen, Key.N or Key.F10 => Tool.Node, _ => Tool.Select,
            };
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Canvas.PenActive)
        {
            Canvas.FinishPen(false); // termina il path aperto
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
        var items = _bus.Read(d => d.Objects.Reverse().Select(o => (o.Id, Label: $"{o.Name ?? o.Type}   ·   {o.Id}{(Document.SelfAndDescendants(o).Any(x => _aiTouched.Contains(x.Id)) ? "   ·   AI" : "")}")).ToList());
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
        }
        _syncing = false;

        var many = Canvas.SelectedIds.Count >= 2;
        WeldButton.IsEnabled = TrimButton.IsEnabled = IntersectButton.IsEnabled = SubtractButton.IsEnabled = many;

        Canvas.InvalidateVisual();
        UpdateScrollBars(); // il contenuto può essere cambiato
    }
}
