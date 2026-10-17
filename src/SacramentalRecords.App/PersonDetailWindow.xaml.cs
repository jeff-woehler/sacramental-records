using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Windows.Input;
using System.Windows.Media;
using SacramentalRecords.Application;
using SacramentalRecords.Domain;
using SacramentalRecords.App.Resources;

namespace SacramentalRecords.App;

public partial class PersonDetailWindow : Window
{
    private readonly RegistryService _registry;
    private Person? _person;
    private SacramentRecord? _baptism;
        private IReadOnlyList<SacramentRecord> _sacraments = [];
    private readonly bool _readOnly;

    public PersonDetailWindow(RegistryService registry, Guid recordId, bool readOnly = false)
    {
        InitializeComponent();
        _readOnly = readOnly;
        WindowControlHelper.RemoveMinimizeButton(this);
        _registry = registry;
        Loaded += async (_, _) => await LoadAsync(recordId);
    }

    private async Task LoadAsync(Guid recordId)
    {
        try
        {
            var person = await _registry.GetPersonAsync(recordId);
            if (person is null) { MessageBox.Show(this, "That person no longer exists.", "Not found", MessageBoxButton.OK, MessageBoxImage.Information); Close(); return; }
            _person = person;
            NameText.Text = string.Join(" ", new[] { person.GivenName, person.MiddleName, person.FamilyName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            BirthText.Text = person.BirthDate?.ToString("d", CultureInfo.CurrentCulture) ?? "—";
            var statusConverter = (StatusDisplayConverter)FindResource("StatusDisplayConverter");
            StatusText.Text = statusConverter.Convert(person.Status, typeof(string), string.Empty, CultureInfo.CurrentCulture)?.ToString() ?? "—";
            PdsText.Text = person.PdsMemberId ?? string.Empty;
            FlocknoteText.Text = person.FlocknoteId ?? string.Empty;
            ChurchDbText.Text = person.ChurchDbId ?? string.Empty;
            BirthCityText.Text = person.BirthCity ?? "—";
            MaidenNameText.Text = person.MaidenName ?? "—";
            DeceasedText.Text = person.DeceasedDate?.ToString("d", CultureInfo.CurrentCulture) ?? "—";
            var records = await _registry.GetSacramentsAsync(person.RecordId);
            _sacraments = records;
            _baptism = records.FirstOrDefault(record =>
                string.Equals(EntryTypeCatalog.Canonicalize(record.SacramentType), "baptism", StringComparison.OrdinalIgnoreCase));
            BaptismDateText.Text = (_baptism?.SacramentDate ?? person.BaptismDate)?.ToString("d", CultureInfo.CurrentCulture) ?? "—";
            SacramentsGrid.ItemsSource = records
                .OrderBy(record => EntryTypeCatalog.SortOrder(record.SacramentType))
                .ThenByDescending(record => record.SacramentDate.HasValue)
                .ThenByDescending(record => record.SacramentDate);
                        StatusBarText.Text = StatusMessages.PersonRecordSummary(person.RecordId, records.Count);
            if (_readOnly)
                _ = Dispatcher.BeginInvoke(DisableEditingButtons);
        }
        catch (Exception ex) { StatusBarText.Text = ex.Message; }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E)
        {
            Edit_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            AddSacrament_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void SacramentsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SacramentsGrid.SelectedItem is SacramentRecord)
            EditSacrament_Click(sender, new RoutedEventArgs());
    }

    private void SacramentsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (SacramentsGrid.SelectedItem is not SacramentRecord) return;
        if (e.Key == Key.Enter)
        {
            EditSacrament_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSacrament_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

        private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || _person is null) return;
        var editor = new PersonEditorWindow(_registry, _person) { Owner = this };
        if (editor.ShowDialog() == true) await LoadAsync(_person.RecordId);
    }

        private async void AddSacrament_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || _person is null) return;
        var editor = new SacramentEditorWindow(_registry, _person.RecordId) { Owner = this };
        if (editor.ShowDialog() == true) await LoadAsync(_person.RecordId);
    }

    private void BaptismCertificate_Click(object sender, RoutedEventArgs e)
    {
        if (_person is null || _baptism is null)
        {
            MessageBox.Show(this, "This person does not have a baptism record to certify.", "Baptism record required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var fullName = string.Join(" ", new[] { _person.GivenName, _person.MiddleName, _person.FamilyName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var dialog = new SaveFileDialog
        {
            Title = "Save baptismal certificate",
            Filter = "PDF document (*.pdf)|*.pdf",
            FileName = $"baptismal-certificate-{MakeSafeFileName(fullName)}.pdf",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            WordBaptismCertificateGenerator.Write(dialog.FileName, _person, _baptism, _sacraments);
            StatusBarText.Text = StatusMessages.CertificateSaved(Path.GetFileName(dialog.FileName));
            if (MessageBox.Show(this, "The certificate was saved. Open it now?", "Certificate created", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to create certificate", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

        private void DisableEditingButtons()
    {
        foreach (var button in Descendants<Button>(this))
            button.IsEnabled = button == CertificateButton;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

        private async void EditSacrament_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || _person is null) return;
        var record = (sender as FrameworkElement)?.DataContext as SacramentRecord ?? SacramentsGrid.SelectedItem as SacramentRecord;
        if (record is null) return;
        var editor = new SacramentEditorWindow(_registry, _person.RecordId, record) { Owner = this };
        if (editor.ShowDialog() == true) await LoadAsync(_person.RecordId);
    }

        private async void DeleteSacrament_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly) return;
        var record = (sender as FrameworkElement)?.DataContext as SacramentRecord ?? SacramentsGrid.SelectedItem as SacramentRecord;
        if (record is null) return;
        var type = EntryTypeCatalog.Display(record.SacramentType);
                        var description = IsSacramentType(record.SacramentType) ? $"{type.ToLowerInvariant()} sacrament record" : $"{type.ToLowerInvariant()} entry";
        if (MessageBox.Show(this, $"Delete this {description}? This cannot be undone.", "Confirm deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _registry.DeleteSacramentAsync(record.RecordId);
            if (_person is not null) await LoadAsync(_person.RecordId);
        }
        catch (RecordConflictException ex) { MessageBox.Show(this, ex.Message, "Record changed", MessageBoxButton.OK, MessageBoxImage.Warning); if (_person is not null) await LoadAsync(_person.RecordId); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to delete entry", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static bool IsSacramentType(string? value) => EntryTypeCatalog.Canonicalize(value) is "baptism" or "first_communion" or "confirmation" or "marriage" or "holy_orders";

        private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly) return;
        if (_person is null || MessageBox.Show(this, "Delete this person and all entries? This cannot be undone.", "Confirm deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _registry.DeletePersonAsync(_person.RecordId); DialogResult = true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to delete person", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
