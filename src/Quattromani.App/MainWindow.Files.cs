using Avalonia;
using Avalonia.Controls;
using Quattromani.Core;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Quattromani.App;

/// <summary>Nuovo, apri, salva, esporta PDF: menu File, scorciatoie, conferme per le modifiche non salvate.</summary>
public partial class MainWindow
{
    readonly FileSession _files = App.Files;
    bool _closeConfirmed;

    void InitFiles()
    {
        _files.StateChanged += UpdateTitle;
        var mod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control; // Cmd su Mac, Ctrl altrove
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
            e.Cancel = true; // prima si chiede cosa fare delle modifiche
            _ = CloseAfterConfirmAsync();
        };
        UpdateTitle();
        if (OperatingSystem.IsMacOS()) BuildNativeMenu();
    }

    // Su macOS i menu stanno nella barra di sistema, con il nome dell'applicazione: File e Edit (Undo/Redo senza scorciatoia
    // nel menu, per non togliere Cmd+Z alle caselle di testo).
    void BuildNativeMenu()
    {
        FileButton.IsVisible = false; // il menu File nella finestra serve solo su Windows e Linux
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
                new NativeMenuItemSeparator(),
                Entry("Save", () => _ = SaveAsync(false), Key.S),
                Entry("Save As…", () => _ = SaveAsync(true), Key.S, KeyModifiers.Shift),
                new NativeMenuItemSeparator(),
                Entry("Import…", () => _ = ImportImageAsync(), Key.I),
                Entry("Export PDF…", () => _ = ExportPdfAsync(), Key.E),
                Entry("Export SVG…", () => _ = ExportSvgAsync()),
            },
        };
        var edit = new NativeMenuItem("Edit")
        {
            Menu = new NativeMenu
            {
                Entry("Undo", () => _bus.Undo()), Entry("Redo", () => _bus.Redo()),
                new NativeMenuItemSeparator(),
                Entry("Cut", () => CopySelection(true)), Entry("Copy", () => CopySelection(false)), Entry("Paste", Paste),
                Entry("Duplicate", Duplicate, Key.D),
            },
        };
        // Menu Object: raggruppa, ordine di sovrapposizione, allinea e distribuisci (stessi comandi dei pulsanti in alto).
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
        _closeConfirmed = true;
        Close();
    }

    // Se ci sono modifiche non salvate chiede cosa farne; false = l'utente ha annullato.
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

    /// <returns>true se il documento è stato salvato.</returns>
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

    // Dimensione nativa, ridotta se non entra in 80% della pagina; centrata su `center` (punto del documento).
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

    // Come per le immagini: ridotto se non entra in 80% della pagina, centrato su `center`.
    void ImportSvgFile(string path, Point center)
    {
        try
        {
            var text = File.ReadAllText(path);
            var (box, page) = _bus.Read(d => (SvgFile.Measure(d, text), (d.Pages[0].Width, d.Pages[0].Height)));
            var k = Math.Min(1, Math.Min(0.8 * page.Width / Math.Max(box.Width, 1e-9), 0.8 * page.Height / Math.Max(box.Height, 1e-9)));
            double w = box.Width * k, h = box.Height * k;
            var cmd = new ImportSvg { Svg = text, Name = Path.GetFileNameWithoutExtension(path), X = center.X - w / 2, Y = center.Y - h / 2, Width = w, Height = h };
            var r = _bus.Execute([cmd]);
            Canvas.SetSelection(r.CreatedIds);
            if (cmd.Notes is { } notes) ShowNotice($"Imported, but: {string.Join("; ", notes)}", error: false);
        }
        catch (Exception ex) when (ex is CommandException or IOException or UnauthorizedAccessException) { ShowNotice(ex.Message); }
    }

    // Vettorializza l'immagine selezionata con i valori di default; il risultato (un path) sta sopra l'originale.
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
