using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VTTranslate.App;

/// <summary>UI-presentation-only helper: hides a status/warning/error TextBlock when its bound string is null/empty, instead of showing an empty line. No effect on what data the ViewModel produces.</summary>
public sealed class EmptyStringToCollapsedConverter : IValueConverter
{
    public static readonly EmptyStringToCollapsedConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
