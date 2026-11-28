using System.Globalization;
using System.Windows.Data;
using SacramentalRecords.Infrastructure;

namespace SacramentalRecords.App;

/// <summary>
/// Converter that displays a date with precision awareness.
/// Requires both the date and precision value to format correctly.
/// Usage: Text="{Binding BirthDate, Converter={StaticResource DatePrecisionConverter}, ConverterParameter={Binding BirthDatePrecision}}"
/// </summary>
[ValueConversion(typeof(DateOnly?), typeof(string))]
public class DatePrecisionConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateOnly date)
            return null;

        var precision = parameter as string ?? "day";
        return DatePrecisionExtensions.FormatDateWithPrecision(date.ToString("yyyy-MM-dd"), precision);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Not used for display-only scenarios
        throw new NotImplementedException();
    }
}
