using Avalonia.Controls;
using Avalonia.Input;
using Quattromani.Core;
using Quattromani.Mcp;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Quattromani.App;

public class App : Application
{
    // Small command for the About menu item (no dependency on MVVM libraries).
    sealed class RelayAbout : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => ShowAbout();
    }

    public static void ShowAbout()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            _ = new AboutWindow().ShowDialog(owner);
    }

    /// <summary>The app's single bus: the UI (and later MCP) modify the document only from here.</summary>
    public static CommandBus Bus { get; } = new(new Document { TextEngine = new SkiaTextEngine(), ImageProbe = SceneRenderer.Probe, ImagePixels = SceneRenderer.Pixels });
    internal static FileSession Files { get; } = new(Bus);
    internal static Recovery Recovery { get; } = new(Bus, Files);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        BuildAppMenu();
    }

    // Application menu (macOS). It must be assigned in Initialize(): Avalonia reads it right after, and if it does not find it it creates
    // its own with "About Avalonia".
    void BuildAppMenu()
    {
        // About, Hide, Quit, with the usual shortcuts.
        if (OperatingSystem.IsMacOS())
        {
            NativeMenuItem Entry(string header, Action action, Key? key = null, KeyModifiers extra = KeyModifiers.None)
            {
                var item = new NativeMenuItem(header);
                if (key is { } k) item.Gesture = new KeyGesture(k, KeyModifiers.Meta | extra);
                item.Click += (_, _) => action();
                return item;
            }
            NativeMenu.SetMenu(this, new NativeMenu
            {
                Entry("About Quattromani Draw", ShowAbout),
                new NativeMenuItemSeparator(),
                Entry("Hide Quattromani Draw", MacApp.Hide, Key.H),
                Entry("Hide Others", MacApp.HideOthers, Key.H, KeyModifiers.Alt),
                Entry("Show All", MacApp.ShowAll),
                new NativeMenuItemSeparator(),
                // Closing the main window: if there are unsaved changes it asks what to do with them.
                Entry("Quit Quattromani Draw", () => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Close(), Key.Q),
            });
        }

    }


    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MacApp.SetDockIcon();
            var window = new MainWindow();
            desktop.MainWindow = window;
            Recovery.Start();
            // An unexpected error in a UI handler must not close the app and lose the work: it is logged, a backup copy is
            // made and we carry on.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                CrashLog.Write("UI thread", e.Exception);
                _ = Task.Run(Recovery.Write);
                window.ShowNotice($"Unexpected error: {e.Exception.Message} (details in ~/.quattromani/crash.log). A backup of your work was made; consider saving it.");
                e.Handled = true;
            };
            var mcp = McpHost.Start(Bus, new SkiaDocumentRenderer(Bus), Files, window.Selection);
            desktop.Exit += (_, _) => { McpHost.RemoveDiscoveryFile(); _ = mcp.StopAsync(); }; // without waiting: the server has nothing to save, and waiting for open connections made the closing "lag"
        }

        base.OnFrameworkInitializationCompleted();
    }
}
