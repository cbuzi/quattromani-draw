using Avalonia.Input.Platform;
using Quattromani.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GradientStop = Quattromani.Core.GradientStop;
using Matrix3x2 = System.Numerics.Matrix3x2;
using Vector2 = System.Numerics.Vector2;

namespace Quattromani.App;

public partial class MainWindow : Window
{
    readonly CommandBus _bus = App.Bus;
    List<string> _ids = [];
    bool _syncing, _syncingScroll, _syncingText, _syncingGrad, _syncingPage, _fitted;
    string _unit = "px";
    string? _renamingId;
    string? _liveInfo; // measures of the gesture in progress: they take precedence over the selection's information
    (double W, double H) _lastPage;
    // "FILL TYPE" panel: 0 = solid, 1 = linear, 2 = radial, 3 = pattern. _gradArmed = which color is about to be chosen from the palette.
    int _fillKind, _gradArmed = -1;
    string _solid = "#3B82F6";
    GradientSpec _spec = new(false, 90, false, 0.5, 0.5, [new("#3B82F6", 0), new("#FFFFFF", 1)]);
    PatternSpec _pat = new("stripes", "#3B82F6", "#FFFFFF", 10, 0);
    TextEditRequest? _edit;
    string _editOriginal = "";
    TextStyle _textDefaults = new("", 24, false, false, "left", 1); // style of new texts; the family is set at startup
    long _seenVersion;
    readonly HashSet<string> _aiTouched = []; // objects whose last modification is by the agent
    readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _chipTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly List<string> _activeAgents = []; // agents that intervened since the chip last expired

    /// <summary>Canvas selection for MCP.</summary>
    public Quattromani.Mcp.ISelectionService Selection { get; }

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            // The Metal layer appears after the first redraw: until it is there we check often, to reduce the time with the wrong colorspace.
            DispatcherTimer? t = null;
            t = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Send, (_, _) =>
            {
                if (MacColorSpace.Apply(this)) t!.Interval = TimeSpan.FromSeconds(1);
            });
            t.Start();
        };
        Selection = new CanvasSelection(Canvas);
        InitFiles();
        InitLayers();
        InitAgents();
        InitStrokeStyle();
        Canvas.Bus = _bus;

        // The bus may notify from any thread.
        _seenVersion = _bus.Version;
        _bus.Changed += () => Dispatcher.UIThread.Post(OnBusChanged);
        _chipTimer.Tick += (_, _) => { _chipTimer.Stop(); AgentChip.IsVisible = false; _activeAgents.Clear(); };
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

        // Page, units of measure and status bar.
        Canvas.FormatLength = Fmt;
        Canvas.UnitLabel = _unit;
        Canvas.InfoChanged += text => { _liveInfo = text; UpdateStatus(); };
        Canvas.CursorMoved += p => CursorText.Text = p is { } q ? $"x {Fmt(q.X)}   y {Fmt(q.Y)}" : "";
        UnitCombo.ItemsSource = Units.All.ToList();
        UnitCombo.SelectedItem = _unit;
        UnitCombo.SelectionChanged += (_, _) =>
        {
            if (UnitCombo.SelectedItem is not string u || u == _unit) return;
            _unit = u;
            Canvas.UnitLabel = u;
            SetGridDefault();
            SyncPageBar();
            UpdateStatus();
            Canvas.InvalidateVisual(); // the rulers change unit
        };

        // Grid and snapping.
        GridToggle.IsCheckedChanged += (_, _) => Canvas.ShowGrid = GridToggle.IsChecked == true;
        SnapToggle.IsCheckedChanged += (_, _) => Canvas.SnapOn = SnapToggle.IsChecked == true;
        SnapGridCheck.IsCheckedChanged += (_, _) => Canvas.SnapToGrid = SnapGridCheck.IsChecked == true;
        SnapGuidesCheck.IsCheckedChanged += (_, _) => Canvas.SnapToGuides = SnapGuidesCheck.IsChecked == true;
        SnapObjectsCheck.IsCheckedChanged += (_, _) => Canvas.SnapToObjects = SnapObjectsCheck.IsChecked == true;
        GridSizeBox.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v && v > 0) { Canvas.GridSize = Units.ToPx((double)v, _unit); Canvas.InvalidateVisual(); }
        };
        Canvas.ShowGrid = GridToggle.IsChecked == true;
        Canvas.SnapOn = SnapToggle.IsChecked == true;
        SetGridDefault();
        PageViewCombo.ItemsSource = new[] { "Page: white", "Page: outline", "Page: hidden" };
        PageViewCombo.SelectedIndex = 1; // by default only the page outline
        PageViewCombo.SelectionChanged += (_, _) => Canvas.PageViewMode = (PageView)Math.Max(0, PageViewCombo.SelectedIndex);
        FitPageButton.Click += (_, _) => Canvas.FitPage();
        Canvas.SizeChanged += (_, _) =>
        {
            if (!_fitted && Canvas.Bounds.Width > 200) { _fitted = true; Canvas.FitPage(); } // on first opening the whole page is visible
        };

        PagePresetCombo.ItemsSource = PageSizes.All.Select(p => p.Label).Append("Custom").ToList();
        PagePresetCombo.SelectionChanged += (_, _) =>
        {
            var i = PagePresetCombo.SelectedIndex;
            if (_syncingPage || i < 0 || i >= PageSizes.All.Length) return;
            var landscape = _lastPage.W > _lastPage.H; // the chosen format keeps the current orientation
            ExecutePage(new SetPage { Preset = PageSizes.All[i].Name, Orientation = landscape ? "landscape" : "portrait" });
        };
        PageWBox.ValueChanged += (_, e) =>
        {
            if (!_syncingPage && e.NewValue is { } v) ExecutePage(new SetPage { Width = (double)v, Unit = _unit });
        };
        PageHBox.ValueChanged += (_, e) =>
        {
            if (!_syncingPage && e.NewValue is { } v) ExecutePage(new SetPage { Height = (double)v, Unit = _unit });
        };
        PortraitButton.Click += (_, _) => ExecutePage(new SetPage { Orientation = "portrait" });
        LandscapeButton.Click += (_, _) => ExecutePage(new SetPage { Orientation = "landscape" });

        // Object names: click on an already selected entry (or double-click) to rename it.
        ObjectList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(ObjectList).Properties.IsLeftButtonPressed) return;
            var item = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
            if (item == null) return;
            var index = ObjectList.IndexFromContainer(item);
            // Tunnel: here the selection is still the one before the click.
            if (index >= 0 && (e.ClickCount >= 2 || ObjectList.SelectedIndex == index && Canvas.SelectedId == _ids.ElementAtOrDefault(index)))
                Dispatcher.UIThread.Post(() => BeginRename(index), DispatcherPriority.Background);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        RenameBox.LostFocus += (_, _) => CommitRename();
        RenameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitRename(); Canvas.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _renamingId = null; RenameBox.IsVisible = false; Canvas.Focus(); e.Handled = true; }
        };

        UndoButton.Click += (_, _) => _bus.Undo();
        RedoButton.Click += (_, _) => _bus.Redo();
        LogoButton.Click += (_, _) => App.ShowAbout();
        ThemeButton.Click += (_, _) => Application.Current!.RequestedThemeVariant =
            ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        void SyncThemeIcon()
        {
            var dark = ActualThemeVariant == ThemeVariant.Dark;
            SunIcon.IsVisible = dark;
            MoonIcon.IsVisible = !dark;
        }
        ActualThemeVariantChanged += (_, _) => SyncThemeIcon();
        SyncThemeIcon();

        ObjectList.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            var i = ObjectList.SelectedIndex;
            Canvas.Select(i >= 0 ? _ids[i] : null);
        };

        // Tools
        SelectTool.Click += (_, _) => { Canvas.Tool = Tool.Select; SyncToolButtons(); };
        RectTool.Click += (_, _) => { Canvas.Tool = Tool.Rectangle; SyncToolButtons(); };
        EllipseTool.Click += (_, _) => { Canvas.Tool = Tool.Ellipse; SyncToolButtons(); };
        PenTool.Click += (_, _) => { Canvas.Tool = Tool.Pen; SyncToolButtons(); };
        PencilTool.Click += (_, _) => { Canvas.Tool = Tool.Pencil; SyncToolButtons(); };
        PolygonTool.Click += (_, _) => { Canvas.Tool = Tool.Polygon; SyncToolButtons(); };
        StarTool.Click += (_, _) => { Canvas.Tool = Tool.Star; SyncToolButtons(); };
        LineTool.Click += (_, _) => { Canvas.Tool = Tool.Line; SyncToolButtons(); };
        EyedropperTool.Click += (_, _) => { Canvas.Tool = Tool.Eyedropper; SyncToolButtons(); };
        Canvas.ColorPicked += (color, stroke) => ApplyStyle(fill: stroke ? null : color, stroke: stroke ? color : null);
        SidesBox.ValueChanged += (_, e) =>
        {
            if (e.NewValue is not { } v) return;
            if (Canvas.Tool == Tool.Star) Canvas.StarPoints = (int)v; else Canvas.PolygonSides = (int)v;
        };
        DepthBox.ValueChanged += (_, e) => { if (e.NewValue is { } v) Canvas.StarInner = (double)v / 100; };
        NodeTool.Click += (_, _) => { Canvas.Tool = Tool.Node; SyncToolButtons(); };
        TextTool.Click += (_, _) => { Canvas.Tool = Tool.Text; SyncToolButtons(); };

        // Text: style of new texts, text bar and in-place editor.
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
        FitPathButton.Click += (_, _) => FitTextToPath();
        PathAlignCombo.ItemsSource = new[] { "Start of the path", "Center", "End of the path" };
        PathAlignCombo.SelectionChanged += (_, _) => ApplyOnPath(align: new[] { "start", "center", "end" }[Math.Max(0, PathAlignCombo.SelectedIndex)]);
        PathOffsetBox.ValueChanged += (_, e) => { if (e.NewValue is { } v) ApplyOnPath(offset: Units.ToPx((double)v, _unit)); };
        PathDistanceBox.ValueChanged += (_, e) => { if (e.NewValue is { } v) ApplyOnPath(distance: Units.ToPx((double)v, _unit)); };
        PathReverseCheck.IsCheckedChanged += (_, _) => ApplyOnPath(reverse: PathReverseCheck.IsChecked == true);
        PathDetachButton.Click += (_, _) => { TextPathButton.Flyout?.Hide(); ApplyOnPath(detach: true); };
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
        foreach (var box in new[] { XBox, YBox, WBox, HBox, RotBox }) box.ValueChanged += (_, _) => ApplyXform(box);

        // Palette: style of new shapes and, if there is a selection, of the selected object.
        void PickColor(string c, bool stroke)
        {
            if (stroke) ApplyStyle(stroke: c);
            else if (c != "none" && Canvas.TrySetSelectedStopColor(c)) { } // stop selected with the fill tool
            else if (_gradArmed >= 0 && c != "none") PickGradientColor(c); // a color of the panel is "pending"
            else ApplyStyle(fill: c);
        }
        BuildSwatches(PaletteSwatches, PickColor);
        BuildPaletteGrid(PaletteGrid, PickColor);
        PaletteExpandButton.Click += (_, _) => SetPaletteOpen(!PaletteFlyout.IsVisible);
        // The expanded panel closes by clicking elsewhere (the click still goes to whatever is underneath).
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!PaletteFlyout.IsVisible) return;
            var inside = (e.Source as Visual)?.GetSelfAndVisualAncestors().Any(v => ReferenceEquals(v, PaletteFlyout) || ReferenceEquals(v, PaletteExpandButton)) ?? false;
            if (!inside) SetPaletteOpen(false);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        // Fill: solid color, multi-color gradient or pattern. For special gradients the CSS text can be typed in the Fill field.
        // Stacking order, alignment and distribution.
        var cmdKey = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        MenuToFront.InputGesture = new KeyGesture(Key.OemCloseBrackets, cmdKey | KeyModifiers.Shift);
        MenuForward.InputGesture = new KeyGesture(Key.OemCloseBrackets, cmdKey);
        MenuBackward.InputGesture = new KeyGesture(Key.OemOpenBrackets, cmdKey);
        MenuToBack.InputGesture = new KeyGesture(Key.OemOpenBrackets, cmdKey | KeyModifiers.Shift);
        MenuToFront.Click += (_, _) => OrderSelected("front");
        MenuForward.Click += (_, _) => OrderSelected("forward");
        MenuBackward.Click += (_, _) => OrderSelected("backward");
        MenuToBack.Click += (_, _) => OrderSelected("back");
        MenuFlipH.Click += (_, _) => FlipSelected("x");
        MenuFlipV.Click += (_, _) => FlipSelected("y");
        MenuAlignLeft.Click += (_, _) => AlignSelected("left", null);
        MenuAlignCenter.Click += (_, _) => AlignSelected("center", null);
        MenuAlignRight.Click += (_, _) => AlignSelected("right", null);
        MenuAlignTop.Click += (_, _) => AlignSelected(null, "top");
        MenuAlignMiddle.Click += (_, _) => AlignSelected(null, "middle");
        MenuAlignBottom.Click += (_, _) => AlignSelected(null, "bottom");
        MenuCenterPage.Click += (_, _) => AlignSelected("center", "middle", "page");
        MenuDistributeX.Click += (_, _) => DistributeSelected("x", "gaps");
        MenuDistributeY.Click += (_, _) => DistributeSelected("y", "gaps");
        MenuDistributeCX.Click += (_, _) => DistributeSelected("x", "centers");
        MenuDistributeCY.Click += (_, _) => DistributeSelected("y", "centers");

        GroupButton.Click += (_, _) => Group();
        UngroupButton.Click += (_, _) => Ungroup();
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
            _spec = _spec with { Angle = (double)v, X1 = null, Y1 = null, X2 = null, Y2 = null }; // the angle replaces the explicit points
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

        // Fill tool on the canvas.
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

    // Opens or closes the full palette; the button's arrow shows where we go ("‹" opens towards the drawing, "›" closes it again).
    void SetPaletteOpen(bool open)
    {
        PaletteFlyout.IsVisible = open;
        PaletteChevron.Data = Geometry.Parse(open ? "M5,3 L9,8 L5,13" : "M9,3 L5,8 L9,13");
        ToolTip.SetTip(PaletteExpandButton, open ? "Close the palette" : "Show the whole palette");
    }

    // ---- Palette: a column of round swatches along the right edge, as in classic vector editors ------------------------

    // HSL color -> "#RRGGBB" (h in degrees, s and l in 0..1).
    static string Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        static int B(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{B(r + m):X2}{B(g + m):X2}{B(b + m):X2}";
    }

    // Color groups in the order they appear: no color, grays, then each hue with its tones from lightest to darkest.
    static readonly List<List<string>> PaletteGroups = BuildPalette();

    static List<List<string>> BuildPalette()
    {
        var groups = new List<List<string>>
        {
            new() { "none" },
            new() { "#FFFFFF", "#F3F4F6", "#E5E7EB", "#D1D5DB", "#9CA3AF", "#6B7280", "#4B5563", "#374151", "#1F2937", "#000000" },
        };
        double[] lightness = [0.94, 0.85, 0.74, 0.63, 0.52, 0.41, 0.30, 0.20];
        double[] saturation = [0.90, 0.85, 0.82, 0.80, 0.78, 0.76, 0.72, 0.68];
        foreach (var hue in new double[] { 0, 18, 32, 46, 60, 85, 120, 150, 172, 190, 207, 225, 245, 268, 290, 315, 338 })
            groups.Add(lightness.Select((l, i) => Hsl(hue, saturation[i], l)).ToList());
        // Browns and beiges: warm, slightly saturated tints.
        groups.Add(lightness.Select((l, i) => Hsl(28, 0.45 + i * 0.01, Math.Min(0.92, l + 0.02))).ToList());
        return groups;
    }

    // A round swatch: left click = fill, right click = stroke (as in classic vector editors).
    static Button Swatch(string c, int size, double bottomMargin, Action<string, bool> pick)
    {
        var b = new Button
        {
            Width = size, Height = size, MinHeight = 0, MinWidth = 0, Margin = new(0, 0, 0, bottomMargin), Padding = new(0), CornerRadius = new(size / 2.0),
            BorderBrush = new SolidColorBrush(Color.Parse("#66808080")), BorderThickness = new(1),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Background = c == "none" ? Brushes.Transparent : new SolidColorBrush(Color.Parse(c)),
            Content = c == "none"
                ? new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse($"M3,{size - 3} L{size - 3},3"), Stroke = Brushes.Red, StrokeThickness = 1.6, Width = size, Height = size, Stretch = Stretch.None }
                : null,
        };
        ToolTip.SetTip(b, c == "none" ? "No color" : c);
        // Button only handles the left click: I listen to the release to tell the two buttons apart.
        b.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!new Rect(b.Bounds.Size).Contains(e.GetPosition(b))) return;
            if (e.InitialPressMouseButton == MouseButton.Left) pick(c, false);
            else if (e.InitialPressMouseButton == MouseButton.Right) pick(c, true);
        }, handledEventsToo: true);
        return b;
    }

    // Narrow column along the edge: all the colors in a row.
    static void BuildSwatches(Panel panel, Action<string, bool> pick)
    {
        foreach (var group in PaletteGroups)
        {
            foreach (var c in group) panel.Children.Add(Swatch(c, 17, 3, pick));
            panel.Children.Add(new Border { Height = 6 }); // a bit of air between one family and the next
        }
    }

    // Expanded panel: one column for each hue (the first with "no color" and the grays), one tone per row.
    static void BuildPaletteGrid(Panel host, Action<string, bool> pick, int size = 24)
    {
        // First column: no color and 7 grays, so it has 8 rows like the others.
        var first = new StackPanel();
        var grays = PaletteGroups[1];
        foreach (var c in PaletteGroups[0].Concat(new[] { 0, 2, 3, 4, 5, 7, 9 }.Select(i => grays[i]))) first.Children.Add(Swatch(c, size, 3, pick));
        host.Children.Add(first);
        foreach (var group in PaletteGroups.Skip(2))
        {
            var column = new StackPanel();
            foreach (var c in group) column.Children.Add(Swatch(c, size, 3, pick));
            host.Children.Add(column);
        }
    }

    // Accepts "#RRGGBB", "RRGGBB" and "none"; anything else is rejected by the command with an explicit error.
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
        PencilTool.IsChecked = Canvas.Tool == Tool.Pencil;
        PolygonTool.IsChecked = Canvas.Tool == Tool.Polygon;
        StarTool.IsChecked = Canvas.Tool == Tool.Star;
        LineTool.IsChecked = Canvas.Tool == Tool.Line;
        EyedropperTool.IsChecked = Canvas.Tool == Tool.Eyedropper;
        TextTool.IsChecked = Canvas.Tool == Tool.Text;
        FillTool.IsChecked = Canvas.Tool == Tool.Fill;
        NodeTool.IsChecked = Canvas.Tool == Tool.Node;
        // With the Node tool the bar shows node operations instead of the boolean ones.
        UpdateBars();
    }

    /// <summary>Updates the current style (new shapes) and applies it to the selection through the bus.</summary>
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
        catch (CommandException ex)
        {
            ShowNotice(ex.Message);
            Refresh(); // invalid color or locked object: restore the fields from the object
        }
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return; // Backspace/letters in text fields are not shortcuts
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
        else if (cmd && e.Key == Key.S)
        {
            _ = SaveAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); // Shift = Save As
            e.Handled = true;
        }
        else if (cmd && e.Key == Key.O) { _ = OpenAsync(); e.Handled = true; }
        else if (cmd && e.Key == Key.N) { _ = NewDocumentAsync(); e.Handled = true; }
        else if (cmd && e.Key == Key.E) { _ = ExportPdfAsync(); e.Handled = true; }
        else if (cmd && e.Key is Key.OemCloseBrackets or Key.OemOpenBrackets)
        {
            var up = e.Key == Key.OemCloseBrackets;
            var extreme = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            OrderSelected(up ? (extreme ? "front" : "forward") : (extreme ? "back" : "backward"));
            e.Handled = true;
        }
        else if (cmd && e.Key == Key.D0)
        {
            Canvas.FitPage();
            e.Handled = true;
        }
        else if (cmd && e.Key == Key.D1)
        {
            Canvas.ZoomActualSize();
            e.Handled = true;
        }
        else if (cmd && e.Key == Key.D) { Duplicate(); e.Handled = true; }
        else if (cmd && e.Key is Key.C or Key.X) { CopySelection(e.Key == Key.X); e.Handled = true; }
        else if (cmd && e.Key == Key.V) { _ = PasteAsync(); e.Handled = true; }
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
        else if (!cmd && e.Key is Key.V or Key.R or Key.E or Key.P or Key.N or Key.F10 or Key.T or Key.G or Key.L or Key.Y or Key.U or Key.B or Key.I)
        {
            Canvas.Tool = e.Key switch
            {
                Key.R => Tool.Rectangle, Key.E => Tool.Ellipse, Key.P => Tool.Pen, Key.N or Key.F10 => Tool.Node, Key.T => Tool.Text, Key.G => Tool.Fill,
                Key.L => Tool.Line, Key.Y => Tool.Polygon, Key.U => Tool.Star, Key.B => Tool.Pencil, Key.I => Tool.Eyedropper, _ => Tool.Select,
            };
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Canvas.PenActive)
        {
            Canvas.FinishPen(false); // ends the open path
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.F2 && Canvas.SelectedId != null)
        {
            Canvas.EditSelectedText(); // Enter or F2 on a selected text: edit it
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && PaletteFlyout.IsVisible)
        {
            SetPaletteOpen(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Canvas.PenActive)
        {
            Canvas.CancelPen();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && Canvas.PenActive)
        {
            Canvas.PenBack(); // while drawing removes the last node
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && Canvas.Tool == Tool.Node && Canvas.NodeSelectionCount > 0)
        {
            Canvas.ApplyNodeOp(NodeOp.Delete); // with the Node tool Delete removes nodes, not the object
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && Canvas.Tool == Tool.Fill && Canvas.DeleteSelectedStop())
        {
            e.Handled = true; // with the fill tool Delete removes the selected color stop
        }
        else if (e.Key is Key.Delete or Key.Back && Canvas.SelectedIds.Count > 0)
        {
            Exec(new DeleteObjects { Targets = Canvas.SelectedIds.ToList() });
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

    // Starting grid step for the chosen unit.
    void SetGridDefault() =>
        GridSizeBox.Value = _unit switch { "mm" => 5, "cm" => 0.5m, "in" => 0.25m, _ => 10 };

    // ---- Rename in the Objects panel ---------------------------------------------------------

    void BeginRename(int index)
    {
        if (index < 0 || index >= _ids.Count || ObjectList.ContainerFromIndex(index) is not { } item) return;
        CommitRename();
        var id = _ids[index];
        var name = _bus.Read(d => d.Objects.FirstOrDefault(o => o.Id == id)?.Name);
        var at = item.TranslatePoint(new Point(0, 0), (Visual)ObjectList.Parent!);
        if (at == null) return;
        _renamingId = id;
        RenameBox.Text = name ?? "";
        RenameBox.Margin = new Thickness(at.Value.X, at.Value.Y, 0, 0);
        RenameBox.Width = item.Bounds.Width;
        RenameBox.Height = Math.Max(item.Bounds.Height, 28);
        RenameBox.IsVisible = true;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    void CommitRename()
    {
        if (_renamingId is not { } id) return;
        _renamingId = null; // before hiding the box: hiding it fires LostFocus
        var text = RenameBox.Text ?? "";
        RenameBox.IsVisible = false;
        var current = _bus.Read(d => d.Objects.FirstOrDefault(o => o.Id == id)?.Name) ?? "";
        if (text.Trim() == current) return;
        try { _bus.Execute([new RenameObject { Target = id, Name = text }]); }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    // ---- Page, measures and status bar -------------------------------------------------------

    // Number in the chosen unit: the document is in px (96 dpi).
    string Fmt(double px)
    {
        var v = Units.FromPx(px, _unit);
        return Math.Round(v, _unit switch { "px" => 1, "mm" => 2, "cm" => 3, "pt" => 2, _ => 3 }).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    void ExecutePage(SetPage cmd)
    {
        try { _bus.Execute([cmd]); }
        catch (CommandException ex) { ShowNotice(ex.Message); SyncPageBar(); }
    }

    // Aligns format, measures and orientation to the document (and reframes the page if it changed).
    void SyncPageBar()
    {
        var (w, h) = _bus.Read(d => (d.Pages[0].Width, d.Pages[0].Height));
        var changed = Math.Abs(w - _lastPage.W) > 0.01 || Math.Abs(h - _lastPage.H) > 0.01;
        var first = _lastPage == default;
        _lastPage = (w, h);

        _syncingPage = true;
        var match = PageSizes.Match(w, h);
        PagePresetCombo.SelectedIndex = match != null ? Array.IndexOf(PageSizes.All, match) : PageSizes.All.Length;
        PageWBox.Value = (decimal)Math.Round(Units.FromPx(w, _unit), 3);
        PageHBox.Value = (decimal)Math.Round(Units.FromPx(h, _unit), 3);
        PageUnitText.Text = _unit;
        PortraitButton.IsChecked = h >= w;
        LandscapeButton.IsChecked = w > h;
        _syncingPage = false;

        if (changed && !first) Canvas.FitPage(); // new page: it is shown whole
    }

    // Status bar: measures of the gesture in progress or of the selection.
    void UpdateStatus()
    {
        if (_liveInfo != null) { StatusInfo.Text = _liveInfo; return; }
        var info = _bus.Read(d =>
        {
            var objs = Canvas.SelectedIds.Select(id => d.Objects.FirstOrDefault(o => o.Id == id)).OfType<SceneObject>().ToList();
            if (objs.Count == 0) return null;
            var boxes = objs.Select(d.PageBounds).ToList();
            var x0 = boxes.Min(b => b.X); var y0 = boxes.Min(b => b.Y);
            var union = new Box(x0, y0, boxes.Max(b => b.X + b.Width) - x0, boxes.Max(b => b.Y + b.Height) - y0);
            var o = objs[0];
            var title = objs.Count > 1 ? $"{objs.Count} objects"
                : o is TextObject t ? $"Text “{(t.Text.Length > 20 ? t.Text[..20] + "…" : t.Text).Replace('\n', ' ')}”"
                : o is GroupObject g ? $"{o.Name ?? "Group"} ({g.Children.Count} objects)"
                : o.Name is { } n ? $"{n} ({o.Type})" : o.Type;
            return ((string, Box, double)?)(title, union, objs.Count == 1 ? Math.Atan2(o.Transform.M12, o.Transform.M11) * 180 / Math.PI : 0);
        });
        if (info is not { } i)
        {
            StatusInfo.Text = $"No selection   ·   page {Fmt(_lastPage.W)} × {Fmt(_lastPage.H)} {_unit}";
            return;
        }
        var (title, b, rot) = i;
        StatusInfo.Text = $"{title}   ·   {Fmt(b.Width)} × {Fmt(b.Height)} {_unit}   ·   x {Fmt(b.X)}   y {Fmt(b.Y)}"
                          + (Math.Abs(rot) > 0.05 ? $"   ·   {rot:0.#}°" : "");
    }

    // ---- Text ----------------------------------------------------------------------------------

    /// <summary>Top bar: nodes with the Node tool, style with the Text tool or a selected text, otherwise booleans.</summary>
    void UpdateBars()
    {
        var node = Canvas.Tool == Tool.Node;
        var shape = !node && Canvas.Tool is Tool.Polygon or Tool.Star;
        var text = !node && !shape && (Canvas.Tool == Tool.Text || SelectedTextStyle() != null);
        var page = !node && !shape && !text && Canvas.SelectedIds.Count == 0; // without a selection the bar shows the page
        NodeBar.IsVisible = node;
        ShapeBar.IsVisible = shape;
        if (shape)
        {
            var star = Canvas.Tool == Tool.Star;
            SidesLabel.Text = star ? "Points" : "Sides";
            DepthLabel.IsVisible = DepthBox.IsVisible = star;
            SidesBox.Value = star ? Canvas.StarPoints : Canvas.PolygonSides;
        }
        TextBar.IsVisible = text;
        PageBar.IsVisible = page;
        BoolBar.IsVisible = !node && !shape && !text && !page;
        XformBar.IsVisible = BoolBar.IsVisible;
    }

    // ---- Numeric fields for position, size and rotation -----------------------------------

    bool _syncingXform;

    void SyncXformBar()
    {
        var info = _bus.Read(d =>
        {
            var objs = Canvas.SelectedIds.Select(id => d.Objects.FirstOrDefault(o => o.Id == id)).OfType<SceneObject>().ToList();
            if (objs.Count == 0) return null;
            var boxes = objs.Select(d.PageBounds).ToList();
            var x0 = boxes.Min(b => b.X); var y0 = boxes.Min(b => b.Y);
            var rot = objs.Count == 1 ? Math.Atan2(objs[0].Transform.M12, objs[0].Transform.M11) * 180 / Math.PI : 0;
            return ((Box, double)?)(new Box(x0, y0, boxes.Max(b => b.X + b.Width) - x0, boxes.Max(b => b.Y + b.Height) - y0), rot);
        });
        _syncingXform = true;
        XformBar.IsEnabled = info != null;
        if (info is var (b, rot))
        {
            decimal V(double px) => (decimal)Math.Round(Units.FromPx(px, _unit), 3);
            XBox.Value = V(b.X); YBox.Value = V(b.Y); WBox.Value = V(b.Width); HBox.Value = V(b.Height);
            RotBox.Value = (decimal)Math.Round(rot, 2);
            WBox.IsEnabled = HBox.IsEnabled = Canvas.SelectedIds.Count == 1 && Math.Abs(rot) < 0.05;
            RotBox.IsEnabled = Canvas.SelectedIds.Count == 1;
        }
        _syncingXform = false;
    }

    // Applies the typed value: X/Y move the selection, W/H resize, the rotation brings the object to the given angle.
    void ApplyXform(NumericUpDown box)
    {
        if (_syncingXform || box.Value is not { } val || Canvas.SelectedIds.Count == 0) return;
        var ids = Canvas.SelectedIds.ToList();
        var v = Units.ToPx((double)val, _unit);
        try
        {
            if (box == XBox || box == YBox)
                _bus.Execute(_bus.Read(d =>
                {
                    var boxes = ids.Select(id => d.Objects.First(o => o.Id == id)).Select(d.PageBounds).ToList();
                    var cur = box == XBox ? boxes.Min(b => b.X) : boxes.Min(b => b.Y);
                    var delta = new Vector2(box == XBox ? (float)(v - cur) : 0, box == YBox ? (float)(v - cur) : 0);
                    // Each object lives in the space of its container: I bring the shift from page space to that space.
                    return ids.Select(id =>
                    {
                        var o = d.Objects.First(x => x.Id == id);
                        Matrix3x2.Invert(d.ParentMatrix(o), out var inv);
                        var local = Vector2.TransformNormal(delta, inv);
                        return (Command)new MoveObjects { Targets = [id], Dx = local.X, Dy = local.Y };
                    }).ToList();
                }));
            else if (box == RotBox)
            {
                var o = _bus.Read(d => d.Objects.First(x => x.Id == ids[0]));
                var cur = Math.Atan2(o.Transform.M12, o.Transform.M11) * 180 / Math.PI;
                if (Math.Abs((double)val - cur) > 0.001) _bus.Execute([new RotateObjects { Targets = ids, Degrees = (double)val - cur }]);
            }
            else
            {
                // The size is changed with the same proportion as the box on the page, which includes any scale of groups.
                var (o, b) = _bus.Read(d => { var x = d.Objects.First(o => o.Id == ids[0]); return (x, d.PageBounds(x)); });
                var k = v / (box == WBox ? b.Width : b.Height);
                _bus.Execute([new ResizeObject { Targets = ids, Width = box == WBox ? o.Width * k : null, Height = box == HBox ? o.Height * k : null }]);
            }
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
        SyncXformBar();
    }

    /// <summary>Style of the selected text (if it is a single text).</summary>
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
        // A text on a path shows its options (in the chosen unit).
        var op = Canvas.SelectedId is { } sid ? _bus.Read(d => (d.Objects.FirstOrDefault(o => o.Id == sid) as TextObject)?.OnPath) : null;
        TextPathButton.IsVisible = op != null;
        if (op != null)
        {
            PathAlignCombo.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "start", "center", "end" }, op.Align));
            PathOffsetBox.Value = (decimal)Math.Round(Units.FromPx(op.Offset, _unit), 3);
            PathDistanceBox.Value = (decimal)Math.Round(Units.FromPx(op.Distance, _unit), 3);
            PathReverseCheck.IsChecked = op.Reverse;
        }
        _syncingText = false;
    }

    // A text and a path (path, rectangle or ellipse) selected together: the text is put on the path.
    (string Text, string Path)? FitPathPair()
    {
        if (Canvas.SelectedIds.Count != 2) return null;
        return _bus.Read(d =>
        {
            var objs = Canvas.SelectedIds.Select(id => d.Objects.FirstOrDefault(o => o.Id == id)).OfType<SceneObject>().ToList();
            var text = objs.FirstOrDefault(o => o is TextObject);
            var path = objs.FirstOrDefault(o => o is PathObject or RectangleObject or EllipseObject);
            return text != null && path != null ? ((string, string)?)(text.Id, path.Id) : null;
        });
    }

    void FitTextToPath()
    {
        if (FitPathPair() is not var (text, path)) return;
        try { _bus.Execute([new SetTextOnPath { Targets = [text], Path = path }]); }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    void ApplyOnPath(string? align = null, double? offset = null, double? distance = null, bool? reverse = null, bool detach = false)
    {
        if (_syncingText || Canvas.SelectedId is not { } id) return;
        try { _bus.Execute([new SetTextOnPath { Targets = [id], Align = align, Offset = offset, Distance = distance, Reverse = reverse, Detach = detach }]); }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    // Changes the style of new texts and applies the change to the selected texts (a single undo).
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

    // In-place editor: a TextBox above the canvas, with the same font; the original object stays hidden.
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
        _edit = null; // before hiding the editor: hiding it fires LostFocus
        var text = TextEditor.Text ?? "";
        TextEditor.IsVisible = false;
        Canvas.HiddenId = null;
        try
        {
            if (e.Id is { } id)
            {
                if (text.Length == 0) _bus.Execute([new DeleteObjects { Targets = [id] }]); // an emptied text disappears
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
                Canvas.Tool = Tool.Select; // after writing we go back to selection
                SyncToolButtons();
            }
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    // ---- Fill (solid, gradient, pattern) -----------------------------------------

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

    // First and last color of the current fill: switching from one type to another the colors stay.
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

    // Document color ("#RRGGBB" or "#RRGGBBAA") as an Avalonia color.
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

    // One row per color: swatch (click, then choose from the palette), position in % and removal.
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
        ToolTip.SetTip(chip, "Click to pick a color");
        var grid = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 2 };
        var flyout = new Flyout { Content = new Border { Padding = new(6), Child = grid }, Placement = PlacementMode.BottomEdgeAlignedLeft };
        BuildPaletteGrid(grid, (c, _) =>
        {
            if (c == "none") return;
            flyout.Hide();
            _gradArmed = index;
            PickGradientColor(c);
        }, 16);
        chip.Flyout = flyout;
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

    // Preview in the panel, with the same geometry as the document (points and radii as fractions of the box).
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
            case 3: // pattern: half pattern color, half background
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

    internal void ShowNotice(string message, bool error = true)
    {
        NoticeText.Foreground = error ? new SolidColorBrush(Color.Parse("#EF4444")) : new SolidColorBrush(Color.Parse("#22C55E"));
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

    // Applies a command to the current selection; with an empty selection it does nothing, and errors become a notice.
    void ExecuteOnSelection(Func<List<string>, Command> make)
    {
        var ids = Canvas.SelectedIds.ToList();
        if (ids.Count == 0) return;
        try { _bus.Execute([make(ids)]); }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    // Several objects are mirrored together, as one block; a single one around its own center.
    void FlipSelected(string axis) => ExecuteOnSelection(ids => new FlipObjects { Targets = ids, Axis = axis, Together = ids.Count > 1 });

    void OrderSelected(string action) => ExecuteOnSelection(ids => new OrderObjects { Targets = ids, Action = action });

    void AlignSelected(string? x, string? y, string? to = null) => ExecuteOnSelection(ids => new AlignObjects { Targets = ids, X = x, Y = y, To = to });

    void DistributeSelected(string axis, string mode) => ExecuteOnSelection(ids => new DistributeObjects { Targets = ids, Axis = axis, Mode = mode });

    void Group()
    {
        var ids = Canvas.SelectedIds.ToList();
        if (ids.Count < 2) return;
        try
        {
            var r = _bus.Execute([new GroupObjects { Targets = ids }]);
            Canvas.Select(r.CreatedIds[0]);
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    string? _clipboard; // copied objects (JSON of DocumentFile.ExportObjects); survives the deletion of the originals
    int _pasteCount;    // each subsequent paste shifts a bit more, so as not to land on the previous one

    void Duplicate()
    {
        var ids = Canvas.SelectedIds.ToList();
        if (ids.Count == 0) return;
        try { Canvas.SetSelection(_bus.Execute([new DuplicateObjects { Targets = ids }]).CreatedIds); }
        catch (CommandException) { }
    }

    void CopySelection(bool cut)
    {
        var ids = Canvas.SelectedIds.ToHashSet();
        if (ids.Count == 0) return;
        _clipboard = _bus.Read(d => DocumentFile.ExportObjects(d.Objects.Where(o => ids.Contains(o.Id))));
        _pasteCount = cut ? 0 : 1; // after Copy the original is still there; after Cut the first paste goes to the same place
        CopySvgToSystemClipboard(ids);
        if (cut) Exec(new DeleteObjects { Targets = ids.ToList() });
    }

    // Besides the internal copy, the selection goes to the system clipboard as SVG text, so other programs can paste it.
    string? _lastSvgCopy;

    void CopySvgToSystemClipboard(HashSet<string> ids)
    {
        var svg = _bus.Read(d => d.Objects.Where(o => ids.Contains(o.Id)).ToList() is { Count: > 0 } objs ? SvgFile.ExportObjects(d, objs) : null);
        if (svg == null) return;
        _lastSvgCopy = svg;
        if (GetTopLevel(this)?.Clipboard is { } cb) _ = cb.SetTextAsync(svg);
    }

    // An SVG on the system clipboard (copied from another program) is imported; otherwise the internal copy is pasted.
    async Task PasteAsync()
    {
        string? text = null;
        try { if (GetTopLevel(this)?.Clipboard is { } cb) text = await cb.TryGetTextAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { } // clipboard not available
        if (text != null && text != _lastSvgCopy && LooksLikeSvg(text))
        {
            ImportSvgText(text, "pasted", Canvas.ViewCenter);
            return;
        }
        Paste();
    }

    static bool LooksLikeSvg(string text)
    {
        var t = text.AsSpan().TrimStart();
        return t.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    void Paste()
    {
        if (_clipboard == null) return;
        var offset = 20 * _pasteCount++;
        try { Canvas.SetSelection(_bus.Execute([new PasteObjects { Data = _clipboard, Dx = offset, Dy = offset }]).CreatedIds); }
        catch (CommandException) { }
    }

    void Ungroup()
    {
        // The children's ids must be read first: after the command the group no longer exists.
        var groups = _bus.Read(d => Canvas.SelectedIds
            .Select(id => d.Objects.FirstOrDefault(o => o.Id == id)).OfType<GroupObject>()
            .Select(g => (g.Id, Children: g.Children.Select(c => c.Id).ToList())).ToList());
        if (groups.Count == 0) return;
        if (Exec(new UngroupObjects { Targets = groups.Select(g => g.Id).ToList() }) != null)
            Canvas.SetSelection(groups.SelectMany(g => g.Children));
    }

    /// <summary>Finds out what changed since last time and signals the agent's work.</summary>
    void OnBusChanged()
    {
        var (version, changes) = _bus.GetChanges(_seenVersion);
        _seenVersion = version;

        var replaced = false;
        foreach (var change in changes)
        {
            if (change.Kind == "reset") // document replaced (open/new): start from scratch
            {
                replaced = true;
                _aiTouched.Clear();
                continue;
            }
            var ids = change.Command.AffectedIds.ToList();
            if (!change.Author.StartsWith("llm:"))
            {
                _aiTouched.ExceptWith(ids);
                continue;
            }
            _aiTouched.UnionWith(ids);
            Canvas.Highlight(ids, change.Author[4..]);
            if (!_activeAgents.Contains(change.Author[4..])) _activeAgents.Add(change.Author[4..]);
            AgentText.Text = $"{string.Join(", ", _activeAgents)} {(_activeAgents.Count > 1 ? "are" : "is")} drawing";
            AgentChip.IsVisible = true;
            _chipTimer.Stop();
            _chipTimer.Start();
        }
        if (replaced) { Canvas.SetSelection([]); Canvas.FitPage(); }
        Refresh();
    }

    /// <summary>Aligns the object list and canvas with the document (UI thread).</summary>
    void Refresh()
    {
        var items = _bus.Read(d =>
        {
            var several = d.Pages[0].Layers.Count > 1;
            return d.Pages[0].Layers.AsEnumerable().Reverse().SelectMany(l => l.Objects.AsEnumerable().Reverse().Select(o => (o.Id, Label:
                $"{o.Name ?? (o is TextObject tx ? "“" + (tx.Text.Length > 24 ? tx.Text[..24].Replace('\n', ' ') + "…" : tx.Text.Replace('\n', ' ')) + "”" : o.Type)}   ·   {o.Id}" +
                $"{(Document.SelfAndDescendants(o).Any(x => _aiTouched.Contains(x.Id)) ? "   ·   AI" : "")}{(o.Hidden ? "   ·   hidden" : "")}{(o.Locked ? "   ·   locked" : "")}{(several ? "   ·   " + l.Name : "")}"))).ToList();
        });
        _ids = items.Select(i => i.Id).ToList();

        Canvas.PruneSelection(_ids);

        _syncing = true;
        ObjectList.ItemsSource = items.Select(i => i.Label).ToList();
        ObjectList.SelectedIndex = Canvas.SelectedId is { } s ? _ids.IndexOf(s) : -1;
        RefreshLayers();
        SyncStrokeStyle();

        // The panel shows the style of the selected object (and becomes the style of new shapes).
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

        // Group and Ungroup are always on the bar; they light up when they make sense.
        GroupButton.IsEnabled = Canvas.SelectedIds.Count >= 2;
        ArrangeButton.IsEnabled = AlignButton.IsEnabled = Canvas.SelectedIds.Count >= 1;
        MenuDistributeX.IsEnabled = MenuDistributeY.IsEnabled = MenuDistributeCX.IsEnabled = MenuDistributeCY.IsEnabled = Canvas.SelectedIds.Count >= 3;
        UngroupButton.IsEnabled = _bus.Read(d => Canvas.SelectedIds.Any(id => d.Objects.FirstOrDefault(o => o.Id == id) is GroupObject));
        var many = Canvas.SelectedIds.Count >= 2;
        FitPathButton.IsEnabled = FitPathPair() != null;
        WeldButton.IsEnabled = TrimButton.IsEnabled = IntersectButton.IsEnabled = SubtractButton.IsEnabled = many;

        Canvas.InvalidateVisual();
        UpdateScrollBars(); // the content may have changed
        UpdateBars();
        SyncTextBar();
        SyncPageBar();
        SyncXformBar();
        UpdateStatus();
        UpdateTitle(); // the title shows "*" if there are unsaved changes
    }
}
