using System.Text.Json;
using Quattromani.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Quattromani.Mcp;

/// <summary>Server MCP (HTTP) ospitato nel processo dell'app. Gli LLM sono client del CommandBus come l'umano.</summary>
public static class McpHost
{
    /// <summary>Porta del server; QUATTROMANI_PORT la cambia (utile per avere più istanze o fare prove senza toccare quella in uso).</summary>
    public static int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("QUATTROMANI_PORT"), out var p) ? p : 5577;

    public static WebApplication Start(CommandBus bus, IDocumentRenderer renderer, IFileService files, ISelectionService selection)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://localhost:{Port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(bus);
        builder.Services.AddSingleton(renderer);
        builder.Services.AddSingleton(files);
        builder.Services.AddSingleton(selection);
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = false).WithTools<QuattromaniTools>();

        var app = builder.Build();
        app.MapMcp("/mcp");

        // Se la porta è occupata l'app deve comunque partire.
        var start = app.StartAsync();
        start.ContinueWith(
            t => Console.Error.WriteLine($"MCP server non avviato su porta {Port}: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
        start.ContinueWith(_ => WriteDiscoveryFile(), TaskContinuationOptions.OnlyOnRanToCompletion);
        return app;
    }

    /// <summary>File di discovery per gli agenti: dice dove è il server anche se la porta non è quella di default.</summary>
    public static string DiscoveryFile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quattromani", "server.json");

    static void WriteDiscoveryFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiscoveryFile)!);
            var version = typeof(McpHost).Assembly.GetName().Version?.ToString(3);
            File.WriteAllText(DiscoveryFile, JsonSerializer.Serialize(new { url = $"http://localhost:{Port}/mcp", port = Port, pid = Environment.ProcessId, version }));
        }
        catch (Exception e) { Console.Error.WriteLine($"File di discovery non scritto: {e.Message}"); }
    }

    /// <summary>Alla chiusura toglie il file, ma solo se è di questo processo (un'altra istanza potrebbe averlo riscritto).</summary>
    public static void RemoveDiscoveryFile()
    {
        try
        {
            if (File.Exists(DiscoveryFile) && JsonDocument.Parse(File.ReadAllText(DiscoveryFile)).RootElement.GetProperty("pid").GetInt32() == Environment.ProcessId)
                File.Delete(DiscoveryFile);
        }
        catch { }
    }
}
