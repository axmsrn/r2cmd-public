using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace R2Cmd;

public partial class FavoriteWindow : Window
{
    private readonly AppSettings _settings;

    public ObservableCollection<FavoriteEntry> Items { get; } = new();
    public FavoriteEntry? SelectedResult { get; private set; }

    public FavoriteWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        foreach (var f in settings.Favorites)
            Items.Add(f);

        lstFavorites.ItemsSource = Items;

        if (Items.Count > 0)
            lstFavorites.SelectedIndex = 0;

        Loaded += (s, e) =>
        {
            lstFavorites.Focus();
            if (lstFavorites.SelectedItem != null)
            {
                var item = (ListBoxItem)lstFavorites.ItemContainerGenerator.ContainerFromItem(lstFavorites.SelectedItem);
                item?.Focus();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme);
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (lstFavorites.SelectedItem is FavoriteEntry entry)
        {
            int idx = lstFavorites.SelectedIndex;
            Items.Remove(entry);
            if (Items.Count > 0)
                lstFavorites.SelectedIndex = Math.Min(idx, Items.Count - 1);

            lstFavorites.Focus();
        }
    }

    private void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        if (lstFavorites.SelectedItem is not FavoriteEntry entry) return;

        int idx = lstFavorites.SelectedIndex;
        var result = ShowEditBox("Edit favorite", entry.Name, entry.Path);
        if (result == null) return;

        string newName = result.Value.Name.Trim();
        string newPath = result.Value.Path.Trim();

        if (string.IsNullOrEmpty(newName)) newName = entry.Name;
        if (string.IsNullOrEmpty(newPath)) newPath = entry.Path;

        if (string.Equals(newName, entry.Name, StringComparison.Ordinal) &&
            string.Equals(newPath, entry.Path, StringComparison.Ordinal))
        { lstFavorites.Focus(); return; }

        Items[idx] = new FavoriteEntry { Name = newName, Path = newPath };
        lstFavorites.SelectedIndex = idx;
        lstFavorites.Focus();
    }

    private void BtnUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = lstFavorites.SelectedIndex;
        if (idx <= 0) return;

        var item = Items[idx];
        Items.RemoveAt(idx);
        Items.Insert(idx - 1, item);
        lstFavorites.SelectedIndex = idx - 1;
        lstFavorites.Focus();
    }

    private void BtnDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = lstFavorites.SelectedIndex;
        if (idx < 0 || idx >= Items.Count - 1) return;

        var item = Items[idx];
        Items.RemoveAt(idx);
        Items.Insert(idx + 1, item);
        lstFavorites.SelectedIndex = idx + 1;
        lstFavorites.Focus();
    }

    private void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        if (lstFavorites.SelectedItem is FavoriteEntry entry)
        {
            SelectedResult = entry;
            SaveAndClose(true);
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        SaveAndClose(false);
    }

    private void OnFavoriteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (lstFavorites.SelectedItem is FavoriteEntry entry)
        {
            SelectedResult = entry;
            SaveAndClose(true);
        }
    }

    private void SaveAndClose(bool result)
    {
        if (!result) SelectedResult = null;

        _settings.Favorites = Items.ToList();
        try { _settings.Save(); } catch { }

        DialogResult = result;
    }

    private (string Name, string Path)? ShowEditBox(string title, string name, string path)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };

        window.SetResourceReference(Control.BackgroundProperty, "Brush.Background");
        window.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");

        var grid = new Grid { Margin = new Thickness(15) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lblName = new TextBlock { Text = "Display name:", Margin = new Thickness(0, 0, 0, 4) };
        lblName.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        Grid.SetRow(lblName, 0);

        var txtName = new TextBox
        {
            Text = name,
            Height = 26,
            Padding = new Thickness(3),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        txtName.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        txtName.SetResourceReference(Control.BackgroundProperty, "Brush.Background");
        Grid.SetRow(txtName, 1);

        var lblPath = new TextBlock { Text = "Folder path:", Margin = new Thickness(0, 12, 0, 4) };
        lblPath.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        Grid.SetRow(lblPath, 2);

        var txtPath = new TextBox
        {
            Text = path,
            Height = 26,
            Padding = new Thickness(3),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        txtPath.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        txtPath.SetResourceReference(Control.BackgroundProperty, "Brush.Background");
        Grid.SetRow(txtPath, 3);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnOk = new Button { Content = "OK", Width = 80, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 10, 0) };
        var btnCancel = new Button { Content = "Cancel", Width = 80, Height = 30, IsCancel = true };
        btnPanel.Children.Add(btnOk);
        btnPanel.Children.Add(btnCancel);
        Grid.SetRow(btnPanel, 5);

        btnOk.Click += (s, e) => window.DialogResult = true;

        grid.Children.Add(lblName);
        grid.Children.Add(txtName);
        grid.Children.Add(lblPath);
        grid.Children.Add(txtPath);
        grid.Children.Add(btnPanel);

        window.Content = grid;

        window.SourceInitialized += (s, e) => Helpers.SetTitleBarTheme(window, ThemeManager.IsDarkTheme);
        window.Loaded += (s, e) => { txtName.Focus(); txtName.CaretIndex = txtName.Text.Length; };

        return window.ShowDialog() == true ? (txtName.Text, txtPath.Text) : null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _settings.Favorites = Items.ToList();
        try { _settings.Save(); } catch { }
        base.OnClosing(e);
    }
}
