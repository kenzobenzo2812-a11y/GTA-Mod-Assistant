using System.Windows;

namespace GTAModAssistant.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmButtonText)
    {
        InitializeComponent();
        Title = title;
        TitleBlock.Text = title;
        MessageBlock.Text = message;
        ConfirmBtn.Content = confirmButtonText;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
