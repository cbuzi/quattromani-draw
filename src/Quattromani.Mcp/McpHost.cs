using System.Text.Json;
using Quattromani.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Quattromani.Mcp;

/// <summary>MCP server (HTTP) hosted in the app's process. LLMs are clients of the CommandBus like the human.</summary>
public static class McpHost
{
    /// <summary>Server port; QUATTROMANI_PORT changes it (useful to have several instances or to experiment without touching the one in use).</summary>
    public static int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("QUATTROMANI_PORT"), out var p) ? p : 5577;

    public static WebApplication Start(CommandBus bus, IDocumentRenderer renderer, IFileService files, ISelectionService selection)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://localhost:{Port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // A failing tool call is reported to the agent as an error result: the SDK's stack trace for each one only floods the console.
        builder.Logging.AddFilter("ModelContextProtocol.Server.McpServer", LogLevel.Critical);
        builder.Services.AddSingleton(bus);
        builder.Services.AddSingleton(renderer);
        builder.Services.AddSingleton(files);
        builder.Services.AddSingleton(selection);
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = false).WithTools<QuattromaniTools>();

        var app = builder.Build();
        // A web page open in the user's browser can reach localhost too (cross-origin requests, DNS rebinding): only loopback hosts and origins get through.
        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (!IsLoopback(context.Request.Host.Host) || (origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && IsLoopback(o.Host))))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });
        app.MapMcp("/mcp");

        // If the port is busy the app must start anyway.
        var start = app.StartAsync();
        start.ContinueWith(
            t => Console.Error.WriteLine($"MCP server not started on port {Port}: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
        start.ContinueWith(_ => WriteDiscoveryFile(), TaskContinuationOptions.OnlyOnRanToCompletion);
        return app;
    }

    static bool IsLoopback(string host) => host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    /// <summary>Discovery file for agents: says where the server is even if the port is not the default one.</summary>
    public static string DiscoveryFile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quattromani", "server.json");

    static void WriteDiscoveryFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiscoveryFile)!);
            var version = typeof(McpHost).Assembly.GetName().Version?.ToString(3);
            File.WriteAllText(DiscoveryFile, JsonSerializer.Serialize(new { url = $"http://localhost:{Port}/mcp", port = Port, pid = Environment.ProcessId, version }));
        }
        catch (Exception e) { Console.Error.WriteLine($"Discovery file not written: {e.Message}"); }
    }

    /// <summary>On exit removes the file, but only if it belongs to this process (another instance may have rewritten it).</summary>
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
