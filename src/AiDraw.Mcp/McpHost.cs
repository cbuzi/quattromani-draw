using AiDraw.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiDraw.Mcp;

/// <summary>Server MCP (HTTP) ospitato nel processo dell'app. Gli LLM sono client del CommandBus come l'umano.</summary>
public static class McpHost
{
    /// <summary>Porta del server; AIDRAW_PORT la cambia (utile per avere più istanze o fare prove senza toccare quella in uso).</summary>
    public static int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("AIDRAW_PORT"), out var p) ? p : 5577;

    public static WebApplication Start(CommandBus bus, IDocumentRenderer renderer, IFileService files)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://localhost:{Port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(bus);
        builder.Services.AddSingleton(renderer);
        builder.Services.AddSingleton(files);
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<AiDrawTools>();

        var app = builder.Build();
        app.MapMcp("/mcp");

        // Se la porta è occupata l'app deve comunque partire.
        app.StartAsync().ContinueWith(
            t => Console.Error.WriteLine($"MCP server non avviato su porta {Port}: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
        return app;
    }
}
