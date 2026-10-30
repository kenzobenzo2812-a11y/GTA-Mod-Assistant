using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using GTAModAssistant.Core.Models;

namespace GTAModAssistant.App.Views;

public partial class AssetPreviewWindow : Window
{
    public AssetPreviewWindow(IReadOnlyList<AssetPreviewItem> assets)
    {
        InitializeComponent();
        DataContext = assets;
        if (assets.Count > 0)
        {
            AssetsGrid.SelectedIndex = 0;
        }
    }

    private void OnAssetSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        PreviewImage.Source = null;
        if (AssetsGrid.SelectedItem is not AssetPreviewItem asset)
        {
            DetailsText.Text = "Select an asset to inspect.";
            return;
        }

        DetailsText.Text = $"{asset.Details}{Environment.NewLine}{asset.RelativePath}";
        if (!asset.CanRenderImage)
        {
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1000;
            image.UriSource = new Uri(asset.FullPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            PreviewImage.Source = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or FormatException)
        {
            DetailsText.Text += $"{Environment.NewLine}Image preview failed: {ex.Message}";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
