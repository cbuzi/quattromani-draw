using Avalonia.Controls;
using Avalonia.Input;
using AiDraw.Core;
using AiDraw.Mcp;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AiDraw.App;

public class App : Application
{
    // Piccolo comando per la voce di menu About (nessuna dipendenza da librerie MVVM).
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

    /// <summary>Unico bus dell'app: UI (e in seguito MCP) modificano il documento solo da qui.</summary>
    public static CommandBus Bus { get; } = new(new Document { TextEngine = new SkiaTextEngine() });
    internal static FileSession Files { get; } = new(Bus);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        BuildAppMenu();
    }

    // Menu dell'applicazione (macOS). Va assegnato in Initialize(): Avalonia lo legge subito dopo, e se non lo trova ne crea uno
    // suo con "About Avalonia".
    void BuildAppMenu()
    {
        // About, Nascondi, Esci, con le scorciatoie di sempre.
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
                Entry("About AiDraw", ShowAbout),
                new NativeMenuItemSeparator(),
                Entry("Hide AiDraw", MacApp.Hide, Key.H),
                Entry("Hide Others", MacApp.HideOthers, Key.H, KeyModifiers.Alt),
                Entry("Show All", MacApp.ShowAll),
                new NativeMenuItemSeparator(),
                // Chiudere la finestra principale: se ci sono modifiche non salvate chiede cosa farne.
                Entry("Quit AiDraw", () => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Close(), Key.Q),
            });
        }

    }


    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            var mcp = McpHost.Start(Bus, new SkiaDocumentRenderer(Bus), Files);
            desktop.Exit += (_, _) => mcp.StopAsync().Wait(TimeSpan.FromSeconds(2));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
