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
    /// <param name="Fallbacks">Where the command may live when it is not on the PATH (for example inside the desktop app that ships it); "~" is the home folder.</param>
    public sealed record Agent(string Id, string Name, string Command, string InstallUrl, string[] Fallbacks);

    public static readonly Agent[] Agents =
    [
        new("claude", "Claude Code", "claude", "https://docs.claude.com/en/docs/claude-code", ["~/.local/bin/claude", "~/.claude/local/claude", "/opt/homebrew/bin/claude", "/usr/local/bin/claude"]),
        // The Codex desktop app carries its own command line tool and shares ~/.codex/config.toml with it.
        new("codex", "Codex", "codex", "https://github.com/openai/codex",
            ["/Applications/Codex.app/Contents/Resources/codex-cli/bin/codex", "~/Applications/Codex.app/Contents/Resources/codex-cli/bin/codex", "/opt/homebrew/bin/codex", "/usr/local/bin/codex"]),
        new("opencode", "OpenCode", "opencode", "https://opencode.ai", ["~/.opencode/bin/opencode", "/opt/homebrew/bin/opencode", "/usr/local/bin/opencode"]),
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

        Example `execute` call. `commands` is an array of objects (an array, not a string):
        `{"commands": [{"cmd": "shape.create", "type": "rectangle", "name": "sky", "x": 0, "y": 0, "width": 400, "height": 200, "fill": "#87CEEB"}]}`

        The tool descriptions contain the full command reference with examples.
        """;

    // ---- Per-system scripts -----------------------------------------------------------------------

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    static string UnixScript(Agent a)
    {
        // $AGENT is the command found on the PATH or, failing that, at one of the usual places.
        var run = a.Id switch
        {
            "claude" => $"\"$AGENT\" --mcp-config {Quote(McpConfigFile)} --allowedTools mcp__quattromani",
            // Codex keeps its MCP servers in its own config: (re)register ours, then start.
            "codex" => $"\"$AGENT\" mcp remove quattromani >/dev/null 2>&1; \"$AGENT\" mcp add quattromani --url {Quote(Url)} >/dev/null 2>&1; \"$AGENT\"",
            _ => "\"$AGENT\"",
        };
        var fallbacks = string.Join(" ", a.Fallbacks.Select(p => p.StartsWith("~/") ? "\"$HOME/" + p[2..] + "\"" : Quote(p)));
        // -il: login and interactive shell, so the PATH is the one the user has in their own terminal.
        return $$"""
            #!/bin/zsh -il
            cd {{Quote(WorkspaceDir)}} || exit 1
            AGENT=$(command -v {{a.Command}} 2>/dev/null)
            if [ -z "$AGENT" ]; then
              for p in {{fallbacks}}; do
                if [ -x "$p" ]; then AGENT="$p"; break; fi
              done
            fi
            if [ -z "$AGENT" ]; then
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

    // ---- Choosing and opening the terminal ----------------------------------------------------------

    public sealed record Terminal(string Id, string Name);

    // Linux terminal emulators we know how to drive: (exe, arguments before the script).
    static readonly (string Exe, string[] Args)[] LinuxTerminals =
    [
        ("x-terminal-emulator", ["-e", "bash"]), ("gnome-terminal", ["--", "bash"]), ("konsole", ["-e", "bash"]),
        ("xfce4-terminal", ["-x", "bash"]), ("kitty", ["bash"]), ("alacritty", ["-e", "bash"]), ("xterm", ["-e", "bash"]),
    ];

    static bool AppExists(string name) =>
        Directory.Exists($"/Applications/{name}.app") || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", $"{name}.app"))
        || Directory.Exists($"/System/Applications/Utilities/{name}.app");

    /// <summary>The terminals "Launch agent" can use on this computer; the first is the system default.</summary>
    public static List<Terminal> Terminals()
    {
        var list = new List<Terminal>();
        if (OperatingSystem.IsMacOS())
        {
            list.Add(new("default", "System default"));
            if (AppExists("Terminal")) list.Add(new("Terminal", "Terminal"));
            if (AppExists("iTerm")) list.Add(new("iTerm", "iTerm2"));
        }
        else if (OperatingSystem.IsWindows())
        {
            list.Add(new("default", "System default"));
            if (OnPath("wt.exe")) list.Add(new("wt", "Windows Terminal"));
            list.Add(new("cmd", "Command Prompt"));
        }
        else
        {
            list.Add(new("default", "First one found"));
            list.AddRange(LinuxTerminals.Where(t => OnPath(t.Exe)).Select(t => new Terminal(t.Exe, t.Exe)));
        }
        return list;
    }

    /// <summary>The chosen terminal's id, or "default" if none was chosen or it is no longer installed.</summary>
    public static string CurrentTerminal()
    {
        var chosen = AppSettings.Terminal;
        return chosen != null && Terminals().Any(t => t.Id == chosen) ? chosen : "default";
    }

    public static void ChooseTerminal(string id) => AppSettings.Terminal = id == "default" ? null : id;

    static void Open(string script)
    {
        var terminal = CurrentTerminal();
        if (OperatingSystem.IsMacOS())
        {
            // A .command file opens in the chosen terminal app; "open" needs no permission, unlike scripting the app.
            var args = terminal == "default" ? new[] { script } : ["-a", terminal, script];
            Process.Start(new ProcessStartInfo("open", args) { UseShellExecute = false });
        }
        else if (OperatingSystem.IsWindows())
        {
            if (terminal == "wt") Process.Start(new ProcessStartInfo("wt.exe", ["new-tab", "--title", "Quattromani agent", "cmd", "/c", script]) { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "start", "Quattromani agent", script]) { UseShellExecute = false, CreateNoWindow = true });
        }
        else
        {
            var (exe, args) = LinuxTerminals.FirstOrDefault(t => t.Exe == terminal) is { Exe: not null } chosen ? chosen : LinuxTerminals.FirstOrDefault(t => OnPath(t.Exe));
            if (exe == null)
                throw new InvalidOperationException("No terminal found (tried x-terminal-emulator, gnome-terminal, konsole, xfce4-terminal, kitty, alacritty, xterm). Run the command from the About window in your own terminal.");
            Process.Start(new ProcessStartInfo(exe, [.. args, script]) { UseShellExecute = false });
        }
    }

    static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, exe)));
}
