using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;

namespace SacramentalRecords.App;

public partial class App : System.Windows.Application
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        UiSpacing.SetLevel(int.TryParse(SacramentalRecords.App.MainWindow.FileSettings.GetValueOrDefault(SacramentalRecords.App.MainWindow.SpacingSettingKey), out var spacingLevel) ? spacingLevel : 1);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Window_Loaded));

        var splash = new SplashWindow();
        splash.Show();

#if !DEBUG
        var updateCheck = AppUpdater.CheckAndPromptAsync();
        await Task.WhenAll(Task.Delay(1400), updateCheck);
        if (await updateCheck)
        {
            Shutdown();
            return;
        }
#else
        await Task.Delay(1400);
#endif

        var mainWindow = new MainWindow { WindowState = WindowState.Maximized };
        MainWindow = mainWindow;
        mainWindow.Show();
        splash.Close();
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) UiSpacing.ApplyToWindow(window);
    }
}
