using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace SacramentalRecords.App;

public partial class AdministrationSettingsWindow : Window
{
    public AdministrationSettingsWindow()
    {
        InitializeComponent();
        WindowControlHelper.RemoveMinimizeButton(this);
        RefreshTemplatePath();
    }

    private void RefreshTemplatePath()
    {
        var configuredPath = MainWindow.FileSettings.GetValueOrDefault(MainWindow.BaptismTemplateSettingKey);
        var usesBuiltInTemplate = string.IsNullOrWhiteSpace(configuredPath);
        TemplatePathText.Text = usesBuiltInTemplate
            ? $"Built-in template ({Path.GetFileName(WordBaptismCertificateGenerator.BundledTemplatePath)})"
            : configuredPath;
        OpenTemplateButton.IsEnabled = !usesBuiltInTemplate;
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        var source = WordBaptismCertificateGenerator.BundledTemplatePath;
        if (!File.Exists(source))
        {
            MessageBox.Show(this, "The bundled baptism certificate template could not be found.", "Template unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save a copy of the baptism certificate template",
            Filter = "Word document (*.docx)|*.docx",
            FileName = "baptism-certificate-template.docx",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.Copy(source, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to save template", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the baptism certificate Word template",
            Filter = "Word document (*.docx)|*.docx|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!string.Equals(Path.GetExtension(dialog.FileName), ".docx", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Please select a Word .docx template file.", "Invalid template", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MainWindow.FileSettings[MainWindow.BaptismTemplateSettingKey] = Path.GetFullPath(dialog.FileName);
        MainWindow.SaveFileSettings();
        RefreshTemplatePath();
    }

    private void ClearTemplate_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.FileSettings[MainWindow.BaptismTemplateSettingKey] = string.Empty;
        MainWindow.SaveFileSettings();
        RefreshTemplatePath();
    }

    private void OpenTemplate_Click(object sender, RoutedEventArgs e)
    {
        var configuredPath = MainWindow.FileSettings.GetValueOrDefault(MainWindow.BaptismTemplateSettingKey);
        if (string.IsNullOrWhiteSpace(configuredPath)) return;
        if (!File.Exists(configuredPath))
        {
            MessageBox.Show(this, "The selected certificate template could not be found.", "Template unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(configuredPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to open template", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
