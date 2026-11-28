using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SacramentalRecords.Domain;

namespace SacramentalRecords.App;

/// <summary>
/// A smart date input control that accepts full dates (YYYY-MM-DD) or partial dates (YYYY-MM, YYYY).
/// Provides a calendar picker for full date selection, with text-entry override for partial dates.
/// Displays dates with precision indicators (e.g., "ca. 1965" for year-only dates).
/// </summary>
public partial class PartialDatePicker : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        "SelectedDate", typeof(DateOnly?), typeof(PartialDatePicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDateChanged));

    public static readonly DependencyProperty SelectedPrecisionProperty = DependencyProperty.Register(
        "SelectedPrecision", typeof(string), typeof(PartialDatePicker),
        new FrameworkPropertyMetadata("day", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public DateOnly? SelectedDate
    {
        get => (DateOnly?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    public string SelectedPrecision
    {
        get => (string)GetValue(SelectedPrecisionProperty);
        set => SetValue(SelectedPrecisionProperty, value);
    }

    private bool _isUpdatingFromProperty;

    public PartialDatePicker()
    {
        InitializeComponent();
    }

    private static void OnSelectedDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PartialDatePicker picker)
            picker.UpdateDisplayFromProperty();
    }

    private void UpdateDisplayFromProperty()
    {
        _isUpdatingFromProperty = true;
        try
        {
            if (SelectedDate is null)
            {
                DateTextBox.Text = string.Empty;
                return;
            }

            var date = SelectedDate.Value;
            var precision = SelectedPrecision?.ToLowerInvariant() ?? "day";

            DateTextBox.Text = precision switch
            {
                "year-only" => date.Year.ToString(),
                "year-month" => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                _ => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
        }
        finally
        {
            _isUpdatingFromProperty = false;
        }
    }

    private void DateTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Only allow digits and hyphens
        if (!char.IsDigit(e.Text[0]) && e.Text[0] != '-')
        {
            e.Handled = true;
        }
    }

    private void DateTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFromProperty) return;

        var input = DateTextBox.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            SelectedDate = null;
            SelectedPrecision = "day";
            return;
        }

        // Try to parse the input (supports YYYY, YYYY-MM, YYYY-MM-DD)
        if (PartialDateOnly.TryParse(input, out var partial))
        {
            SelectedDate = partial.Date;
            SelectedPrecision = partial.Precision switch
            {
                DatePrecision.YearOnly => "year-only",
                DatePrecision.YearMonth => "year-month",
                _ => "day"
            };
            UpdateDisplayFromProperty();
        }
        else
        {
            // Invalid input - restore previous value
            MessageBox.Show(
                "Invalid date format. Enter YYYY, YYYY-MM, or YYYY-MM-DD (e.g., 1965, 1965-10, 1965-10-15).",
                "Date Format",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            UpdateDisplayFromProperty();
        }
    }

    private void PickerButton_Click(object sender, RoutedEventArgs e)
    {
        // Create a simple calendar picker window
        var pickerWindow = new DatePickerWindow(SelectedDate) { Owner = Window.GetWindow(this) };
        if (pickerWindow.ShowDialog() == true)
        {
            // User selected a date from calendar - set as full day precision
            SelectedDate = pickerWindow.SelectedDate;
            SelectedPrecision = "day";
            UpdateDisplayFromProperty();
        }
    }
}