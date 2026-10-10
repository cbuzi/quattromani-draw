using Avalonia.Controls;
using AiDraw.Core;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AiDraw.App;

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
        MenuExportPdf.InputGesture = new KeyGesture(Key.E, mod);
        MenuNew.Click += async (_, _) => await NewDocumentAsync();
        MenuOpen.Click += async (_, _) => await OpenAsync();
        MenuSave.Click += async (_, _) => await SaveAsync(false);
        MenuSaveAs.Click += async (_, _) => await SaveAsync(true);
        MenuExportPdf.Click += async (_, _) => await ExportPdfAsync();
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
                Entry("Export PDF…", () => _ = ExportPdfAsync(), Key.E),
            },
        };
        var edit = new NativeMenuItem("Edit")
        {
            Menu = new NativeMenu { Entry("Undo", () => _bus.Undo()), Entry("Redo", () => _bus.Redo()) },
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
            },
        };
        var help = new NativeMenuItem("Help") { Menu = new NativeMenu { Entry("Connect an AI agent…", App.ShowAbout) } };
        NativeMenu.SetMenu(this, new NativeMenu { file, edit, obj, help });
    }

    void UpdateTitle() =>
        Title = $"{(_files.CurrentPath is { } p ? Path.GetFileName(p) : "Untitled")}{(_files.IsDirty ? "*" : "")} — AiDraw";

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
            FileTypeFilter = [new FilePickerFileType("AiDraw document") { Patterns = [$"*{DocumentFile.Extension}"] }],
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
                path = await PickSaveAsync("Save document", SuggestedName(DocumentFile.Extension), DocumentFile.Extension, "AiDraw document");
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
}
