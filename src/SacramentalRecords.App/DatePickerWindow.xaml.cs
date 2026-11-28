using System.Windows;
using System.Windows.Controls;

namespace SacramentalRecords.App;

public partial class DatePickerWindow : Window
{
    private DateOnly _currentMonth;
    public DateOnly? SelectedDate { get; private set; }

    public DatePickerWindow(DateOnly? initialDate = null)
    {
        InitializeComponent();
        _currentMonth = initialDate?.AddDays(-(initialDate.Value.Day - 1)) ?? new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1);
        RenderCalendar();
    }

    private void RenderCalendar()
    {
        MonthYearText.Text = _currentMonth.ToString("MMMM yyyy");
        CalendarGrid.Children.Clear();
        CalendarGrid.RowDefinitions.Clear();
        for (int i = 0; i < 6; i++)
            CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var firstDay = _currentMonth;
        var daysInMonth = DateTime.DaysInMonth(_currentMonth.Year, _currentMonth.Month);
        var startingDayOfWeek = (int)firstDay.DayOfWeek;

        var currentDate = firstDay.AddDays(-startingDayOfWeek);
        
        for (int row = 0; row < 6; row++)
        {
            for (int col = 0; col < 7; col++)
            {
                var button = new Button
                {
                    Content = currentDate.Day.ToString(),
                    Margin = new Thickness(2),
                    Padding = new Thickness(8, 6, 8, 6),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    BorderBrush = currentDate.Month != _currentMonth.Month 
                        ? System.Windows.Media.Brushes.LightGray 
                        : System.Windows.Media.Brushes.Transparent,
                    Foreground = currentDate.Month != _currentMonth.Month 
                        ? System.Windows.Media.Brushes.LightGray 
                        : System.Windows.Media.Brushes.Black,
                    Cursor = currentDate.Month == _currentMonth.Month ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
                    IsEnabled = currentDate.Month == _currentMonth.Month
                };

                if (currentDate == DateOnly.FromDateTime(DateTime.Today))
                {
                    button.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 240, 255));
                    button.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215));
                    button.BorderThickness = new Thickness(2);
                }

                var buttonDate = new DateOnly(currentDate.Year, currentDate.Month, currentDate.Day);
                button.Click += (s, e) => SelectDate(buttonDate);

                Grid.SetRow(button, row);
                Grid.SetColumn(button, col);
                CalendarGrid.Children.Add(button);

                currentDate = currentDate.AddDays(1);
            }
        }
    }

    private void SelectDate(DateOnly date)
    {
        SelectedDate = date;
        DialogResult = true;
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        _currentMonth = _currentMonth.AddMonths(-1);
        RenderCalendar();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _currentMonth = _currentMonth.AddMonths(1);
        RenderCalendar();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDate.HasValue)
            DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
