using AiDraw.Mcp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiDraw.App;

/// <summary>"Informazioni su AiDraw", con il comando per collegare un agente (Claude Code, Codex...) via MCP.</summary>
sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "About AiDraw";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var url = $"http://localhost:{McpHost.Port}/mcp";
        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var command = new TextBox
        {
            Text = $"claude mcp add --transport http aidraw {url}", IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 12,
        };
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "AiDraw", FontSize = 26, FontWeight = FontWeight.Bold },
                new TextBlock { Text = $"Version {version}", Opacity = 0.6 },
                new TextBlock
                {
                    Text = "A vector graphics editor you draw with together with an AI agent: you and the agent work on the same document, at the same time.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = "CONNECT AN AGENT", FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.55, Margin = new Thickness(0, 8, 0, 0) },
                new TextBlock { Text = $"AiDraw runs an MCP server at {url} while it is open. In Claude Code, register it once with:", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                command,
                close,
            },
        };
    }
}
