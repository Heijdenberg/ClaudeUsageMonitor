using System.Runtime.InteropServices;
using System.Windows;

namespace TokenUsage.Monitor;

/// <summary>A run's whole prompt, read from Claude Code's log. Shown only; nothing is stored.</summary>
public partial class PromptWindow : Window
{
    public PromptWindow(string prompt, string details)
    {
        InitializeComponent();
        Text.Text = prompt;
        Details.Text = details;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (MainWindow.UseSystemFrame(this)) WindowFrame.BorderThickness = new Thickness(0);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Text.Text);
            CopiedText.Text = "Kopierad";
        }
        catch (ExternalException ex)   // another program is holding the clipboard
        {
            CopiedText.Text = $"Kunde inte kopiera: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
