using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using R2Cmd.Providers;

namespace R2Cmd.Controls;

public partial class FilePaneControl : UserControl
{
    #region Properties & Fields

    public string CurrentPath { get; private set; } = "";
    public string SortColumn { get; set; } = "Name";
    public bool SortAscending { get; set; } = true;
    public BulkObservableCollection<FileEntry> Items { get; } = new();

    public FileEntry? SelectedItem => lvFiles.SelectedItem as FileEntry;

    public List<FileEntry> SelectedItems
    {
        get
        {
            var marked = Items.Where(e => e.IsMarked && e.Name != "..").ToList();
            if (marked.Count > 0) return marked;

            var cursor = SelectedItem;
            if (cursor != null && cursor.Name != "..") return new List<FileEntry> { cursor };
            return new List<FileEntry>();
        }
    }

    private CancellationTokenSource? _navCts;
    private System.Windows.Threading.DispatcherTimer? _loadingAnimTimer;
    private int _requestId;
    private bool _isBusy;

    /// <summary>True while this pane is reading a directory. The host uses it for the wait cursor.</summary>
    public bool IsBusy => _isBusy;

    private bool _suppressDriveSelectionChanged;
    private FileSystemWatcher? _watcher;
    private string _quickSearchText = "";

    private FileEntry? _renamingEntry;
    private bool _renameInProgress;
    private FileEntry? _selectionAtMouseDown;
    private System.Windows.Threading.DispatcherTimer? _renameClickTimer;
    private TextBox? _renameBox;

    private Point? _dragStartPoint;
    private FileEntry? _dragStartItem;
    private bool _isDragging;
    private DateTime _dragStartTime;
    private const int DragDelayMs = 180;
    private const double DragThresholdPx = 12;

    // Right-click drag selection state
    private bool _isRightDragSelecting;
    private bool _rightDragTargetState;
    private FileEntry? _lastRightDragItem;
    private Point _rightDragStartPoint;
    private Point _lastRightDragHitPoint;
    private bool _isSearchResults;
    // Independent forward history stack for this specific pane
    private readonly Stack<string> _forwardHistory = new();

    // Total size of a volume never changes while the application runs, and asking
    // for it means touching the drive. One question per root.
    private static readonly ConcurrentDictionary<string, long> s_driveTotals =
        new(StringComparer.OrdinalIgnoreCase);

    // One shared instance instead of a new brush per breadcrumb segment
    private static readonly Brush s_breadcrumbHoverBrush = CreateFrozen(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));

    public event EventHandler? PathChanged;
    public event EventHandler<FileEntry>? ItemExecuted;
    public event EventHandler<string>? StatusMessage;
    public event EventHandler? PaneGotFocus;
    public event EventHandler? BusyStateChanged;
    public event EventHandler? DirectoryModified;
    public event EventHandler<FileEntry>? SizeCalculationRequested;

    public Func<string, string>? SyncPathResolver;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    #endregion

    #region Initialization

    public FilePaneControl()
    {
        InitializeComponent();
        lvFiles.ItemsSource = Items;
        lvFiles.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnColumnHeaderClick));

        // One handler that routes, rather than two that both run on every single
        // mouse move whether or not anything is being dragged
        lvFiles.PreviewMouseMove += LvFiles_MouseMoveRouter;

        lstDrives.PreviewMouseLeftButtonDown += LstDrives_PreviewMouseLeftButtonDown;

        lvFiles.PreviewMouseRightButtonDown += LvFiles_PreviewMouseRightButtonDown;
        lvFiles.PreviewMouseRightButtonUp += LvFiles_PreviewMouseRightButtonUp;

        lstDrives.PreviewMouseRightButtonDown += LstDrives_PreviewMouseRightButtonDown;

        pnlBreadcrumbs.MouseRightButtonDown += PnlBreadcrumbs_MouseRightButtonDown;

        // Dragging the splitter or resizing the window changes how much room the
        // Name column may take without pushing Modified out of view
        lvFiles.SizeChanged += (s, e) => AutoSizeNameColumn();

        Unloaded += (s, e) =>
        {
            StopWatcher();
            StopLoadingAnimation();
        };
    }

    private static Brush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    #endregion

    #region Loading Animation

    private void StartLoadingAnimation(string parentPath)
    {
        StopLoadingAnimation();

        var loadingEntry = new FileEntry
        {
            Name = "..",
            FullPath = parentPath,
            IsFolder = true
        };

        Items.ReplaceAll(new List<FileEntry> { loadingEntry });
        SetSelectedItem(loadingEntry, takeFocus: true);

        bool toggle = false;
        _loadingAnimTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(380)
        };
        _loadingAnimTimer.Tick += (s, e) =>
        {
            toggle = !toggle;
            string newName = toggle ? ". ." : "..";

            if (lvFiles.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem lvi)
            {
                var textBlock = FindVisualTextBlock(lvi);
                if (textBlock != null)
                    textBlock.Text = newName;
            }
        };
        _loadingAnimTimer.Start();
    }

    private void StopLoadingAnimation()
    {
        _loadingAnimTimer?.Stop();
        _loadingAnimTimer = null;
    }

    #endregion

    #region Drives Bar

    public static List<DriveItem> ScanDrives()
    {
        var list = new List<DriveItem>();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady) list.Add(new DriveItem { Name = d.Name.TrimEnd('\\'), Type = d.DriveType });
            }
            catch { }
        }

        list.Add(new DriveItem { Name = "NET", Type = DriveType.Network });
        return list;
    }

    public void ApplyDrives(IEnumerable<DriveItem> drives)
    {
        lstDrives.ItemsSource = drives;
        UpdateDriveSelection();
    }

    public void InitDrives() => ApplyDrives(ScanDrives());

    private void UpdateDriveSelection()
    {
        _suppressDriveSelectionChanged = true;
        try
        {
            // Treat both \\Network and ssh:// paths as "NET" drive
            string root = (CurrentPath.StartsWith(@"\\") || CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                ? "NET"
                : (Path.GetPathRoot(CurrentPath) ?? "").TrimEnd('\\');

            foreach (var obj in lstDrives.Items)
            {
                if (obj is DriveItem di && string.Equals(di.Name, root, StringComparison.OrdinalIgnoreCase))
                {
                    lstDrives.SelectedItem = di;
                    return;
                }
            }
            lstDrives.SelectedItem = null;
        }
        finally { _suppressDriveSelectionChanged = false; }
    }

    private async void OnDriveSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDriveSelectionChanged) return;
        if (lstDrives.SelectedItem is DriveItem di) await NavigateToDrive(di);
    }

    private async void LstDrives_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is DriveItem di &&
            ReferenceEquals(lstDrives.SelectedItem, di))
        {
            await NavigateToDrive(di);
        }
    }

    private async Task NavigateToDrive(DriveItem di)
    {
        string newRoot;
        if (di.Name == "NET")
        {
            newRoot = !string.IsNullOrEmpty(_lastSshPath) ? _lastSshPath : @"\\Network\";
        }
        else
        {
            newRoot = di.Name + "\\";
        }

        string targetPath = SyncPathResolver?.Invoke(newRoot) ?? newRoot;

        PaneGotFocus?.Invoke(this, EventArgs.Empty);

        if (!string.Equals(CurrentPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            await NavigateAsync(targetPath);
        }
        _ = Dispatcher.BeginInvoke(new Action(() => FocusPanel()),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    #endregion

    #region Navigation Core

    private static bool IsAncestorPath(string current, string target)
    {
        if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(target)) return false;

        int currentLength = TrimmedLength(current);
        int targetLength = TrimmedLength(target);

        if (targetLength == 0 || currentLength <= targetLength) return false;

        for (int i = 0; i < targetLength; i++)
        {
            if (Normalize(current[i]) != Normalize(target[i])) return false;
        }

        // The next character must be a separator, otherwise C:\Us would look like
        // an ancestor of C:\Users
        return IsSeparator(current[targetLength]);

        static bool IsSeparator(char c) => c == '\\' || c == '/';
        static char Normalize(char c) => char.ToUpperInvariant(c == '/' ? '\\' : c);
        static int TrimmedLength(string path)
        {
            int length = path.Length;
            while (length > 0 && IsSeparator(path[length - 1])) length--;
            return length;
        }
    }

    public bool CanNavigateForward => _forwardHistory.Count > 0;

    public async Task NavigateForwardAsync()
    {
        if (_forwardHistory.Count > 0)
        {
            string nextPath = _forwardHistory.Pop();
            await NavigateAsync(nextPath, null, isForward: true);
            FocusPanel();
        }
    }

    private void PnlSearchPath_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            string textToCopy = txtSearchPath.Text ?? "";

            // Remove the "Search results: " prefix if present
            const string prefix = "Search results: ";
            if (textToCopy.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                textToCopy = textToCopy.Substring(prefix.Length);

            if (!string.IsNullOrWhiteSpace(textToCopy))
            {
                Clipboard.SetText(textToCopy);
                StatusMessage?.Invoke(this, "Path copied to clipboard.");
            }
        }
        catch { }

        e.Handled = true;
    }

    public async Task NavigateAsync(string newPath, string? itemToSelect = null, bool isForward = false)
    {
        ClearQuickSearch();

        _isSearchResults = false;
        txtPath.Visibility = Visibility.Visible;
        pnlSearchPath.Visibility = Visibility.Collapsed;

        // A slow second click may have armed the rename timer just before the
        // user opened something. Navigation wins.
        _renameClickTimer?.Stop();
        _renameClickTimer = null;

        _navCts?.Cancel();
        _navCts = new CancellationTokenSource();
        var token = _navCts.Token;

        StopLoadingAnimation();
        StopWatcher();
        SetBusy(true);

        bool isSshPath = newPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);
        string? sessionName = null;

        try
        {
            if (isForward)
            {
                // Using forward history (popping) - do not clear it
            }
            else if (IsAncestorPath(CurrentPath, newPath))
            {
                // Navigating UP - save current path to return to it later
                _forwardHistory.Push(CurrentPath);
            }
            else if (!string.Equals(CurrentPath.TrimEnd('\\', '/'), newPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                // Navigating to a different folder (down or side) - clear forward history
                _forwardHistory.Clear();
            }

            CurrentPath = newPath;
            // Remember last network location (SSH, \\Network\LAN, \\PC\share, ...)
            if (newPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
                newPath.StartsWith(@"\\", StringComparison.Ordinal))
                _lastSshPath = newPath;
            txtPath.Text = CurrentPath;

            UpdateBreadcrumbs(CurrentPath);
            pnlBreadcrumbs.Visibility = Visibility.Visible;

            UpdateDriveSelection();
            PathChanged?.Invoke(this, EventArgs.Empty);

            int currentRequestId = ++_requestId;
            bool showLoadingAnimation = false;

            if (isSshPath)
            {
                sessionName = newPath.Substring(6).TrimEnd('/');
                int firstSlash = sessionName.IndexOf('/');
                if (firstSlash > 0) sessionName = sessionName.Substring(0, firstSlash);

                bool sessionOpen = Providers.SshFileSystemProvider.IsSessionOpen(sessionName);

                StatusMessage?.Invoke(this,
                    sessionOpen
                        ? "Reading remote folder..."
                        : $"Connecting to SSH session '{sessionName}'...");

                // Show loading animation only when we actually need to connect
                if (!sessionOpen)
                    showLoadingAnimation = true;
            }
            else if (newPath.Equals(@"\\Network\LAN", StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage?.Invoke(this, "Scanning Windows Network...");
                showLoadingAnimation = true;
            }

            if (showLoadingAnimation)
            {
                StartLoadingAnimation(@"\\Network");
                SetBusy(false);
            }

            // Pass the real cancellation token.
            // If the user clicks drive C: or D:, the heavy network scan
            // must stop immediately to free ThreadPool resources.
            var provider = FileSystemFactory.GetProvider(CurrentPath);
            var (entries, error) = await provider.ReadDirectoryAsync(CurrentPath, token);

            StopLoadingAnimation();

            if (token.IsCancellationRequested || currentRequestId != _requestId) return;

            // --- SSH EVACUATION LOGIC ---
            if (isSshPath && error != null)
            {
                StatusMessage?.Invoke(this, $"SSH Error: Connection lost. Returning to network root. ({error})");

                if (sessionName != null)
                {
                    try { Providers.SshFileSystemProvider.CloseConnection(sessionName); } catch { }
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    _ = NavigateAsync(@"\\Network\");
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                return;
            }

            bool atDriveRoot = string.Equals(
                            Path.GetPathRoot(CurrentPath)?.TrimEnd('\\'),
                            CurrentPath.TrimEnd('\\'),
                            StringComparison.OrdinalIgnoreCase);

            bool hasParentEntry = entries.Count > 0 && entries[0].Name == "..";

            if (!atDriveRoot && !hasParentEntry && provider.CanHandle(CurrentPath))
                entries.Insert(0, new FileEntry { Name = "..", IsFolder = true });

            var sorted = SortEntries(entries);

            Items.ReplaceAll(sorted);
            AutoSizeNameColumn();

            bool virtualEntries = provider is not Providers.LocalDiskProvider;
            IconService.QueueLoad(sorted, virtualEntries, currentRequestId, () => _requestId, Dispatcher);

            StartWatcher(CurrentPath, isLocal: !virtualEntries);

            txtSpace.Text = "";
            _ = UpdateFreeSpaceAsync(provider, CurrentPath, currentRequestId);

            RestoreSelection(itemToSelect);
            StatusMessage?.Invoke(this, error != null ? $"Error: {error}" : "Ready.");
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                SetBusy(false);
            }
        }
    }

    public async Task RefreshAsync() => await NavigateAsync(CurrentPath, SelectedItem?.Name);

    public void ShowSearchResults(string searchRoot, IReadOnlyList<FileEntry> results)
    {
        _navCts?.Cancel();
        StopLoadingAnimation();
        StopWatcher();
        ClearQuickSearch();

        _isSearchResults = true;
        CurrentPath = searchRoot;

        txtPath.Visibility = Visibility.Collapsed;
        pnlSearchPath.Visibility = Visibility.Collapsed;

        pnlBreadcrumbs.Visibility = Visibility.Visible;
        UpdateBreadcrumbs(searchRoot);   // show the search root as breadcrumbs

        UpdateDriveSelection();
        PathChanged?.Invoke(this, EventArgs.Empty);

        int requestId = ++_requestId;

        var items = new List<FileEntry>(results.Count + 1)
    {
        new FileEntry
        {
            Name = "..",
            IsFolder = true,
            FullPath = searchRoot
        }
    };
        items.AddRange(results);

        Items.ReplaceAll(items);

        IconService.QueueLoad(items, virtualEntries: false, requestId, () => _requestId, Dispatcher);

        if (items.Count > 1) SetSelectedItem(items[1], takeFocus: true);
        else if (items.Count > 0) SetSelectedItem(items[0], takeFocus: true);

        StatusMessage?.Invoke(this, $"Search results: {results.Count} item(s). Ctrl+R reloads the folder.");
    }

    private void SetBusy(bool busy)
    {
        if (_isBusy == busy) return;

        _isBusy = busy;
        BusyStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task UpdateFreeSpaceAsync(IFileSystemProvider provider, string path, int requestId)
    {
        string text = await Task.Run(() =>
        {
            try
            {
                var freeSpace = provider.GetFreeSpace(path);
                if (!freeSpace.HasValue) return "";

                string free = Helpers.FormatSize((long)freeSpace.Value);

                string? root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                    return $"{free} free";

                if (!s_driveTotals.TryGetValue(root, out long total))
                {
                    try { total = new DriveInfo(root).TotalSize; }
                    catch { total = 0; }

                    s_driveTotals[root] = total;
                }

                return total > 0 ? $"{free} of {Helpers.FormatSize(total)} free" : $"{free} free";
            }
            catch { return ""; }
        });

        if (requestId == _requestId) txtSpace.Text = text;
    }

    public string GetPersistentPath()
    {
        // Network and SSH paths always restore to the network root
        if (CurrentPath.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase) ||
            CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            return @"\\Network\";

        var (archivePath, _) = ArchiveService.ParseVirtualPath(CurrentPath);
        if (archivePath != null)
        {
            string? dir = Path.GetDirectoryName(archivePath);
            return !string.IsNullOrEmpty(dir) ? dir : archivePath;
        }
        return CurrentPath;
    }

    #endregion

    #region Breadcrumbs

    private void UpdateBreadcrumbs(string path)
    {
        spBreadcrumbs.Children.Clear();

        if (string.IsNullOrEmpty(path)) return;

        // When showing search results — add a clear visual indicator
        if (_isSearchResults)
        {
            var searchLabel = new TextBlock
            {
                Text = "Search results › ",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
                FontWeight = FontWeights.SemiBold
            };
            searchLabel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            searchLabel.SetResourceReference(TextBlock.FontSizeProperty, "AppFilePaneFontSize");
            spBreadcrumbs.Children.Add(searchLabel);
        }

        string separator = path.Contains("/") ? "/" : "\\";
        string prefix = "";
        string remainingPath = path;

        if (path.StartsWith(@"\\"))
        {
            int nextSlash = path.IndexOf('\\', 2);
            if (nextSlash > 0)
            {
                prefix = path.Substring(0, nextSlash + 1);
                remainingPath = path.Substring(nextSlash + 1);
            }
            else
            {
                prefix = path;
                remainingPath = "";
            }
        }
        else if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            int nextSlash = path.IndexOf('/', 6);
            if (nextSlash > 0)
            {
                prefix = path.Substring(0, nextSlash + 1);
                remainingPath = path.Substring(nextSlash + 1);
            }
            else
            {
                prefix = path;
                remainingPath = "";
            }
        }
        else if (path.Contains(":\\"))
        {
            prefix = path.Substring(0, 3);
            remainingPath = path.Substring(3);
        }
        else
        {
            prefix = path;
            remainingPath = "";
        }

        string currentBuiltPath = prefix;
        AddBreadcrumbItem(prefix, currentBuiltPath);

        if (!string.IsNullOrEmpty(remainingPath))
        {
            var parts = remainingPath.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                currentBuiltPath += part + separator;
                AddBreadcrumbItem(part + separator, currentBuiltPath);
            }
        }
    }

    private void AddBreadcrumbItem(string text, string targetPath)
    {
        string displayText = text;
        string trailingSeparator = "";

        if (text.Length > 1 && (text.EndsWith("\\") || text.EndsWith("/")))
        {
            displayText = text.Substring(0, text.Length - 1);
            trailingSeparator = text.Substring(text.Length - 1);
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0),
            Padding = new Thickness(1, 2, 1, 2),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent
        };

        var tb = new TextBlock
        {
            Text = displayText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        tb.SetResourceReference(TextBlock.FontSizeProperty, "AppFilePaneFontSize");

        border.Child = tb;

        border.MouseEnter += (s, e) => border.Background = s_breadcrumbHoverBrush;
        border.MouseLeave += (s, e) => border.Background = Brushes.Transparent;

        border.MouseLeftButtonDown += async (s, e) =>
        {
            e.Handled = true;
            await NavigateAsync(targetPath);
            FocusPanel();
        };

        border.MouseRightButtonDown += (s, e) =>
        {
            try
            {
                Clipboard.SetText(targetPath);
                StatusMessage?.Invoke(this, $"Path copied: {targetPath}");
            }
            catch { }
            e.Handled = true;
        };

        spBreadcrumbs.Children.Add(border);

        if (!string.IsNullOrEmpty(trailingSeparator))
        {
            var sepTb = new TextBlock
            {
                Text = trailingSeparator,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 1, 0)
            };
            sepTb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
            sepTb.SetResourceReference(TextBlock.FontSizeProperty, "AppFilePaneFontSize");

            spBreadcrumbs.Children.Add(sepTb);
        }
    }

    private void PnlBreadcrumbs_MouseLeftButtonDown(object sender, MouseButtonEventArgs? e)
    {
        pnlBreadcrumbs.Visibility = Visibility.Collapsed;
        txtPath.Focus();
        Dispatcher.BeginInvoke(new Action(() => txtPath.SelectAll()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PnlBreadcrumbs_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(CurrentPath);
            StatusMessage?.Invoke(this, "Full path copied to clipboard.");
        }
        catch { }
        e.Handled = true;
    }

    private void TxtPath_LostFocus(object sender, RoutedEventArgs e)
    {
        pnlBreadcrumbs.Visibility = Visibility.Visible;
        txtPath.Text = CurrentPath;
    }

    #endregion

    #region Column Widths

    private const double NameColumnMin = 160;
    private bool _columnWidthsPinned;
    private double _lastAutoNameWidth = -1;

    public List<double> GetColumnWidths()
    {
        var widths = new List<double>();
        if (lvFiles.View is not GridView grid) return widths;

        foreach (var column in grid.Columns)
        {
            double width = column.ActualWidth > 0 ? column.ActualWidth : column.Width;
            widths.Add(double.IsNaN(width) ? 0 : width);
        }

        return widths;
    }

    public void ApplyColumnWidths(IReadOnlyList<double>? widths)
    {
        if (lvFiles.View is not GridView grid) return;
        if (widths == null || widths.Count != grid.Columns.Count) return;

        foreach (double width in widths)
        {
            if (double.IsNaN(width) || width < 20 || width > 4000) return;
        }

        for (int i = 0; i < widths.Count; i++) grid.Columns[i].Width = widths[i];

        _columnWidthsPinned = true;
    }

    private void AutoSizeNameColumn()
    {
        if (_columnWidthsPinned) return;
        if (lvFiles.View is not GridView grid || grid.Columns.Count == 0) return;
        if (lvFiles.ActualWidth <= 0) return;

        var nameColumn = grid.Columns[0];

        if (_lastAutoNameWidth >= 0 && !double.IsNaN(nameColumn.Width) &&
            Math.Abs(nameColumn.Width - _lastAutoNameWidth) > 0.5)
        {
            _columnWidthsPinned = true;
            return;
        }

        double otherColumns = 0;
        for (int i = 1; i < grid.Columns.Count; i++) otherColumns += grid.Columns[i].ActualWidth;

        double width = lvFiles.ActualWidth - otherColumns - SystemParameters.VerticalScrollBarWidth - 8;
        if (width < NameColumnMin) width = NameColumnMin;

        if (Math.Abs(width - _lastAutoNameWidth) < 1) return;

        nameColumn.Width = width;
        _lastAutoNameWidth = width;
    }

    #endregion

    #region Interaction & Selection

    private void LvFiles_MouseMoveRouter(object sender, MouseEventArgs e)
    {
        if (_isRightDragSelecting)
        {
            LvFiles_PreviewRightMouseMove(sender, e);
            return;
        }

        LvFiles_PreviewMouseMove(sender, e);
    }

    private void LvFiles_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var listViewItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);

        if (listViewItem != null && listViewItem.DataContext is FileEntry entry && entry.Name != "..")
        {
            _rightDragStartPoint = e.GetPosition(lvFiles);
            _lastRightDragHitPoint = _rightDragStartPoint;

            bool newState = !entry.IsMarked;
            entry.IsMarked = newState;
            UpdateMarkedStatus();

            _rightDragTargetState = newState;
            _isRightDragSelecting = true;
            _lastRightDragItem = entry;

            lvFiles.CaptureMouse();
            lvFiles.SelectedItem = entry;

            e.Handled = true;
        }
    }

    private void LvFiles_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isRightDragSelecting)
        {
            _isRightDragSelecting = false;
            lvFiles.ReleaseMouseCapture();

            Point currentPos = e.GetPosition(lvFiles);
            bool wasDragging = Math.Abs(currentPos.X - _rightDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                               Math.Abs(currentPos.Y - _rightDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance;

            if (wasDragging)
            {
                e.Handled = true;
            }
            else
            {
                if (_lastRightDragItem != null && _lastRightDragItem.Name != "..")
                {
                    string fullPath = _lastRightDragItem.FullPath;
                    var owner = Window.GetWindow(this);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        WindowsContextMenu.Show(fullPath, owner);
                    }), System.Windows.Threading.DispatcherPriority.ContextIdle);

                    e.Handled = true;
                }
            }
        }
    }

    private void LvFiles_PreviewRightMouseMove(object sender, MouseEventArgs e)
    {
        if (e.RightButton == MouseButtonState.Released)
        {
            _isRightDragSelecting = false;
            lvFiles.ReleaseMouseCapture();
            return;
        }

        Point currentPosition = e.GetPosition(lvFiles);

        bool hasMoved = Math.Abs(currentPosition.X - _rightDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(currentPosition.Y - _rightDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance;

        if (!hasMoved) return;

        if (Math.Abs(currentPosition.Y - _lastRightDragHitPoint.Y) < 2) return;
        _lastRightDragHitPoint = currentPosition;

        var hitTestResult = VisualTreeHelper.HitTest(lvFiles, currentPosition);
        if (hitTestResult == null) return;

        var listViewItem = FindAncestor<ListViewItem>(hitTestResult.VisualHit);
        if (listViewItem?.DataContext is not FileEntry entry || entry.Name == "..") return;

        if (entry != _lastRightDragItem)
        {
            entry.IsMarked = _rightDragTargetState;
            _lastRightDragItem = entry;
            UpdateMarkedStatus();
        }
    }

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string newPath = txtPath.Text;

            if (!newPath.StartsWith(@"\\") && !newPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) && !newPath.EndsWith("\\"))
            {
                newPath += "\\";
            }

            await NavigateAsync(newPath);
            FocusPanel();
        }
    }

    private void RestoreSelection(string? itemToSelect)
    {
        FileEntry? match = null;
        if (!string.IsNullOrEmpty(itemToSelect))
            match = Items.FirstOrDefault(e => string.Equals(e.Name, itemToSelect, StringComparison.OrdinalIgnoreCase));

        if (match != null || Items.Count > 0)
        {
            var target = match ?? Items[0];
            SetSelectedItem(target, takeFocus: lvFiles.IsKeyboardFocusWithin);
        }
    }

    public void SetSelectedItem(FileEntry item) => SetSelectedItem(item, takeFocus: true);

    public void SetSelectedItem(FileEntry item, bool takeFocus)
    {
        lvFiles.SelectedItem = item;
        lvFiles.ScrollIntoView(item);

        if (!takeFocus) return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (lvFiles.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
                container.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void FocusPanel()
    {
        if (lvFiles.SelectedItem != null && lvFiles.ItemContainerGenerator.ContainerFromItem(lvFiles.SelectedItem) is ListViewItem container)
            container.Focus();
        else
            lvFiles.Focus();
    }

    #endregion

    #region Sorting

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        string columnName = GetBaseColumnName(header.Column?.Header?.ToString() ?? "");
        ApplySort(columnName);
    }

    public void UpdateColumnHeaders()
    {
        if (lvFiles.View is not GridView gridView) return;
        foreach (var column in gridView.Columns)
        {
            string baseName = GetBaseColumnName(column.Header?.ToString() ?? "");
            column.Header = baseName == SortColumn ? baseName + (SortAscending ? " ↑" : " ↓") : baseName;
        }
    }

    private static string GetBaseColumnName(string header)
    {
        return header.Replace("↑", "").Replace("↓", "").Replace("\u2007", "").Trim();
    }

    private List<FileEntry> SortEntries(List<FileEntry> entries)
    {
        if (entries.Count < 2) return entries;

        FileEntry? parent = null;

        if (entries[0].Name == "..")
        {
            parent = entries[0];
            entries.RemoveAt(0);
        }

        entries.Sort(new FileEntryComparer(SortColumn, SortAscending));

        if (parent != null) entries.Insert(0, parent);

        return entries;
    }

    public void ApplySort(string columnName)
    {
        if (string.IsNullOrEmpty(columnName)) return;

        if (SortColumn == columnName) SortAscending = !SortAscending;
        else { SortColumn = columnName; SortAscending = true; }

        var selected = SelectedItem;

        var sorted = SortEntries(Items.ToList());
        Items.ReplaceAll(sorted);

        if (selected != null) lvFiles.SelectedItem = selected;

        UpdateColumnHeaders();
    }

    public class FileEntryComparer : IComparer<FileEntry>
    {
        private readonly string _sortColumn;
        private readonly bool _sortAscending;

        public FileEntryComparer(string sortColumn, bool sortAscending)
        {
            _sortColumn = sortColumn;
            _sortAscending = sortAscending;
        }

        public int Compare(FileEntry? x, FileEntry? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            if (x.IsFolder != y.IsFolder)
                return x.IsFolder ? -1 : 1;

            int result = _sortColumn switch
            {
                "Size" => x.Size.CompareTo(y.Size),
                "Modified" => CompareDates(x.Modified, y.Modified),
                "Extension" => MemoryExtensions.CompareTo(
                                    Path.GetExtension(x.Name.AsSpan()),
                                    Path.GetExtension(y.Name.AsSpan()),
                                    StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
            };

            if (result == 0 && _sortColumn != "Name")
                result = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

            return _sortAscending ? result : -result;
        }

        private static int CompareDates(DateTime? a, DateTime? b)
        {
            if (a.HasValue) return b.HasValue ? a.Value.CompareTo(b.Value) : 1;
            return b.HasValue ? -1 : 0;
        }
    }

    #endregion

    #region Visual Tree Helpers

    private static TextBlock? FindVisualTextBlock(DependencyObject root)
    {
        if (root is TextBlock tb)
            return tb;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindVisualTextBlock(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        DependencyObject? d = start;
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T fe && fe.Name == name) return fe;
            if (FindDescendant<T>(child, name) is T found) return found;
        }
        return null;
    }

    #endregion
}
