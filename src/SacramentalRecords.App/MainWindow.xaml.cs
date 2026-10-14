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
    private const string PackageSettingKey = "SACRAMENTAL_RECORDS_PACKAGE";
    private const string LegacyPackageExtension = ".dbpackage";
    private const string PackageExtension = ".srdb";
    internal const string BaptismTemplateSettingKey = "SACRAMENTAL_RECORDS_BAPTISM_TEMPLATE";
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

                var configuredPath = FileSettings.GetValueOrDefault(PackageSettingKey);
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
        var dialog = new OpenFileDialog
        {
            Filter = "Sacramental Records packages (*.srdb;*.dbpackage)|*.srdb;*.dbpackage|All files (*.*)|*.*",
            Title = "Select the synchronized database package"
        };
        if (dialog.ShowDialog(this) == true) EnableLocalAdmin(dialog.FileName);
    }

            private void Admin_Click(object sender, RoutedEventArgs e)
    {
        var window = new AdministrationSettingsWindow { Owner = this };
        window.ShowDialog();
    }

            private async void EnableLocalAdmin(string path)
    {
        path = MigrateLegacyPackagePath(path);
        if (!File.Exists(path)) { StatusText.Text = StatusMessages.PackageNotFound; return; }

        if (_packageStore is not null)
        {
            await _packageStore.DisposeAsync();
            _packageStore = null;
            _registry = null;
        }

        _packagePath = Path.GetFullPath(path);
        while (true)
        {
            try
            {
                                _packageStore = new SqlitePackageStore(_packagePath);
                _registry = new RegistryService(_packageStore);
                SetReadWriteMode();
                FileSettings[PackageSettingKey] = _packagePath;
                SaveFileSettings();
                ModeText.Text = Path.GetFileName(_packagePath);
                StatusText.Text = StatusMessages.UsingPackage(Path.GetFileName(_packagePath));
                _ = RefreshPeopleAsync();
                return;
            }
            catch (PackageLockedException ex)
            {
                                // MainWindow may still be loading the saved package from its constructor,
                // before it has been shown. An unshown Window cannot be used as a WPF owner.
                var lockDialog = new LockResolutionWindow(ex.LockPath);
                if (IsVisible) lockDialog.Owner = this;
                lockDialog.ShowDialog();

                if (lockDialog.Choice == LockResolutionChoice.ReadOnly)
                {
                    try
                    {
                        _packageStore = new SqlitePackageStore(_packagePath, readOnly: true);
                        _registry = new RegistryService(_packageStore);
                        SetReadOnlyMode();
                        ModeText.Text = $"{Path.GetFileName(_packagePath)} (read-only)";
                        StatusText.Text = "Opened read-only because another session is using this package.";
                        _ = RefreshPeopleAsync();
                    }
                    catch (Exception readOnlyException)
                    {
                        StatusText.Text = readOnlyException.Message;
                    }
                    return;
                }

                                if (lockDialog.Choice == LockResolutionChoice.Cancel)
                {
                    System.Windows.Application.Current.Shutdown();
                    return;
                }

                if (!SqlitePackageStore.TryRemoveStaleLock(_packagePath))
                {
                    MessageBox.Show(
                        this,
                        "The lock is currently in use or cannot be removed. Close the other application session and try Open again.",
                        "Package still in use",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    StatusText.Text = "The package is still locked.";
                    return;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
                return;
            }
        }
    }

        private void SetReadOnlyMode()
        {
            SaveButton.IsEnabled = false;
            AddButton.IsEnabled = false;
            EditButton.IsEnabled = true;
            EditButton.Content = "View";
        }

        private void SetReadWriteMode()
        {
            SaveButton.IsEnabled = true;
            AddButton.IsEnabled = true;
            EditButton.IsEnabled = true;
            EditButton.Content = "Edit";
        }

        private static string MigrateLegacyPackagePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.EndsWith(LegacyPackageExtension, StringComparison.OrdinalIgnoreCase)) return fullPath;

        var newPath = Path.ChangeExtension(fullPath, PackageExtension);
        if (!File.Exists(fullPath))
        {
            // This also repairs settings after a successful rename if the settings
            // file could not be written (for example, a read-only install folder).
            if (File.Exists(newPath))
            {
                FileSettings[PackageSettingKey] = newPath;
                SaveFileSettings();
                return newPath;
            }

            return fullPath;
        }

        // Never rename over an existing file. If both names exist, prefer the new
        // name and leave the legacy file untouched for manual recovery.
        if (File.Exists(newPath))
        {
            FileSettings[PackageSettingKey] = newPath;
            SaveFileSettings();
            return newPath;
        }

        // A live legacy lock means another session is using the old name. Leave it
        // alone; the migration will be retried on the next startup.
        if (File.Exists(fullPath + ".lock") && !SqlitePackageStore.TryRemoveStaleLock(fullPath)) return fullPath;

        try
        {
            File.Move(fullPath, newPath);
            FileSettings[PackageSettingKey] = newPath;
            SaveFileSettings();
            return newPath;
        }
        catch (IOException)
        {
            return fullPath;
        }
        catch (UnauthorizedAccessException)
        {
            return fullPath;
        }
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
        var detail = new PersonDetailWindow(_registry, person.RecordId, _packageStore?.IsReadOnly == true) { Owner = this };
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
