using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SacramentalRecords.Domain;
using SacramentalRecords.Application;
using SacramentalRecords.Infrastructure;
using SacramentalRecords.App.Resources;

namespace SacramentalRecords.App;

public partial class MainWindow : Window
{
    private const string SettingsFileName = "SacramentalRecords.settings.json";
    internal static readonly Dictionary<string, string?> FileSettings = LoadFileSettings();

    private RegistryService? _registry;
    private SqlitePackageStore? _packageStore;
    private string? _packagePath;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{App.Version}";
        StatusText.Text = StatusMessages.OpenPackageToBegin;
        Closing += MainWindow_Closing;

        var configuredPath = FileSettings.GetValueOrDefault("SACRAMENTAL_RECORDS_PACKAGE");
        if (!string.IsNullOrWhiteSpace(configuredPath)) EnableLocalAdmin(configuredPath);
    }

    private static Dictionary<string, string?> LoadFileSettings()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
            if (!File.Exists(path)) return new Dictionary<string, string?>();
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(path))
                ?? new Dictionary<string, string?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>();
        }
        catch (IOException)
        {
            return new Dictionary<string, string?>();
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, string?>();
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_registry is null) { StatusText.Text = StatusMessages.OpenPackageFirst; return; }
        try
        {
            var people = await _registry.SearchPeopleAsync(SearchBox.Text);
            PeopleGrid.ItemsSource = people;
            StatusText.Text = StatusMessages.RecordsFound(people.Count);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void LocalAdmin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SQLite database packages (*.dbpackage)|*.dbpackage", Title = "Select the synchronized database package" };
        if (dialog.ShowDialog(this) == true) EnableLocalAdmin(dialog.FileName);
    }

    private async void EnableLocalAdmin(string path)
    {
        if (!File.Exists(path)) { StatusText.Text = StatusMessages.PackageNotFound; return; }
        try
        {
            if (_packageStore is not null) await _packageStore.DisposeAsync();
            _packagePath = Path.GetFullPath(path);
            _packageStore = new SqlitePackageStore(_packagePath);
            _registry = new RegistryService(_packageStore);
            FileSettings["SACRAMENTAL_RECORDS_PACKAGE"] = _packagePath;
            SaveFileSettings();
            ModeText.Text = "LOCAL SQLITE SESSION — synchronized package";
            StatusText.Text = StatusMessages.UsingPackage(Path.GetFileName(_packagePath));
            _ = RefreshPeopleAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    internal static void SaveFileSettings()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(FileSettings, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }
        catch (IOException)
        {
            // Settings persistence is best effort and should not block workbook access.
        }
        catch (UnauthorizedAccessException)
        {
            // Settings persistence is best effort and should not block workbook access.
        }
    }

    private async void AddPerson_Click(object sender, RoutedEventArgs e)
    {
        if (_registry is null) { MessageBox.Show(this, "Open a database package first.", "Add person"); return; }
        var editor = new PersonEditorWindow(_registry) { Owner = this };
        if (editor.ShowDialog() == true) await RefreshPeopleAsync();
    }

    private void OpenPerson_Click(object sender, RoutedEventArgs e) => OpenSelectedPerson();

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (_packageStore is null)
        {
            StatusText.Text = StatusMessages.OpenPackageFirst;
            return;
        }

        var publishButton = sender as Button;
        if (publishButton is not null) publishButton.IsEnabled = false;
        try
        {
            StatusText.Text = StatusMessages.Publishing;
            await _packageStore.PublishPackageAsync();
            StatusText.Text = StatusMessages.Published(DateTime.Now);
        }
        catch (Exception ex)
        {
            StatusText.Text = StatusMessages.PublishFailed(ex.Message);
        }
        finally
        {
            if (publishButton is not null) publishButton.IsEnabled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            AddPerson_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            LocalAdmin_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = RefreshPeopleAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void PeopleGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedPerson();

    private void PeopleGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OpenSelectedPerson(); }
    }

    private void OpenSelectedPerson()
    {
        if (_registry is null) { StatusText.Text = StatusMessages.OpenPackageFirst; return; }
        if (PeopleGrid.SelectedItem is not Person person) { StatusText.Text = StatusMessages.SelectPersonFirst; return; }
        var detail = new PersonDetailWindow(_registry, person.RecordId) { Owner = this };
        detail.ShowDialog();
        _ = RefreshPeopleAsync();
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing || _packageStore is null) return;

        if (_packageStore.HasUnpublishedChanges)
        {
            var choice = MessageBox.Show(
                this,
                "There are local changes that have not been published to OneDrive.\n\nYes: publish them now\nNo: close and discard the local session changes\nCancel: keep working",
                "Unpublished changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (choice == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                try
                {
                    StatusText.Text = StatusMessages.PublishingBeforeClose;
                    await _packageStore.PublishPackageAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"The changes could not be published. The application will remain open.\n\n{ex.Message}", "Publish failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        e.Cancel = true;
        _isClosing = true;
        try
        {
            StatusText.Text = StatusMessages.ClosingSession;
            await _packageStore.DisposeAsync();
            _packageStore = null;
            _registry = null;

            // The Closing event is still in progress here. Defer the second Close()
            // until WPF has finished unwinding the current close operation.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex)
        {
            _isClosing = false;
            MessageBox.Show(this, $"The local database session could not be closed.\n\n{ex.Message}", "Close failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshPeopleAsync()
    {
        if (_registry is null) return;
        try
        {
            PeopleGrid.ItemsSource = await _registry.SearchPeopleAsync(SearchBox.Text);
            StatusText.Text = _packageStore?.HasUnpublishedChanges == true
                ? StatusMessages.PeopleRefreshedUnpublished
                : StatusMessages.PeopleRefreshed;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
