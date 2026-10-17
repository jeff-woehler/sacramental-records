using System.Windows;
using System.Windows.Input;
using SacramentalRecords.Application;
using SacramentalRecords.Domain;

namespace SacramentalRecords.App;

public partial class PersonEditorWindow : Window
{
    private readonly RegistryService _registry;
    private readonly Person? _original;

    public PersonEditorWindow(RegistryService registry, Person? person = null)
    {
        InitializeComponent();
        WindowControlHelper.RemoveMinimizeButton(this);
        _registry = registry;
        _original = person;
        Title = person is null ? "Add person" : "Edit person";
        if (person is not null)
        {
            GivenNameBox.Text = person.GivenName;
            MiddleNameBox.Text = person.MiddleName;
            FamilyNameBox.Text = person.FamilyName;
            BirthDatePicker.SelectedDate = person.BirthDate?.ToDateTime(TimeOnly.MinValue);
            ChurchDbIdBox.Text = person.ChurchDbId;
            BirthCityBox.Text = person.BirthCity;
            BaptismDatePicker.SelectedDate = person.BaptismDate?.ToDateTime(TimeOnly.MinValue);
            BaptismChurchBox.Text = person.BaptismChurch;
            CreateBaptismBox.Visibility = Visibility.Collapsed;
            StatusBox.SelectedValue = person.Status;
            PdsIdBox.Text = person.PdsMemberId;
            FlocknoteIdBox.Text = person.FlocknoteId;
            MaidenNameBox.Text = person.MaidenName;
            DeceasedDatePicker.SelectedDate = person.DeceasedDate?.ToDateTime(TimeOnly.MinValue);
        }
        else StatusBox.SelectedIndex = 0;
        Loaded += async (_, _) => await ApplyBaptismLinkAsync();
    }

    private async Task ApplyBaptismLinkAsync()
    {
        if (_original is null) return;
        try
        {
            var baptism = (await _registry.GetSacramentsAsync(_original.RecordId)).FirstOrDefault(record =>
                string.Equals(EntryTypeCatalog.Canonicalize(record.SacramentType), "baptism", StringComparison.OrdinalIgnoreCase));
            if (baptism is null) return;
            BaptismDatePicker.SelectedDate = baptism.SacramentDate?.ToDateTime(TimeOnly.MinValue);
            BaptismChurchBox.Text = baptism.Place;
            BaptismDatePicker.IsEnabled = false;
            BaptismChurchBox.IsReadOnly = true;
        }
        catch
        {
            // The person can still be edited if the sacrament refresh is unavailable.
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            Save_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var given = GivenNameBox.Text.Trim();
        var family = FamilyNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(given) || string.IsNullOrWhiteSpace(family))
        {
            MessageBox.Show(this, "Given name and family name are required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var status = StatusBox.SelectedValue?.ToString() ?? "historical";
        var person = new Person(_original?.RecordId ?? Guid.NewGuid(), given,
            Null(MiddleNameBox.Text), family,
            BirthDatePicker.SelectedDate is DateTime date ? DateOnly.FromDateTime(date) : null,
            Null(ChurchDbIdBox.Text), Null(BirthCityBox.Text),
            BaptismDatePicker.SelectedDate is DateTime baptismDate ? DateOnly.FromDateTime(baptismDate) : null,
            Null(BaptismChurchBox.Text), status,
            Null(PdsIdBox.Text), Null(FlocknoteIdBox.Text), _original?.UpdatedUtc ?? DateTimeOffset.MinValue,
            Null(MaidenNameBox.Text),
            DeceasedDatePicker.SelectedDate is DateTime deceased ? DateOnly.FromDateTime(deceased) : null);
        if (person == _original)
        {
            DialogResult = false;
            return;
        }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var saved = await _registry.SavePersonAsync(person, _original?.UpdatedUtc);
            if (_original is null && CreateBaptismBox.IsChecked == true)
            {
                // The person is saved first so the sacrament editor can attach its record.
                var baptismEditor = new SacramentEditorWindow(_registry, saved.RecordId) { Owner = this };
                baptismEditor.ShowDialog();
            }
            DialogResult = true;
        }
        catch (RecordConflictException ex) { MessageBox.Show(this, ex.Message, "Record changed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (ValidationException ex) { MessageBox.Show(this, ex.Message, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to save person", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Mouse.OverrideCursor = null; }
    }

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
