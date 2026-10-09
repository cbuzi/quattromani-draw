using AiDraw.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AiDraw.App;

public partial class MainWindow : Window
{
    readonly CommandBus _bus = App.Bus;
    List<string> _ids = [];
    bool _syncing;
    long _seenVersion;
    readonly HashSet<string> _aiTouched = []; // oggetti la cui ultima modifica è dell'agente
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
        Canvas.ViewChanged += () => ZoomText.Text = $"{Canvas.Zoom:P0}";
        ZoomText.Text = $"{Canvas.Zoom:P0}";

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

        KeyDown += OnKeyDown;
        Refresh();
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
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
        else if (e.Key is Key.Delete or Key.Back && Canvas.SelectedId is { } id)
        {
            _bus.Execute([new DeleteObjects { Targets = [id] }]);
            e.Handled = true;
        }
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
        var items = _bus.Read(d => d.Objects.Reverse().Select(o => (o.Id, Label: $"{o.Name ?? o.Type}   ·   {o.Id}{(_aiTouched.Contains(o.Id) ? "   ·   AI" : "")}")).ToList());
        _ids = items.Select(i => i.Id).ToList();

        if (Canvas.SelectedId is { } sel && !_ids.Contains(sel)) Canvas.Select(null);

        _syncing = true;
        ObjectList.ItemsSource = items.Select(i => i.Label).ToList();
        ObjectList.SelectedIndex = Canvas.SelectedId is { } s ? _ids.IndexOf(s) : -1;
        _syncing = false;

        Canvas.InvalidateVisual();
    }
}
