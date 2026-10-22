using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quattromani.Core;

namespace Quattromani.App;

/// <summary>Layers panel, hide/lock and "move to layer" for objects.</summary>
public partial class MainWindow
{
    // Material Design icons (eye, closed and open lock).
    const string EyeIcon = "M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5Z";
    const string LockIcon = "M12,17A2,2 0 0,0 14,15C14,13.89 13.1,13 12,13A2,2 0 0,0 10,15A2,2 0 0,0 12,17M18,8A2,2 0 0,1 20,10V20A2,2 0 0,1 18,22H6A2,2 0 0,1 4,20V10C4,8.89 4.9,8 6,8H7V6A5,5 0 0,1 12,1A5,5 0 0,1 17,6V8H18M12,3A3,3 0 0,0 9,6V8H15V6A3,3 0 0,0 12,3Z";
    const string UnlockIcon = "M18,1C15.24,1 13,3.24 13,6V8H4A2,2 0 0,0 2,10V20A2,2 0 0,0 4,22H16A2,2 0 0,0 18,20V10A2,2 0 0,0 16,8H15V6C15,4.34 16.34,3 18,3C19.66,3 21,4.34 21,6V8H23V6C23,3.24 20.76,1 18,1M10,17A2,2 0 0,1 8,15A2,2 0 0,1 10,13A2,2 0 0,1 12,15A2,2 0 0,1 10,17Z";

    List<string> _layerNames = []; // from top to bottom, as in the list

    // The user's commands go through here: a rejected command becomes a notice.
    BatchResult? Exec(params Command[] commands)
    {
        try { return _bus.Execute(commands); }
        catch (CommandException ex) { ShowNotice(ex.Message); return null; }
    }

    // ---- Launch agent -----------------------------------------------------------------------------

    void InitAgents()
    {
        MenuLaunchClaude.Click += (_, _) => LaunchAgent(AgentLauncher.Agents[0]);
        MenuLaunchCodex.Click += (_, _) => LaunchAgent(AgentLauncher.Agents[1]);
        MenuLaunchOpenCode.Click += (_, _) => LaunchAgent(AgentLauncher.Agents[2]);
        MenuConnectManual.Click += (_, _) => App.ShowAbout();
    }

    void LaunchAgent(AgentLauncher.Agent agent)
    {
        try
        {
            AgentLauncher.Launch(agent);
            ShowNotice($"Starting {agent.Name} in a terminal. Describe what to draw there; you will see its work appear here.", error: false);
        }
        catch (InvalidOperationException ex) { ShowNotice(ex.Message); }
    }

    void InitLayers()
    {
        Canvas.Notice += message => ShowNotice(message);
        LayerAddButton.Click += (_, _) => Exec(new AddLayer());
        LayerUpButton.Click += (_, _) => MoveActiveLayer("up");
        LayerDownButton.Click += (_, _) => MoveActiveLayer("down");
        LayerDeleteButton.Click += (_, _) => { if (ActiveLayerName() is { } name) Exec(new DeleteLayer { Layer = name }); };
        LayerList.SelectionChanged += (_, _) =>
        {
            if (_syncing || LayerList.SelectedIndex is not (>= 0 and var i) || i >= _layerNames.Count) return;
            _bus.SetActiveLayer(_layerNames[i]);
        };

        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            if (Canvas.SelectedIds.Count == 0) { e.Cancel = true; return; }
            FillObjectMenu(menu);
        };
        ObjectList.ContextMenu = menu;
    }

    string? ActiveLayerName() => LayerList.SelectedIndex is (>= 0 and var i) && i < _layerNames.Count ? _layerNames[i] : null;

    void MoveActiveLayer(string action)
    {
        if (ActiveLayerName() is { } name) Exec(new OrderLayer { Layer = name, Action = action });
    }

    /// <summary>Aligns the layer list with the document (UI thread, inside Refresh).</summary>
    void RefreshLayers()
    {
        var (layers, active) = _bus.Read(d => (d.Pages[0].Layers.Select(l => (l.Name, l.Visible, l.Locked, l.Objects.Count)).ToList(), d.Pages[0].Layers.IndexOf(d.Pages[0].ActiveLayer)));
        layers.Reverse();
        _layerNames = layers.Select(l => l.Name).ToList();
        LayerList.ItemsSource = layers.Select(l => LayerRow(l.Name, l.Visible, l.Locked, l.Count)).ToList();
        LayerList.SelectedIndex = layers.Count - 1 - active;
        LayerDeleteButton.IsEnabled = layers.Count > 1;
        LayerUpButton.IsEnabled = LayerList.SelectedIndex > 0;
        LayerDownButton.IsEnabled = LayerList.SelectedIndex >= 0 && LayerList.SelectedIndex < layers.Count - 1;
    }

    Control LayerRow(string name, bool visible, bool locked, int count)
    {
        Button IconButton(string data, bool on, string tip, int column, Action click)
        {
            var b = new Button
            {
                Width = 26, Height = 24, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Opacity = on ? 1 : 0.3, Content = new PathIcon { Data = Geometry.Parse(data), Width = 14, Height = 14 },
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            Grid.SetColumn(b, column);
            return b;
        }

        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = visible ? 1 : 0.5 };
        Grid.SetColumn(label, 2);
        var objects = new TextBlock { Text = count.ToString(), FontSize = 11, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        Grid.SetColumn(objects, 3);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        row.Children.Add(IconButton(EyeIcon, visible, visible ? "Hide layer" : "Show layer", 0, () => Exec(new SetLayer { Layer = name, Visible = !visible })));
        row.Children.Add(IconButton(locked ? LockIcon : UnlockIcon, locked, locked ? "Unlock layer" : "Lock layer", 1, () => Exec(new SetLayer { Layer = name, Locked = !locked })));
        row.Children.Add(label);
        row.Children.Add(objects);
        label.DoubleTapped += (_, _) => BeginLayerRename(row, label, name);
        return row;
    }

    void BeginLayerRename(Grid row, TextBlock label, string name)
    {
        var box = new TextBox { Text = name, MinHeight = 0, Height = 24, Padding = new Thickness(4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetColumn(box, 2);
        row.Children.Remove(label);
        row.Children.Add(box);
        var done = false;
        void Finish(bool apply)
        {
            if (done) return;
            done = true;
            var text = box.Text?.Trim() ?? "";
            if (apply && text != name && Exec(new SetLayer { Layer = name, Name = text }) != null) return; // the list rebuilds itself
            row.Children.Remove(box);
            row.Children.Add(label);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { Finish(e.Key == Key.Enter); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Finish(true);
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Background);
    }

    // ---- Hide, lock, move to layer ----------------------------------------------------

    void FillObjectMenu(ContextMenu menu)
    {
        var ids = Canvas.SelectedIds.ToList();
        var (hidden, locked, layers) = _bus.Read(d =>
        {
            var objs = ids.Select(id => d.AllObjects.FirstOrDefault(o => o.Id == id)).OfType<SceneObject>().ToList();
            return (objs.All(o => o.Hidden), objs.All(o => o.Locked), d.Pages[0].Layers.Select(l => l.Name).Reverse().ToList());
        });
        MenuItem Entry(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
        var move = new MenuItem { Header = "Move to layer" };
        move.ItemsSource = layers.Select(name => Entry(name, () => Exec(new MoveToLayer { Targets = ids, Layer = name }))).ToList();
        menu.ItemsSource = new List<Control> { Entry(hidden ? "Show" : "Hide", ToggleHidden), Entry(locked ? "Unlock" : "Lock", ToggleLocked), new Separator(), move };
    }

    // If all the selected objects already have the state, remove it; otherwise give it to all.
    void ToggleState(bool hidden)
    {
        var ids = Canvas.SelectedIds.ToList();
        if (ids.Count == 0) return;
        var all = _bus.Read(d => ids.All(id => d.AllObjects.FirstOrDefault(o => o.Id == id) is { } o && (hidden ? o.Hidden : o.Locked)));
        var cmd = new SetObjectState { Targets = ids };
        if (hidden) cmd.Hidden = !all; else cmd.Locked = !all;
        if (Exec(cmd) != null && hidden && !all) Canvas.SetSelection([]); // a hidden object does not stay selected on the canvas
    }

    void ToggleHidden() => ToggleState(hidden: true);
    void ToggleLocked() => ToggleState(hidden: false);

    void ShowAll() => ClearState(hidden: true);
    void UnlockAll() => ClearState(hidden: false);

    void ClearState(bool hidden)
    {
        var ids = _bus.Read(d => d.AllObjects.Where(o => hidden ? o.Hidden : o.Locked).Select(o => o.Id).ToList());
        if (ids.Count == 0) return;
        Exec(hidden ? new SetObjectState { Targets = ids, Hidden = false } : new SetObjectState { Targets = ids, Locked = false });
    }
}
