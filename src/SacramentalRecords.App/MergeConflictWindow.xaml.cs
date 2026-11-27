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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) });
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

            // Left value with radio button
            var leftRadio = new RadioButton
            {
                Name = $"Radio_Left_{fieldName}",
                GroupName = fieldName,
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 8, 0)
            };
            Grid.SetColumn(leftRadio, 0);
            Grid.SetRow(leftRadio, 1);
            grid.Children.Add(leftRadio);

            var leftContent = new TextBlock
            {
                Text = FormatValue(leftValue),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(28, 0, 0, 0)
            };
            Grid.SetColumn(leftContent, 0);
            Grid.SetRow(leftContent, 1);
            grid.Children.Add(leftContent);

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

            // Right value with radio button
            var rightRadio = new RadioButton
            {
                Name = $"Radio_Right_{fieldName}",
                GroupName = fieldName,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 4, 8, 0)
            };
            Grid.SetColumn(rightRadio, 2);
            Grid.SetRow(rightRadio, 1);
            grid.Children.Add(rightRadio);

            var rightContent = new TextBlock
            {
                Text = FormatValue(rightValue),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(rightContent, 2);
            Grid.SetRow(rightContent, 1);
            grid.Children.Add(rightContent);

            container.Child = grid;
            ConflictsPanel.Children.Add(container);

            _selectedChoices[fieldName] = leftRadio;
            leftRadio.Checked += (s, e) => _selectedChoices[fieldName] = leftRadio;
            rightRadio.Checked += (s, e) => _selectedChoices[fieldName] = rightRadio;
        }
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
