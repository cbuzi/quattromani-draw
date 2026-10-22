using Quattromani.Mcp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Quattromani.App;

/// <summary>"About Quattromani Draw", with the command to connect an agent (Claude Code, Codex...) via MCP.</summary>
sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "About Quattromani Draw";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var url = $"http://localhost:{McpHost.Port}/mcp";
        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var snippets = new (string Agent, string Text)[]
        {
            ("Claude Code", $"claude mcp add --transport http quattromani {url}"),
            ("Codex", $"codex mcp add quattromani --url {url}"),
            ("Other (JSON: Claude Desktop, Cursor...)", $"{{\"mcpServers\":{{\"quattromani\":{{\"type\":\"http\",\"url\":\"{url}\"}}}}}}"),
        };
        var command = new TextBox
        {
            Text = snippets[0].Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 12,
        };
        var agent = new ComboBox { ItemsSource = snippets.Select(x => x.Agent).ToList(), SelectedIndex = 0, MinWidth = 220 };
        agent.SelectionChanged += (_, _) => command.Text = snippets[Math.Max(agent.SelectedIndex, 0)].Text;
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) => { if (GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(command.Text); };
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(28), Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 16,
                    Children =
                    {
                        new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://Quattromani.App/Assets/icon.png"))), Width = 72, Height = 72 },
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = "Quattromani Draw", FontSize = 26, FontWeight = FontWeight.Bold },
                                new TextBlock { Text = $"Version {version}", Opacity = 0.6 },
                            },
                        },
                    },
                },
                new TextBlock
                {
                    Text = "A vector graphics editor you draw with together with an AI agent: you and the agent work on the same document, at the same time.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = "CONNECT AN AGENT", FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.55, Margin = new Thickness(0, 8, 0, 0) },
                new TextBlock { Text = $"Quattromani Draw runs an MCP server at {url} while it is open. Pick your agent and register the server once with:", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { agent, copy } },
                command,
                new TextBlock { Text = "Working with several agents, or several sessions of the same one? Ask each to pass a distinct \"agent\" name in its execute calls: it labels and colors what it draws.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.55 },
                new TextBlock { Text = $"The running server's address is also written to {McpHost.DiscoveryFile}", TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.55 },
                close,
            },
        };
    }
}
