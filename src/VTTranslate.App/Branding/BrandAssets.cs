using System.IO;
using System.Windows.Media.Imaging;

namespace VTTranslate.App.Branding;

/// <summary>
/// Loads the two officially supplied AUTRAXIS SYSTEMS INC. brand assets from disk at
/// startup — never regenerates, redraws, or approximates them. If the files are not yet
/// present (see Assets/Branding/README.md), every property below is null/false and every
/// consumer falls back to a plain text wordmark, so the app always builds and runs
/// correctly regardless of asset availability.
/// </summary>
public static class BrandAssets
{
    private static readonly string BrandingDirectory =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Branding");

    public static readonly string IconPath = Path.Combine(BrandingDirectory, "autraxis-icon.png");
    public static readonly string LogoPath = Path.Combine(BrandingDirectory, "autraxis-logo.png");

    public static BitmapImage? IconImage { get; } = TryLoad(IconPath);
    public static BitmapImage? LogoImage { get; } = TryLoad(LogoPath);

    public static bool HasIcon => IconImage != null;
    public static bool HasLogo => LogoImage != null;

    private static BitmapImage? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // A present-but-corrupt/unreadable file must never crash the app at startup —
            // fall back to the text wordmark exactly as if the file were simply absent.
            return null;
        }
    }
}
