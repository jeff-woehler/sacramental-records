using System.Globalization;
using System.Windows.Data;

namespace SacramentalRecords.App;

public sealed class StatusDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value as string;
        if (string.IsNullOrWhiteSpace(status)) return "—";

        return status.Trim().ToLowerInvariant() switch
        {
            "historical" => "Historical",
            "active_member" => "Active member",
            "non_member" => "Non-member",
            _ => status.Replace('_', ' ')
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
