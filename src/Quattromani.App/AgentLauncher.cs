using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Quattromani.Mcp;

namespace Quattromani.App;

/// <summary>
/// Starts an AI agent (Claude Code, Codex, OpenCode) in the system's own terminal, already connected to this app's MCP server and
/// running in a workspace folder that tells it how to use the tools. The agent must be installed (and signed in) by the user.
/// </summary>
static class AgentLauncher
{
    public sealed record Agent(string Id, string Name, string Command, string InstallUrl);

    public static readonly Agent[] Agents =
    [
        new("claude", "Claude Code", "claude", "https://docs.claude.com/en/docs/claude-code"),
        new("codex", "Codex", "codex", "https://github.com/openai/codex"),
        new("opencode", "OpenCode", "opencode", "https://opencode.ai"),
    ];

    static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quattromani");
    public static string WorkspaceDir => Path.Combine(DataDir, "workspace");
    static string Url => $"http://localhost:{McpHost.Port}/mcp";
    static string McpConfigFile => Path.Combine(DataDir, "mcp.json");

    /// <summary>Opens a terminal with the agent. Throws <see cref="InvalidOperationException"/> with a readable message if it cannot.</summary>
    public static void Launch(Agent agent)
    {
        try
        {
            PrepareWorkspace();
            var script = Path.Combine(DataDir, $"launch-{agent.Id}{(OperatingSystem.IsWindows() ? ".cmd" : OperatingSystem.IsMacOS() ? ".command" : ".sh")}");
            File.WriteAllText(script, OperatingSystem.IsWindows() ? WindowsScript(agent) : UnixScript(agent), new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Open(script);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"Could not start {agent.Name}: {ex.Message}");
        }
    }

    // ---- Files the agent finds ------------------------------------------------------------------

    static void PrepareWorkspace()
    {
        Directory.CreateDirectory(WorkspaceDir);
        // The server's address can change (QUATTROMANI_PORT), so these are rewritten every time.
        File.WriteAllText(McpConfigFile, JsonSerializer.Serialize(new { mcpServers = new { quattromani = new { type = "http", url = Url } } }));
        File.WriteAllText(Path.Combine(WorkspaceDir, "opencode.json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["$schema"] = "https://opencode.ai/config.json",
                ["mcp"] = new { quattromani = new { type = "remote", url = Url, enabled = true } },
            }));
        // Claude Code reads CLAUDE.md, Codex and OpenCode read AGENTS.md: same text.
        foreach (var name in new[] { "CLAUDE.md", "AGENTS.md" })
            File.WriteAllText(Path.Combine(WorkspaceDir, name), Instructions);
    }

    const string Instructions = """
        # Quattromani Draw

        You are connected to Quattromani Draw, a vector editor, through the MCP server `quattromani`.
        A human is looking at the same document while you draw: you work together, "a quattro mani".

        - Start with `get_document` to see what exists; remember its `version`.
        - Draw with `execute` (one batch = one undo step for the human). Name your objects so you can address them later.
        - Look at your work with `render` after drawing, and fix what looks wrong.
        - Use `get_changes` with the last version you saw to learn what the human changed, and `get_selection` to see what they selected.
        - Use relative positioning (`rel`), layers and names instead of computing coordinates when you can.
        - Locked objects and layers are off limits unless the human asks.
        - If several agents or sessions work at once, pass a distinct `agent` name in your `execute` calls.

        The tool descriptions contain the full command reference with examples.
        """;

    // ---- Per-system scripts -----------------------------------------------------------------------

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    static string UnixScript(Agent a)
    {
        var run = a.Id switch
        {
            "claude" => $"claude --mcp-config {Quote(McpConfigFile)} --allowedTools mcp__quattromani",
            // Codex keeps its MCP servers in its own config: (re)register ours, then start.
            "codex" => $"codex mcp remove quattromani >/dev/null 2>&1; codex mcp add quattromani --url {Quote(Url)} >/dev/null 2>&1; codex",
            _ => a.Command,
        };
        // -il: login and interactive shell, so the PATH is the one the user has in their own terminal.
        return $$"""
            #!/bin/zsh -il
            cd {{Quote(WorkspaceDir)}} || exit 1
            if ! command -v {{a.Command}} >/dev/null 2>&1; then
              echo "{{a.Name}} was not found. Install it first: {{a.InstallUrl}}"
              echo
              read -r "?Press Enter to close"
              exit 1
            fi
            {{run}}
            """.Replace("\r\n", "\n") + "\n";
    }

    static string WindowsScript(Agent a)
    {
        var run = a.Id switch
        {
            "claude" => $"claude --mcp-config \"{McpConfigFile}\" --allowedTools mcp__quattromani",
            "codex" => $"codex mcp remove quattromani >nul 2>&1\r\ncodex mcp add quattromani --url {Url} >nul 2>&1\r\ncodex",
            _ => a.Command,
        };
        return $"@echo off\r\ncd /d \"{WorkspaceDir}\"\r\nwhere {a.Command} >nul 2>&1\r\nif errorlevel 1 (\r\n  echo {a.Name} was not found. Install it first: {a.InstallUrl}\r\n  pause\r\n  exit /b 1\r\n)\r\n{run}\r\n";
    }

    // ---- Opening the terminal --------------------------------------------------------------------

    static void Open(string script)
    {
        if (OperatingSystem.IsMacOS())
        {
            // A .command file opens in the user's terminal app (Terminal by default) without asking for any permission.
            Process.Start(new ProcessStartInfo("open", [script]) { UseShellExecute = false });
        }
        else if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "start", "Quattromani agent", script]) { UseShellExecute = false, CreateNoWindow = true });
        }
        else
        {
            // Linux has no single way: the first terminal emulator found wins.
            foreach (var (exe, args) in new (string, string[])[]
            {
                ("x-terminal-emulator", ["-e", "bash", script]), ("gnome-terminal", ["--", "bash", script]), ("konsole", ["-e", "bash", script]),
                ("xfce4-terminal", ["-x", "bash", script]), ("kitty", ["bash", script]), ("alacritty", ["-e", "bash", script]), ("xterm", ["-e", "bash", script]),
            })
            {
                if (!OnPath(exe)) continue;
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
                return;
            }
            throw new InvalidOperationException("No terminal found (tried x-terminal-emulator, gnome-terminal, konsole, xfce4-terminal, kitty, alacritty, xterm). Run the command from the About window in your own terminal.");
        }
    }

    static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, exe)));
}
