using System;
using System.Windows;
using System.Windows.Input;

namespace R2Cmd;

// Added "Cancel" to handle Escape key and 'X' button
public enum OverwriteChoice { None, Overwrite, Skip, OverwriteAll, SkipAll, Cancel }

public partial class OverwriteDialog : Window
{
    // Default choice is Cancel, so closing via 'X' aborts the operation
    public OverwriteChoice Choice { get; private set; } = OverwriteChoice.Cancel;

    public OverwriteDialog(string message)
    {
        InitializeComponent();
        txtOverwritePrompt.Text = message;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Helpers.ApplyDarkTitleBar(this);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme);

        // Force OS activation so the window doesn't get lost in the background
        this.Activate();
        this.Focus();

        // Delay focus setting slightly until the UI is fully rendered
        // Focus is set to 'Overwrite' button as requested
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            btnOverwrite.Focus();
            System.Windows.Input.Keyboard.Focus(btnOverwrite);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Abort on Escape key
        if (e.Key == Key.Escape)
        {
            Choice = OverwriteChoice.Cancel;
            Close();
        }
    }

    private void BtnOverwrite_Click(object sender, RoutedEventArgs e)
    {
        Choice = OverwriteChoice.Overwrite;
        DialogResult = true;
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        Choice = OverwriteChoice.Skip;
        DialogResult = true;
    }

    private void BtnOverwriteAll_Click(object sender, RoutedEventArgs e)
    {
        Choice = OverwriteChoice.OverwriteAll;
        DialogResult = true;
    }

    private void BtnSkipAll_Click(object sender, RoutedEventArgs e)
    {
        Choice = OverwriteChoice.SkipAll;
        DialogResult = true;
    }
}
