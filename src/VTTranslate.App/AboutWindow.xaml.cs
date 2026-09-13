using System.Reflection;
using System.Windows;
using VTTranslate.App.Branding;

namespace VTTranslate.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        if (BrandAssets.HasLogo)
        {
            LogoImage.Source = BrandAssets.LogoImage;
            FallbackWordmark.Visibility = Visibility.Collapsed;
        }
        else
        {
            LogoImage.Visibility = Visibility.Collapsed;
        }

        if (BrandAssets.HasIcon) Icon = BrandAssets.IconImage;

        // The .NET SDK always assigns an assembly version (default 1.0.0.0 if none is set
        // explicitly in the project) — this is a real, reliable source, never fabricated.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version != null ? $"Version {version.ToString(3)}" : "";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
