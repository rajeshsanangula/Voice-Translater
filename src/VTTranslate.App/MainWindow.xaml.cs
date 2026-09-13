using System.ComponentModel;
using System.Windows;
using VTTranslate.App.Branding;

namespace VTTranslate.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        ApplyBranding();
        Loaded += Window_Loaded;
    }

    /// <summary>Phase 7.1: attempts a silent-only sign-in check once the window is ready — never launches an interactive browser prompt from here (see MainViewModel.InitializeAsync's own doc comment).</summary>
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void ApplyBranding()
    {
        if (BrandAssets.HasIcon) Icon = BrandAssets.IconImage;

        if (BrandAssets.HasLogo)
        {
            HeaderLogoImage.Source = BrandAssets.LogoImage;
            HeaderFallbackWordmark.Visibility = Visibility.Collapsed;
        }
        else
        {
            HeaderLogoImage.Visibility = Visibility.Collapsed;
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        await _viewModel.DisposeAsync();
    }
}
