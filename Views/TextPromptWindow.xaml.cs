using System.Windows;
using System.Windows.Input;

namespace S3VideoManager.Views;

public partial class TextPromptWindow : Window
{
    public string InputText => InputBox.Text.Trim();

    public TextPromptWindow(string title, string prompt, string? placeholder = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptText.Text = prompt;

        if (!string.IsNullOrWhiteSpace(placeholder))
        {
            InputBox.Text = placeholder;
            InputBox.SelectAll();
        }
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private void InputBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }

    private void Accept_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            InputBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
