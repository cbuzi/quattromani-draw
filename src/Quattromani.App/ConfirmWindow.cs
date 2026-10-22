using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Quattromani.App;

/// <summary>"Save changes?" window: Save, Don't save, Cancel.</summary>
sealed class ConfirmWindow : Window
{
    public enum Choice { Save, Discard, Cancel }

    Choice _choice = Choice.Cancel; // closing with the X is the same as Cancel

    ConfirmWindow(string title, string detail, string saveText, string discardText, string cancelText)
    {
        Title = "Quattromani Draw";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var save = new Button { Content = saveText, IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var discard = new Button { Content = discardText, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        save.Click += (_, _) => { _choice = Choice.Save; Close(); };
        discard.Click += (_, _) => { _choice = Choice.Discard; Close(); };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = detail, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 6, 0, 0),
                    Children = { discard, cancel, save },
                },
            },
        };
    }

    public static async Task<Choice> ShowAsync(Window owner, string title, string detail, string saveText = "Save", string discardText = "Don't save", string cancelText = "Cancel")
    {
        var w = new ConfirmWindow(title, detail, saveText, discardText, cancelText);
        await w.ShowDialog(owner);
        return w._choice;
    }
}
