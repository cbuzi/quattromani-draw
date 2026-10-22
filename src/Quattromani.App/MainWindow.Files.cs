using Avalonia;
using Avalonia.Controls;
using Quattromani.Core;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Quattromani.App;

/// <summary>New, open, save, export PDF: File menu, shortcuts, confirmations for unsaved changes.</summary>
public partial class MainWindow
{
    readonly FileSession _files = App.Files;
    bool _closeConfirmed;

    void InitFiles()
    {
        _files.StateChanged += UpdateTitle;
        _files.StateChanged += RefreshRecent;
        Opened += (_, _) => _ = OfferRecoveryAsync();
        var mod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control; // Cmd on Mac, Ctrl elsewhere
        MenuNew.InputGesture = new KeyGesture(Key.N, mod);
        MenuOpen.InputGesture = new KeyGesture(Key.O, mod);
        MenuSave.InputGesture = new KeyGesture(Key.S, mod);
        MenuSaveAs.InputGesture = new KeyGesture(Key.S, mod | KeyModifiers.Shift);
        MenuImport.InputGesture = new KeyGesture(Key.I, mod);
        MenuExportPdf.InputGesture = new KeyGesture(Key.E, mod);
        MenuNew.Click += async (_, _) => await NewDocumentAsync();
        MenuOpen.Click += async (_, _) => await OpenAsync();
        MenuSave.Click += async (_, _) => await SaveAsync(false);
        MenuSaveAs.Click += async (_, _) => await SaveAsync(true);
        MenuImport.Click += async (_, _) => await ImportImageAsync();
        MenuExportPdf.Click += async (_, _) => await ExportPdfAsync();
        MenuExportSvg.Click += async (_, _) => await ExportSvgAsync();
        MenuExportPng.Click += async (_, _) => await ExportPngAsync();
        DragDrop.SetAllowDrop(Canvas, true);
        Canvas.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        Canvas.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var at = Canvas.DocPoint(e.GetPosition(Canvas));
            var paths = (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            for (var i = 0; i < paths.Count; i++) ImportImage(paths[i], new(at.X + 20 * i, at.Y + 20 * i));
        });
        Closing += (_, e) =>
        {
            if (_closeConfirmed || !_files.IsDirty) return;
            e.Cancel = true; // first we ask what to do with the changes
            _ = CloseAfterConfirmAsync();
        };
        UpdateTitle();
        if (OperatingSystem.IsMacOS()) BuildNativeMenu();
        RefreshRecent();
    }

    NativeMenuItem? _recentNative;

    // The "Open recent" menu (in the window or in the system bar) follows the list of recent documents.
    void RefreshRecent()
    {
        var recent = RecentFiles.Load();
        MenuItem Item(string path)
        {
            var item = new MenuItem { Header = Path.GetFileName(path) };
            ToolTip.SetTip(item, path);
            item.Click += async (_, _) => await OpenPathAsync(path);
            return item;
        }
        MenuRecent.ItemsSource = recent.Count == 0 ? new List<MenuItem> { new() { Header = "(none)", IsEnabled = false } } : recent.Select(Item).ToList();
        if (_recentNative?.Menu is { } menu)
        {
            menu.Items.Clear();
            foreach (var path in recent)
            {
                var item = new NativeMenuItem(Path.GetFileName(path));
                item.Click += async (_, _) => await OpenPathAsync(path);
                menu.Items.Add(item);
            }
            _recentNative.IsEnabled = recent.Count > 0;
        }
    }

    // If the app did not close properly last time, offers the backup of the unsaved work.
    async Task OfferRecoveryAsync()
    {
        if (_files.IsDirty || Recovery.FindStale() is not { } stale) return;
        var what = stale.OriginalPath is { } p ? $"“{Path.GetFileName(p)}”" : "an untitled document";
        var choice = await ConfirmWindow.ShowAsync(this, "Recover unsaved work?",
            $"Quattromani Draw did not close properly last time. A backup of {what} from {stale.SavedAt:g} was found.", "Recover", "Discard", "Later");
        try
        {
            if (choice == ConfirmWindow.Choice.Save)
            {
                _files.Recover(stale.File, stale.OriginalPath);
                App.Recovery.Write(); // the new copy before removing the old one
                Recovery.Discard(stale.File);
            }
            else if (choice == ConfirmWindow.Choice.Discard) Recovery.Discard(stale.File);
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    async Task OpenPathAsync(string path)
    {
        CommitTextEdit();
        if (!await ConfirmDiscardAsync()) return;
        try { _files.Open(path); }
        catch (CommandException ex)
        {
            ShowNotice(ex.Message);
            if (!File.Exists(path)) RecentFiles.Remove(path);
            RefreshRecent();
        }
    }

    // On macOS the menus live in the system bar, with the application's name: File and Edit (Undo/Redo without a shortcut
    // in the menu, so as not to take Cmd+Z away from text boxes).
    void BuildNativeMenu()
    {
        FileButton.IsVisible = false; // the File menu in the window is only needed on Windows and Linux
        var mod = KeyModifiers.Meta;
        NativeMenuItem Entry(string header, Action action, Key? key = null, KeyModifiers extra = KeyModifiers.None)
        {
            var item = new NativeMenuItem(header);
            if (key is { } k) item.Gesture = new KeyGesture(k, mod | extra);
            item.Click += (_, _) => action();
            return item;
        }

        var file = new NativeMenuItem("File")
        {
            Menu = new NativeMenu
            {
                Entry("New", () => _ = NewDocumentAsync(), Key.N),
                Entry("Open…", () => _ = OpenAsync(), Key.O),
                (_recentNative = new NativeMenuItem("Open Recent") { Menu = new NativeMenu() }),
                new NativeMenuItemSeparator(),
                Entry("Save", () => _ = SaveAsync(false), Key.S),
                Entry("Save As…", () => _ = SaveAsync(true), Key.S, KeyModifiers.Shift),
                new NativeMenuItemSeparator(),
                Entry("Import…", () => _ = ImportImageAsync(), Key.I),
                Entry("Export PDF…", () => _ = ExportPdfAsync(), Key.E),
                Entry("Export SVG…", () => _ = ExportSvgAsync()),
                Entry("Export PNG…", () => _ = ExportPngAsync()),
            },
        };
        var edit = new NativeMenuItem("Edit")
        {
            Menu = new NativeMenu
            {
                Entry("Undo", () => _bus.Undo()), Entry("Redo", () => _bus.Redo()),
                new NativeMenuItemSeparator(),
                Entry("Cut", () => CopySelection(true)), Entry("Copy", () => CopySelection(false)), Entry("Paste", () => _ = PasteAsync()),
                Entry("Duplicate", Duplicate, Key.D),
            },
        };
        // Object menu: group, stacking order, align and distribute (same commands as the buttons at the top).
        var obj = new NativeMenuItem("Object")
        {
            Menu = new NativeMenu
            {
                Entry("Group", Group, Key.G),
                Entry("Ungroup", Ungroup, Key.G, KeyModifiers.Shift),
                new NativeMenuItemSeparator(),
                Entry("To Front", () => OrderSelected("front"), Key.OemCloseBrackets, KeyModifiers.Shift),
                Entry("Forward One", () => OrderSelected("forward"), Key.OemCloseBrackets),
                Entry("Back One", () => OrderSelected("backward"), Key.OemOpenBrackets),
                Entry("To Back", () => OrderSelected("back"), Key.OemOpenBrackets, KeyModifiers.Shift),
                new NativeMenuItemSeparator(),
                Entry("Align Left", () => AlignSelected("left", null)),
                Entry("Align Centers", () => AlignSelected("center", null)),
                Entry("Align Right", () => AlignSelected("right", null)),
                Entry("Align Top", () => AlignSelected(null, "top")),
                Entry("Align Middles", () => AlignSelected(null, "middle")),
                Entry("Align Bottom", () => AlignSelected(null, "bottom")),
                Entry("Center on Page", () => AlignSelected("center", "middle", "page")),
                new NativeMenuItemSeparator(),
                Entry("Distribute Horizontally", () => DistributeSelected("x", "gaps")),
                Entry("Distribute Vertically", () => DistributeSelected("y", "gaps")),
                new NativeMenuItemSeparator(),
                Entry("Fit Text to Path", FitTextToPath),
                Entry("Trace Image", () => _ = TraceSelectedAsync()),
                Entry("Flip Horizontally", () => FlipSelected("x")),
                Entry("Flip Vertically", () => FlipSelected("y")),
                new NativeMenuItemSeparator(),
                Entry("Hide", ToggleHidden),
                Entry("Show All", ShowAll),
                Entry("Lock", ToggleLocked),
                Entry("Unlock All", UnlockAll),
            },
        };
        var pillItem = new NativeMenuItem("Show Agent Label") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = Canvas.ShowAgentPill };
        pillItem.Click += (_, _) => Canvas.ShowAgentPill = pillItem.IsChecked;
        var view = new NativeMenuItem("View") { Menu = new NativeMenu { pillItem } };
        var help = new NativeMenuItem("Help") { Menu = new NativeMenu { Entry("Connect an AI agent…", App.ShowAbout) } };
        NativeMenu.SetMenu(this, new NativeMenu { file, edit, obj, view, help });
    }

    void UpdateTitle() =>
        Title = $"{(_files.CurrentPath is { } p ? Path.GetFileName(p) : "Untitled")}{(_files.IsDirty ? "*" : "")} — Quattromani Draw";

    string SuggestedName(string extension) =>
        (_files.CurrentPath is { } p ? Path.GetFileNameWithoutExtension(p) : "Untitled") + extension;

    async Task CloseAfterConfirmAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        App.Recovery.Delete(); // deliberate close (saved or discarded): nothing to recover
        _closeConfirmed = true;
        Close();
    }

    // If there are unsaved changes asks what to do with them; false = the user cancelled.
    async Task<bool> ConfirmDiscardAsync()
    {
        if (!_files.IsDirty) return true;
        var name = _files.CurrentPath is { } p ? Path.GetFileName(p) : "Untitled";
        var choice = await ConfirmWindow.ShowAsync(this, $"Save changes to “{name}”?", "If you don't save, your changes will be lost.");
        return choice switch
        {
            ConfirmWindow.Choice.Save => await SaveAsync(false),
            ConfirmWindow.Choice.Discard => true,
            _ => false,
        };
    }

    async Task<string?> PickSaveAsync(string title, string suggested, string extension, string typeName)
    {
        var picked = await GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggested, DefaultExtension = extension.TrimStart('.'), ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [$"*{extension}"] }],
        });
        var path = picked?.TryGetLocalPath();
        return path is null ? null : path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
    }

    public async Task NewDocumentAsync()
    {
        CommitTextEdit();
        if (!await ConfirmDiscardAsync()) return;
        _files.New();
    }

    public async Task OpenAsync()
    {
        CommitTextEdit();
        if (!await ConfirmDiscardAsync()) return;
        var picked = await GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open document", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Quattromani Draw document") { Patterns = [$"*{DocumentFile.Extension}"] }],
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try { _files.Open(path); }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    /// <returns>true if the document was saved.</returns>
    public async Task<bool> SaveAsync(bool saveAs)
    {
        CommitTextEdit();
        CommitRename();
        try
        {
            var path = _files.CurrentPath;
            if (saveAs || path == null)
            {
                path = await PickSaveAsync("Save document", SuggestedName(DocumentFile.Extension), DocumentFile.Extension, "Quattromani Draw document");
                if (path == null) return false;
            }
            _files.Save(path);
            ShowNotice($"Saved {Path.GetFileName(path)}", error: false);
            return true;
        }
        catch (CommandException ex) { ShowNotice(ex.Message); return false; }
    }

    public async Task ExportPdfAsync()
    {
        CommitTextEdit();
        var path = await PickSaveAsync("Export PDF", SuggestedName(".pdf"), ".pdf", "PDF document");
        if (path == null) return;
        try
        {
            await Task.Run(() => _files.ExportPdf(path));
            ShowNotice($"Exported {Path.GetFileName(path)}", error: false);
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    public async Task ExportSvgAsync()
    {
        CommitTextEdit();
        var path = await PickSaveAsync("Export SVG", SuggestedName(SvgFile.Extension), SvgFile.Extension, "SVG image");
        if (path == null) return;
        try
        {
            await Task.Run(() => _files.ExportSvg(path));
            ShowNotice($"Exported {Path.GetFileName(path)}", error: false);
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    // Whole page at 192 dpi (twice the on-screen size), transparent background.
    public async Task ExportPngAsync()
    {
        CommitTextEdit();
        var path = await PickSaveAsync("Export PNG", SuggestedName(".png"), ".png", "PNG image");
        if (path == null) return;
        try
        {
            var (w, h) = await Task.Run(() => _files.ExportPng(path, 2, null));
            ShowNotice($"Exported {Path.GetFileName(path)} ({w} × {h} px)", error: false);
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    public async Task ImportImageAsync()
    {
        CommitTextEdit();
        var picked = await GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Images and SVG") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp", "*.svg"] }],
        });
        var c = Canvas.ViewCenter;
        var paths = picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        for (var i = 0; i < paths.Count; i++) ImportImage(paths[i], new(c.X + 20 * i, c.Y + 20 * i));
    }

    // Native size, reduced if it does not fit in 80% of the page; centered on `center` (a document point).
    void ImportImage(string path, Point center)
    {
        if (path.EndsWith(SvgFile.Extension, StringComparison.OrdinalIgnoreCase)) { ImportSvgFile(path, center); return; }
        try
        {
            var (size, page) = _bus.Read(d => (d.ImageProbe!(File.ReadAllBytes(path)), (d.Pages[0].Width, d.Pages[0].Height)));
            if (size is not { } s) throw new CommandException("Not a readable image (PNG, JPEG, GIF, WebP or BMP).");
            var k = Math.Min(1, Math.Min(0.8 * page.Width / s.Width, 0.8 * page.Height / s.Height));
            double w = s.Width * k, h = s.Height * k;
            var r = _bus.Execute([new CreateShape { Type = "image", Src = path, X = center.X - w / 2, Y = center.Y - h / 2, Width = w, Height = h, Name = Path.GetFileNameWithoutExtension(path) }]);
            Canvas.SetSelection(r.CreatedIds);
        }
        catch (Exception ex) when (ex is CommandException or IOException or UnauthorizedAccessException) { ShowNotice(ex.Message); }
    }

    // As for images: reduced if it does not fit in 80% of the page, centered on `center`.
    void ImportSvgFile(string path, Point center)
    {
        try { ImportSvgText(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), center); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowNotice(ex.Message); }
    }

    void ImportSvgText(string text, string name, Point center)
    {
        try
        {
            var (box, page) = _bus.Read(d => (SvgFile.Measure(d, text), (d.Pages[0].Width, d.Pages[0].Height)));
            var k = Math.Min(1, Math.Min(0.8 * page.Width / Math.Max(box.Width, 1e-9), 0.8 * page.Height / Math.Max(box.Height, 1e-9)));
            double w = box.Width * k, h = box.Height * k;
            var cmd = new ImportSvg { Svg = text, Name = name, X = center.X - w / 2, Y = center.Y - h / 2, Width = w, Height = h };
            var r = _bus.Execute([cmd]);
            Canvas.SetSelection(r.CreatedIds);
            if (cmd.Notes is { } notes) ShowNotice($"Imported, but: {string.Join("; ", notes)}", error: false);
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }

    // Vectorizes the selected image with the default values; the result (a path) sits above the original.
    async Task TraceSelectedAsync()
    {
        var id = _bus.Read(d => Canvas.SelectedIds.Select(i => d.AllObjects.FirstOrDefault(o => o.Id == i)).OfType<ImageObject>().FirstOrDefault()?.Id);
        if (id == null) { ShowNotice("Select an image to trace."); return; }
        try
        {
            var r = await Task.Run(() => _bus.Execute([new TraceImage { Target = id }]));
            Canvas.SetSelection(r.CreatedIds);
        }
        catch (CommandException ex) { ShowNotice(ex.Message); }
    }
}
