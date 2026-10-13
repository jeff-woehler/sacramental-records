using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SacramentalRecords.Application;
using SacramentalRecords.Domain;

namespace SacramentalRecords.App;

public partial class SacramentEditorWindow : Window
{
    private readonly RegistryService _registry;
    private readonly SacramentRecord? _original;
    private readonly Guid _personRecordId;

    public SacramentEditorWindow(RegistryService registry, Guid personRecordId, SacramentRecord? record = null)
    {
        InitializeComponent();
        WindowControlHelper.RemoveMinimizeButton(this);
        _registry = registry;
        _personRecordId = personRecordId;
        _original = record;
        foreach (var type in SacramentTypeCatalog.Types) TypeBox.Items.Add(new ComboBoxItem { Content = type.Label, Tag = type.Value });
        HeadingText.Text = record is null ? "Add sacrament" : "Edit sacrament";
        if (record is null) TypeBox.SelectedIndex = 0;
        else
        {
            // Stored values are canonical keys; accept legacy/display values when loading
            // so an existing record still selects the correct ComboBox item.
            var canonicalType = SacramentTypeCatalog.Canonicalize(record.SacramentType);
            TypeBox.SelectedItem = TypeBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, canonicalType));
            DatePicker.SelectedDate = record.SacramentDate?.ToDateTime(TimeOnly.MinValue);
            NameBox.Text = record.Name;
            PlaceBox.Text = record.Place; MinisterBox.Text = record.Minister; FatherBox.Text = record.FatherName; MotherBox.Text = record.MotherName;
            SponsorOneBox.Text = record.SponsorOne; SponsorTwoBox.Text = record.SponsorTwo;
            SponsorTwoWitnessBox.IsChecked = record.SponsorTwoIsChristianWitness;
            ProxyOneBox.Text = record.ProxyOne; ProxyTwoBox.Text = record.ProxyTwo;
            VolumeBox.Text = record.Volume; PageBox.Text = record.Page; ItemBox.Text = record.Item;
            DetailsBox.Text = record.Details; NotationsBox.Text = record.CanonicalNotations;
        }
        TypeBox.SelectionChanged += (_, _) =>
        {
            UpdateNameLabel();
            UpdateSponsorLabels();
        };
        UpdateNameLabel();
        UpdateSponsorLabels();
    }

    private void UpdateNameLabel()
    {
        NameLabel.Text = (TypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "baptism" or "first_communion" => "Baptismal Name",
            "confirmation" => "Confirmation Name",
            "marriage" => "Spouse Name",
            _ => "Name"
        };
    }

    private void UpdateSponsorLabels()
    {
        var value = (TypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        (SponsorOneLabel.Text, SponsorTwoLabel.Text) = value switch
        {
            "baptism" => ("Godfather", "Godmother"),
            "marriage" => ("Witness 1", "Witness 2"),
            _ => ("Sponsor 1", "Sponsor 2")
        };
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
        var type = (TypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(type))
        {
            MessageBox.Show(this, "Select a sacrament type.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var record = new SacramentRecord(_original?.RecordId ?? Guid.NewGuid(), _original?.PersonRecordId ?? _personRecordId,
            type, DatePicker.SelectedDate is DateTime date ? DateOnly.FromDateTime(date) : null,
            Null(NameBox.Text), Null(PlaceBox.Text), Null(MinisterBox.Text), Null(FatherBox.Text), Null(MotherBox.Text), Null(SponsorOneBox.Text), Null(SponsorTwoBox.Text),
            SponsorTwoWitnessBox.IsChecked == true, Null(ProxyOneBox.Text), Null(ProxyTwoBox.Text),
            Null(VolumeBox.Text), Null(PageBox.Text), Null(ItemBox.Text), Null(DetailsBox.Text), Null(NotationsBox.Text),
            _original?.UpdatedUtc ?? DateTimeOffset.MinValue);
        if (record == _original)
        {
            DialogResult = false;
            return;
        }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            await _registry.SaveSacramentAsync(record, _original?.UpdatedUtc);
            DialogResult = true;
        }
        catch (RecordConflictException ex) { MessageBox.Show(this, ex.Message, "Record changed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (ValidationException ex) { MessageBox.Show(this, ex.Message, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to save sacrament", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Mouse.OverrideCursor = null; }
    }

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
