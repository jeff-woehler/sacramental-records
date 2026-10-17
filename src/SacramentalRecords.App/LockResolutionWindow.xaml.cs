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

        public LockResolutionWindow(string lockPath, string? lockOwnerMachineName = null)
    {
        InitializeComponent();
        WindowControlHelper.RemoveMinimizeButton(this);
        LockPathText.Text = lockPath;
        LockOwnerText.Text = string.IsNullOrWhiteSpace(lockOwnerMachineName)
            ? "The machine holding this lock could not be identified."
            : $"Currently open on machine: {lockOwnerMachineName}";
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
