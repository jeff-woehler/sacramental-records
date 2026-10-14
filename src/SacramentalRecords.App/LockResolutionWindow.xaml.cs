using System.Windows;

namespace SacramentalRecords.App;

public enum LockResolutionChoice
{
    Cancel,
    ReadOnly,
    RemoveLock
}

public partial class LockResolutionWindow : Window
{
    public LockResolutionChoice Choice { get; private set; } = LockResolutionChoice.Cancel;

    public LockResolutionWindow(string lockPath)
    {
        InitializeComponent();
        WindowControlHelper.RemoveMinimizeButton(this);
        LockPathText.Text = lockPath;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(LockResolutionChoice.Cancel);
    private void ReadOnly_Click(object sender, RoutedEventArgs e) => CloseWith(LockResolutionChoice.ReadOnly);
    private void RemoveLock_Click(object sender, RoutedEventArgs e) => CloseWith(LockResolutionChoice.RemoveLock);

    private void CloseWith(LockResolutionChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
