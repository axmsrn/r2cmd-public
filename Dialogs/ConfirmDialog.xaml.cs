using System;
using System.Windows;

namespace R2Cmd;

public partial class ConfirmDialog : Window
{
    public enum ConfirmResult
    {
        Yes,
        No,
        Cancel
    }

    public ConfirmResult Result { get; private set; } = ConfirmResult.Cancel;

    public ConfirmDialog(
        string message,
        string title = "Confirm",
        string yesText = "Yes",
        string noText = "No",
        string cancelText = "Cancel",
        bool showCancel = true)
    {
        InitializeComponent();

        Title = title;
        txtMessage.Text = message;
        btnYes.Content = yesText;
        btnNo.Content = noText;
        btnCancel.Content = cancelText;

        btnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) => btnYes.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (ThemeManager.IsDarkTheme)
            Helpers.ApplyDarkTitleBar(this);
    }

    private void BtnYes_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmResult.Yes;
        DialogResult = true;
    }

    private void BtnNo_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmResult.No;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Result = ConfirmResult.Cancel;
        DialogResult = false;
    }
}
