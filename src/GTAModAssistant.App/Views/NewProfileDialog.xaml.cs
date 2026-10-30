using System.Windows;

namespace GTAModAssistant.App.Views;

public partial class NewProfileDialog : Window
{
    public string ProfileName => NameBox.Text.Trim();
    public string ProfileDescription => DescriptionBox.Text.Trim();

    public NewProfileDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, "Enter a profile name.", "Profile Name Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
