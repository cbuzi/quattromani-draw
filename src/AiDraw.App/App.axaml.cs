using AiDraw.Core;
using AiDraw.Mcp;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AiDraw.App;

public class App : Application
{
    /// <summary>Unico bus dell'app: UI (e in seguito MCP) modificano il documento solo da qui.</summary>
    public static CommandBus Bus { get; } = new(new Document { TextEngine = new SkiaTextEngine() });

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Rettangolo di prova.
        Bus.Execute([new CreateShape
        {
            Type = "rectangle", Name = "prova", X = 100, Y = 80, Width = 220, Height = 140,
            Fill = "#FFC800", Stroke = "#1F2937", StrokeWidth = 2,
        }]);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            var mcp = McpHost.Start(Bus, new SkiaDocumentRenderer(Bus));
            desktop.Exit += (_, _) => mcp.StopAsync().Wait(TimeSpan.FromSeconds(2));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
