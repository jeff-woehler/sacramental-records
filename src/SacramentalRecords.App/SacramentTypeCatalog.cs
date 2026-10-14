using System.Globalization;
using System.Windows.Data;

namespace SacramentalRecords.App;

internal static class EntryTypeCatalog
{
    internal static readonly (string Value, string Label)[] Types =
    [
        ("baptism", "Baptism"),
        ("first_communion", "First Communion"),
        ("confirmation", "Confirmation"),
        ("marriage", "Marriage"),
        ("holy_orders", "Holy Orders"),
        ("diaconate", "Diaconate"),
        ("religious_profession", "Religious Profession")
    ];

    internal static string? Canonicalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var key = value.Trim().ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_');

        return key switch
        {
            "baptism" => "baptism",
            "first_communion" or "communion" => "first_communion",
            "confirmation" => "confirmation",
            "marriage" => "marriage",
            "holy_orders" or "holy_order" => "holy_orders",
            "diaconate" => "diaconate",
            "religious_profession" => "religious_profession",
            _ => null
        };
    }

    internal static string Display(string? value)
    {
        var canonical = Canonicalize(value);
        return Types.FirstOrDefault(type => type.Value == canonical).Label
            ?? value?.Replace('_', ' ') ?? string.Empty;
    }

    internal static int SortOrder(string? value)
    {
        var canonical = Canonicalize(value);
        var index = Array.FindIndex(Types, type => type.Value == canonical);
        return index < 0 ? int.MaxValue : index;
    }
}

public sealed class EntryNameLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        EntryTypeCatalog.Canonicalize(value?.ToString()) switch
        {
            "baptism" or "first_communion" => "BAPTISMAL NAME",
            "confirmation" => "CONFIRMATION NAME",
            "marriage" => "SPOUSE NAME",
            _ => "NAME"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class EntryTypeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        EntryTypeCatalog.Display(value?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

