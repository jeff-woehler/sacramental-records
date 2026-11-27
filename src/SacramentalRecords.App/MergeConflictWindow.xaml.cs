using System.Windows;
using System.Windows.Controls;
using SacramentalRecords.Domain;

namespace SacramentalRecords.App;

public partial class MergeConflictWindow : Window
{
    private readonly Person _person1;
    private readonly Person _person2;
    private readonly MergeResult _mergeResult;
    private readonly Dictionary<string, RadioButton> _selectedChoices = new();

    public MergeConflictWindow(Person person1, Person person2, MergeResult mergeResult)
    {
        InitializeComponent();
        _person1 = person1;
        _person2 = person2;
        _mergeResult = mergeResult;
        BuildConflictUI();
    }

    private void BuildConflictUI()
    {
        foreach (var conflict in _mergeResult.Conflicts)
        {
            var fieldName = conflict.Key;
            var (leftValue, rightValue) = conflict.Value;

            var container = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 241, 231)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(217, 204, 185)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var fieldLabel = new TextBlock
            {
                Text = fieldName,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetColumnSpan(fieldLabel, 3);
            Grid.SetRow(fieldLabel, 0);
            grid.Children.Add(fieldLabel);

            // Left side container with radio button and content
            var leftContainer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 0)
            };

            var leftRadio = new RadioButton
            {
                Name = $"Radio_Left_{fieldName}",
                GroupName = fieldName,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 8, 0),
                MinWidth = 20
            };

            var leftContent = new TextBox
            {
                Text = FormatValue(leftValue),
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = System.Windows.Media.Brushes.Black,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(0),
                Margin = new Thickness(0)
            };

            leftContainer.Children.Add(leftRadio);
            leftContainer.Children.Add(leftContent);

            Grid.SetColumn(leftContainer, 0);
            Grid.SetRow(leftContainer, 1);
            grid.Children.Add(leftContainer);

            var vsLabel = new TextBlock
            {
                Text = "vs.",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(101, 113, 116)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0)
            };
            Grid.SetColumn(vsLabel, 1);
            Grid.SetRow(vsLabel, 1);
            Grid.SetRowSpan(vsLabel, 2);
            grid.Children.Add(vsLabel);

            // Right side container with radio button and content
            var rightContainer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 0)
            };

            var rightRadio = new RadioButton
            {
                Name = $"Radio_Right_{fieldName}",
                GroupName = fieldName,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 8, 0),
                MinWidth = 20
            };

            var rightContent = new TextBox
            {
                Text = FormatValue(rightValue),
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = System.Windows.Media.Brushes.Black,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(0),
                Margin = new Thickness(0)
            };

            rightContainer.Children.Add(rightRadio);
            rightContainer.Children.Add(rightContent);

            Grid.SetColumn(rightContainer, 2);
            Grid.SetRow(rightContainer, 1);
            grid.Children.Add(rightContainer);

            container.Child = grid;
            ConflictsPanel.Children.Add(container);

            // Determine which radio button should be selected by default
            // Prefer non-empty values over empty ones
            bool leftIsEmpty = IsValueEmpty(leftValue);
            bool rightIsEmpty = IsValueEmpty(rightValue);

            RadioButton defaultSelection;
            if (leftIsEmpty && !rightIsEmpty)
            {
                defaultSelection = rightRadio;
                rightRadio.IsChecked = true;
            }
            else if (rightIsEmpty && !leftIsEmpty)
            {
                defaultSelection = leftRadio;
                leftRadio.IsChecked = true;
            }
            else
            {
                // Both empty or both non-empty: default to left
                defaultSelection = leftRadio;
                leftRadio.IsChecked = true;
            }

            _selectedChoices[fieldName] = defaultSelection;
            leftRadio.Checked += (s, e) => _selectedChoices[fieldName] = leftRadio;
            rightRadio.Checked += (s, e) => _selectedChoices[fieldName] = rightRadio;
        }
    }

    private static bool IsValueEmpty(object? value)
    {
        if (value is null)
            return true;
        if (value is string str)
            return string.IsNullOrWhiteSpace(str);
        return false;
    }

    private static string FormatValue(object? value)
    {
        if (value is null)
            return "(empty)";
        return value.ToString() ?? "(empty)";
    }

    public MergeResult GetMergedPerson()
    {
        var choices = new Dictionary<string, string>();
        foreach (var kvp in _selectedChoices)
        {
            choices[kvp.Key] = kvp.Value.Name.Contains("Left") ? "left" : "right";
        }

        var resolvedPerson = MergeService.ResolveMerge(_person1, _person2, choices);
        return new MergeResult(resolvedPerson, _person2.RecordId) { HasConflicts = false };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
