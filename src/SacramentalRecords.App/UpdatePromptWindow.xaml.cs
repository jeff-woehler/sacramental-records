using System.Windows;

namespace SacramentalRecords.App;

public partial class UpdatePromptWindow : Window
{
    public bool Accepted { get; private set; }

    public UpdatePromptWindow(string title, string heading, string message, bool confirmation)
    {
        InitializeComponent();
        WindowControlHelper.RemoveMinimizeButton(this);
        Title = title;
        HeadingText.Text = heading;
        MessageText.Text = message;

        if (!confirmation)
        {
            NoButton.Visibility = Visibility.Collapsed;
            YesButton.Content = "OK";
            YesButton.Width = 100;
        }
    }

    private void No_Click(object sender, RoutedEventArgs e) => CloseWith(false);
    private void Yes_Click(object sender, RoutedEventArgs e) => CloseWith(true);

    private void CloseWith(bool accepted)
    {
        Accepted = accepted;
        DialogResult = true;
    }
}
