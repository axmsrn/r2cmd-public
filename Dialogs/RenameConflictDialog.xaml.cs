using System;
using System.Windows;

namespace R2Cmd;

public enum RenameConflictResult { Overwrite, Rename, Cancel }

public partial class RenameConflictDialog : Window
{
    public RenameConflictResult Result { get; private set; } = RenameConflictResult.Cancel;

    public RenameConflictDialog(string itemName, bool isFolder, bool canOverwrite)
    {
        InitializeComponent();

        if (!canOverwrite)
        {
            if (isFolder)
                txtMessage.Text = $"A non-empty folder named \"{itemName}\" already exists.\nIt cannot be overwritten.";
            else
                txtMessage.Text = $"An item named \"{itemName}\" already exists.\nPlease choose a different name.";

            btnOverwrite.IsEnabled = false;
            btnOverwrite.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (isFolder)
                txtMessage.Text = $"An empty folder named \"{itemName}\" already exists.\nDo you want to overwrite it?";
            else
                txtMessage.Text = $"A file named \"{itemName}\" already exists.\nDo you want to overwrite it?";
        }

        // Set default focus to the 'Rename' button for safety
        Loaded += (s, e) => btnRename.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (ThemeManager.IsDarkTheme)
        {
            Helpers.ApplyDarkTitleBar(this);
        }
    }

    private void BtnOverwrite_Click(object sender, RoutedEventArgs e)
    {
        Result = RenameConflictResult.Overwrite;
        DialogResult = true;
    }

    private void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        Result = RenameConflictResult.Rename;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Result = RenameConflictResult.Cancel;
        DialogResult = false;
    }
}
