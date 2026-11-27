using System.Windows;
using System.Windows.Input;

namespace SacramentalRecords.App;

public partial class IdEditorWindow : Window
{
    public string? Result { get; private set; }

    public IdEditorWindow(string idType, string? currentValue)
    {
        InitializeComponent();
        WindowControlHelper.RemoveMinimizeButton(this);
        WindowControlHelper.RemoveMaximizeButton(this);
        
        Title = $"Edit {idType}";
        IdValueBox.Text = currentValue ?? string.Empty;
        
        (TitleText.Text, DescriptionText.Text) = idType switch
        {
            "Church DB" => ("Edit Church Database ID", "Enter or modify the Church Database ID for this person."),
            "PDS" => ("Edit PDS Member ID", "Enter or modify the PDS (Parish Data System) Member ID for this person."),
            "Flocknote" => ("Edit Flocknote ID", "Enter or modify the Flocknote ID for this person."),
            _ => ("Edit ID", "Enter or modify the ID value.")
        };
        
        IdValueBox.Focus();
        IdValueBox.SelectAll();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Save_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = string.IsNullOrWhiteSpace(IdValueBox.Text) ? null : IdValueBox.Text.Trim();
        DialogResult = true;
        Close();
    }
}
