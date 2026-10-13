using System.Windows;

namespace SacramentalRecords.App;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{App.Version}";
    }
}
