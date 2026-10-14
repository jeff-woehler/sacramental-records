using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;


namespace SacramentalRecords.App;

internal sealed record UpdateManifest(string Version, string Package, string Sha256, string? Notes);

internal static class AppUpdater
{
    public const string SettingKey = "SACRAMENTAL_RECORDS_UPDATE_MANIFEST";
    public const string DefaultManifestLocation =
        "https://raw.githubusercontent.com/jeff-woehler/sacramental-records/main/releases/latest.json";

    private const string SettingsFileName = "SacramentalRecords.settings.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Returns true when an update was started and the app should exit.</summary>
    public static async Task<bool> CheckAndPromptAsync()
    {
        var location = MainWindow.FileSettings.GetValueOrDefault(SettingKey);
        if (location is null)
        {
            MainWindow.FileSettings[SettingKey] = DefaultManifestLocation;
            MainWindow.SaveFileSettings();
            location = DefaultManifestLocation;
        }
        if (string.IsNullOrWhiteSpace(location)) return false;

        Uri manifestUri;
        UpdateManifest manifest;
        try
        {
            manifestUri = ToUri(location);
            var manifestBytes = StripUtf8Bom(await ReadBytesAsync(manifestUri));
            manifest = JsonSerializer.Deserialize<UpdateManifest>(
                manifestBytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Empty update manifest.");
        }
        catch (Exception ex)
        {
            LogSkippedCheck(ex);
            return false;
        }

        if (!Version.TryParse(manifest.Version, out var available)
            || !Version.TryParse(App.Version, out var current)
            || available <= current)
            return false;

                var notes = string.IsNullOrWhiteSpace(manifest.Notes) ? "" : $"\n\nWhat's new:\n{manifest.Notes}";
        var prompt = new UpdatePromptWindow(
            "Update available",
            "A new version is available",
            $"Installed: v{App.Version}\nAvailable: v{available.ToString(3)}{notes}\n\nUpdate now? The app will close and restart automatically.",
            confirmation: true);
        prompt.ShowDialog();
        if (!prompt.Accepted) return false;

        try
        {
            await StartUpdateAsync(manifestUri, manifest);
            return true;
        }
                catch (Exception ex)
        {
                        var failurePrompt = new UpdatePromptWindow(
                "Update failed",
                "The update could not be installed",
                $"The current version will continue to run.\n\n{ex.Message}",
                confirmation: false);
                        failurePrompt.ShowDialog();
            return false;
        }
    }

    private static async Task StartUpdateAsync(Uri manifestUri, UpdateManifest manifest)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        EnsureWritable(installDir);

        if (manifest.Package.Contains('/') || manifest.Package.Contains('\\') || !manifest.Package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update manifest names an invalid package file.");

        var zipBytes = await ReadBytesAsync(new Uri(manifestUri, manifest.Package));
        var actualHash = Convert.ToHexString(SHA256.HashData(zipBytes));
        if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update failed its integrity check.");

        var workDir = Path.Combine(Path.GetTempPath(), "SacramentalRecordsUpdate");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        var stagingDir = Path.Combine(workDir, "staging");
        Directory.CreateDirectory(stagingDir);

        using (var stream = new MemoryStream(zipBytes))
            ZipFile.ExtractToDirectory(stream, stagingDir);

        var exeName = Path.GetFileName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("Unable to locate the running executable.");
        if (!File.Exists(Path.Combine(stagingDir, exeName)))
            throw new InvalidDataException($"The update package does not contain {exeName}.");

        var scriptPath = Path.Combine(workDir, "apply-update.cmd");
        var logPath = Path.Combine(workDir, "update.log");
        File.WriteAllText(scriptPath, BuildScript(Environment.ProcessId, stagingDir, installDir, exeName, logPath), Encoding.Default);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{scriptPath}\"\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = workDir
        });
    }

    private static string BuildScript(int pid, string stagingDir, string installDir, string exeName, string logPath) => $"""
        @echo off
        :wait
        tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
        if not errorlevel 1 (
          timeout /t 1 /nobreak >nul
          goto wait
        )
        robocopy "{stagingDir}" "{installDir}" /E /R:10 /W:2 /XF {SettingsFileName} /LOG:"{logPath}" >nul
        if errorlevel 8 (
          start "" notepad "{logPath}"
          exit /b 1
        )
        start "" /D "{installDir}" "{Path.Combine(installDir, exeName)}"
        exit /b 0
        """;

    private static void EnsureWritable(string directory)
    {
        var probe = Path.Combine(directory, $".update-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException($"This computer account cannot write to the install folder:\n{directory}\n\nAsk an administrator to install the update.", ex);
        }
    }

    private static Uri ToUri(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) ? uri : new Uri(Path.GetFullPath(location));

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static byte[] StripUtf8Bom(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Utf8Bom) ? bytes[Utf8Bom.Length..] : bytes;

    // Debug.WriteLine is compiled out in Release builds, so failures here would otherwise be invisible.
    private static void LogSkippedCheck(Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "update-check.log");
            File.AppendAllText(logPath, $"{DateTime.Now:u} Update check skipped: {ex}\n");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    private static async Task<byte[]> ReadBytesAsync(Uri uri) => uri.Scheme switch
    {
        "https" => await Http.GetByteArrayAsync(uri),
        "file" => await File.ReadAllBytesAsync(uri.LocalPath),
        _ => throw new NotSupportedException($"Unsupported update location: {uri}")
    };
}
