using System;
using System.Windows;

namespace R2Cmd;

public enum ErrorChoice { Retry, Skip, SkipAll, Cancel }

public partial class ErrorActionDialog : Window
{
    public ErrorChoice Choice { get; private set; } = ErrorChoice.Cancel;

    public string DialogTitle { get; set; }
    public string ErrorMessage { get; set; }

    private readonly bool _focusSkipAll;

    // =========================================================================
    // allowRetry and focusSkipAll both default to the behaviour the copy
    // pipeline already relies on: no Retry button, and the focus on the single
    // Skip so an accidental Enter cannot skip everything at once.
    //
    // Delete passes both: it can genuinely repeat the failed step, and it asks
    // per file inside a tree, where "take everything that is free and list the
    // rest" is the answer most people want after the first prompt.
    // =========================================================================
    public ErrorActionDialog(string message, string title, bool allowRetry = false, bool focusSkipAll = false)
    {
        InitializeComponent();
        DataContext = this;
        DialogTitle = title;
        ErrorMessage = message;

        _focusSkipAll = focusSkipAll;

        if (allowRetry) btnRetry.Visibility = Visibility.Visible;

        if (focusSkipAll)
        {
            // Enter has to agree with the focus, otherwise it would still fire
            // the button marked as default in the markup
            btnSkip.IsDefault = false;
            btnSkipAll.IsDefault = true;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme);

        // Force OS activation so the window doesn't get lost in the background
        this.Activate();
        this.Focus();

        var target = _focusSkipAll ? btnSkipAll : btnSkip;

        // Delay focus setting slightly until the UI is fully rendered
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            target.Focus();
            System.Windows.Input.Keyboard.Focus(target);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BtnRetry_Click(object sender, RoutedEventArgs e)
    {
        Choice = ErrorChoice.Retry;
        DialogResult = true;
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        Choice = ErrorChoice.Skip;
        DialogResult = true;
    }

    private void BtnSkipAll_Click(object sender, RoutedEventArgs e)
    {
        Choice = ErrorChoice.SkipAll;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = ErrorChoice.Cancel;
        DialogResult = false;
    }
}
