using System;
using System.Windows;

namespace R2Cmd;

public partial class MessageDialog : Window
{
    public MessageDialog(string message, string title = "R2cmd")
    {
        InitializeComponent();
        Title = title;
        txtMessage.Text = message;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Helpers.ApplyDarkTitleBar(this);
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Close();

    // Convenient alternative to MessageBox.Show, but in app dark theme.
    public static void Show(Window? owner, string message, string title = "R2cmd")
    {
        var dlg = new MessageDialog(message, title);
        if (owner != null) dlg.Owner = owner;
        dlg.ShowDialog();
    }
}
