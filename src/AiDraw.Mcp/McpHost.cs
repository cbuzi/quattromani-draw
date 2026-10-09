using AiDraw.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiDraw.Mcp;

/// <summary>Server MCP (HTTP) ospitato nel processo dell'app. Gli LLM sono client del CommandBus come l'umano.</summary>
public static class McpHost
{
    public const int Port = 5577;

    public static WebApplication Start(CommandBus bus, IDocumentRenderer renderer)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://localhost:{Port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(bus);
        builder.Services.AddSingleton(renderer);
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
