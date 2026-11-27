using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SacramentalRecords.App;

public sealed class CompactSacramentDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var compactMode = MainWindow.FileSettings.GetValueOrDefault(MainWindow.CompactSacramentDisplaySettingKey);
        return string.Equals(compactMode, "true", StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
