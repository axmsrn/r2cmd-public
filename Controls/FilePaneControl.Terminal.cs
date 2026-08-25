using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace R2Cmd.Controls;

// Embedded terminal. Two layouts:
//   Ctrl+~  Full  - the terminal replaces the file list
//   Ctrl+T  Split - the terminal sits beside the list, on the OUTER edge of the
//                   window, so the two file lists stay next to each other
//
// Nothing here depends on markup: the wiring happens in OnInitialized and the
// layout is built from code. A locally defined type referenced from XAML pushes
// WPF into a two pass build where generated x:Name fields do not exist yet.
public partial class FilePaneControl
{
    private enum TerminalMode { Hidden, Full, Split }

    private const double MinTerminalWidth = 220;
    private const double SplitterWidth = 4;

    private readonly TerminalControl _terminal = new();

    private Border? _terminalHost;
    private GridSplitter? _terminalSplitter;
    private ColumnDefinition? _colLeft;
    private ColumnDefinition? _colMiddle;
    private ColumnDefinition? _colRight;

    private AppSettings? _terminalSettings;
    private bool _terminalReady;
    private TerminalMode _terminalMode = TerminalMode.Hidden;

    // Which side the terminal currently occupies, so Hide can restore the right column
    private bool _terminalOnLeft = true;

    private string? _sshTerminalPath;
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        // Tunnels before the ListView and before the terminal itself, so the
        // hotkeys work no matter which of the two currently holds focus
        PreviewKeyDown += TerminalKeys;

        Loaded += (s, args) => InitTerminal();
        Unloaded += (s, args) => ShutdownTerminal();
    }

    private bool InitTerminal()
    {
        if (_terminalReady) return true;

        if (!BuildTerminalColumn())
        {
            StatusMessage?.Invoke(this, "Terminal: cannot attach to the file list layout.");
            return false;
        }

        _terminalReady = true;
        _terminalSettings = AppSettings.Load();

        _terminal.SessionExited += (s, args) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string msg = _terminal.IsNetworkError
                    ? "Terminal closed: connection lost."
                    : "Terminal closed.";

                HideTerminal(returnFocus: true);
                TerminalVisibilityChanged?.Invoke(this, EventArgs.Empty);
                StatusMessage?.Invoke(this, msg);
            }));

        PathChanged += (s, args) => SyncTerminalDirectory();

        lvFiles.GotKeyboardFocus += (s, args) =>
        {
            if (_terminalMode != TerminalMode.Full) return;

            _ = Dispatcher.BeginInvoke(new Action(() => _terminal.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        };

        return true;
    }

    // Wraps the existing file list in [left] [splitter] [right]. Which of the two
    // outer columns holds the terminal is decided later, per pane.
    private bool BuildTerminalColumn()
    {
        if (lvFiles?.Parent is not Grid grid) return false;

        while (grid.ColumnDefinitions.Count < 3)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        }

        _colLeft = grid.ColumnDefinitions[0];
        _colMiddle = grid.ColumnDefinitions[1];
        _colRight = grid.ColumnDefinitions[2];

        _colLeft.Width = new GridLength(0);
        _colMiddle.Width = new GridLength(0);
        _colRight.Width = new GridLength(1, GridUnitType.Star);

        Grid.SetColumn(lvFiles, 2);
        if (pnlSearch != null) Grid.SetColumn(pnlSearch, 2);

        _terminal.SetResourceReference(FontSizeProperty, "AppFilePaneFontSize");

        _terminalHost = new Border { Visibility = Visibility.Collapsed, Child = _terminal };
        Grid.SetColumn(_terminalHost, 0);
        grid.Children.Add(_terminalHost);

        _terminalSplitter = new GridSplitter
        {
            Width = SplitterWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Columns,
            Visibility = Visibility.Collapsed,
            Focusable = false
        };
        _terminalSplitter.SetResourceReference(BackgroundProperty, "Brush.Border");
        Grid.SetColumn(_terminalSplitter, 1);
        grid.Children.Add(_terminalSplitter);

        return true;
    }

    private void TerminalKeys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab) { HandleTabKey(e); return; }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        // Prevent opening a terminal at the network root where local shells cannot operate
        if (CurrentPath.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Disconnect SSH file system on Ctrl+Q if the user is in the file list
        if (e.Key == Key.Q && CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            if (!_terminal.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                _ = DisconnectSshPaneAsync();
                return;
            }
        }

        TerminalMode requested;

        // Oem3 is the backtick/tilde key on most layouts; Oem8 covers the rest
        if (e.Key is Key.Oem3 or Key.Oem8) requested = TerminalMode.Full;
        else if (e.Key == Key.T) requested = TerminalMode.Split;
        else return;

        e.Handled = true;

        // Lazy build: works even if the Loaded event never reached us
        if (!InitTerminal()) return;

        // Same key again closes; the other key switches layout without restarting
        if (_terminalMode == requested) HideTerminal(returnFocus: true);
        else ShowTerminal(requested, takeFocus: true);
    }
    private async Task DisconnectSshPaneAsync()
    {
        string? sessionName = ExtractSshSessionName(CurrentPath);
        if (sessionName == null) return;

        try
        {
            Providers.SshFileSystemProvider.CloseConnection(sessionName);
            StatusMessage?.Invoke(this, $"Disconnected from SSH session '{sessionName}'.");
            await NavigateAsync(@"\\Network\");
            FocusPanel();
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"SSH disconnect error: {ex.Message}");
        }
    }

    // Split mode:  Tab stays pane switching, so the two file lists remain one
    //              Tab apart. Ctrl+Tab moves in and out of the terminal.
    // Full mode:   the list is a zero width column, so Tab from the list enters
    //              the terminal and Tab from the terminal leaves for the other pane.
    //
    // The shell never sees Tab; Ctrl+I sends the same \x09 and keeps completion.
    private void HandleTabKey(KeyEventArgs e)
    {
        if (_terminalMode == TerminalMode.Hidden) return; // MainWindow switches panes

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool inTerminal = _terminal.IsKeyboardFocusWithin;

        if (_terminalMode == TerminalMode.Split)
        {
            // Plain Tab is left to MainWindow, from the list and from the terminal alike
            if (!ctrl) return;

            if (inTerminal) FocusPanel();
            else _terminal.Focus();

            e.Handled = true;
            return;
        }

        // Full mode
        if (ctrl || inTerminal) return; // both go to the other pane

        _terminal.Focus();
        e.Handled = true;
    }

    // ===================== Public API for the toolbar button =====================
    public bool IsTerminalVisible => _terminalMode != TerminalMode.Hidden;

    // split: true  - terminal beside the file list (what the toolbar button uses)
    //        false - terminal fills the pane
    public void ToggleTerminal(bool split)
    {
        if (!InitTerminal()) return;

        var requested = split ? TerminalMode.Split : TerminalMode.Full;

        if (_terminalMode == requested) HideTerminal(returnFocus: true);
        else ShowTerminal(requested, takeFocus: true);

        TerminalVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    // Lets the toolbar button follow the state, including when the shell exits
    public event EventHandler? TerminalVisibilityChanged;

    // Tracks the last known SSH path so we can restore it when returning to the terminal
    private string? _lastSshPath;

    private void ShowTerminal(TerminalMode mode, bool takeFocus)
    {
        if (_colLeft == null || _colMiddle == null || _colRight == null || _terminalHost == null) return;

        ClearQuickSearch();

        _terminalOnLeft = IsLeftHalfOfWindow();
        _terminalMode = mode;

        int terminalColumn = _terminalOnLeft ? 0 : 2;
        int filesColumn = _terminalOnLeft ? 2 : 0;

        Grid.SetColumn(_terminalHost, terminalColumn);
        Grid.SetColumn(lvFiles, filesColumn);
        if (pnlSearch != null) Grid.SetColumn(pnlSearch, filesColumn);

        var terminalCol = _terminalOnLeft ? _colLeft : _colRight;
        var filesCol = _terminalOnLeft ? _colRight : _colLeft;

        if (mode == TerminalMode.Full)
        {
            terminalCol.Width = new GridLength(1, GridUnitType.Star);
            _colMiddle.Width = new GridLength(0);
            filesCol.Width = new GridLength(0);
            lvFiles.Visibility = Visibility.Visible;
            _terminalSplitter!.Visibility = Visibility.Collapsed;
        }
        else
        {
            double width = _terminalSettings?.TerminalWidth ?? 420;
            if (width < MinTerminalWidth) width = MinTerminalWidth;
            if (ActualWidth > 0 && width > ActualWidth - MinTerminalWidth) width = ActualWidth / 2;

            terminalCol.Width = new GridLength(width, GridUnitType.Pixel);
            filesCol.Width = new GridLength(1, GridUnitType.Star);
            _colMiddle.Width = new GridLength(SplitterWidth, GridUnitType.Pixel);

            lvFiles.Visibility = Visibility.Visible;
            _terminalSplitter!.Visibility = Visibility.Visible;
        }

        _terminalHost.Visibility = Visibility.Visible;

        string? sshSessionName = ExtractSshSessionName(CurrentPath);
        bool shouldBeSsh = sshSessionName != null;

        if (!_terminal.IsRunning)
        {

            if (shouldBeSsh)
            {
                var session = FindSshSession(sshSessionName!);
                if (session != null)
                {
                    _sshTerminalPath = CurrentPath;
                    _terminal.StartSsh(session);
                }
                else
                {
                    StatusMessage?.Invoke(this, $"Terminal: SSH session '{sshSessionName}' not found in settings.");
                    return;
                }
            }
            else
            {
                _terminal.Start(LocalWorkingDirectory(), _terminalSettings?.TerminalShellPath);
            }
        }
        else
        {
            if (shouldBeSsh && !_terminal.IsSshSession)
            {
                // Rule 1: A network folder can kill the local Windows terminal without warning.
                _terminal.Stop();
                var session = FindSshSession(sshSessionName!);
                if (session != null)
                {
                    _sshTerminalPath = CurrentPath;
                    _terminal.StartSsh(session);
                }
            }
            else if (_terminal.IsSshSession)
            {
                if (!string.IsNullOrEmpty(_sshTerminalPath) && CurrentPath != _sshTerminalPath)
                {
                    Dispatcher.BeginInvoke(new Action(() => { _ = NavigateAsync(_sshTerminalPath); }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }

        if (takeFocus)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => _terminal.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }
    // Extracts SSH session name from a path like "ssh://session-name/path/to/dir".
    // Returns null for non-SSH paths or when the session name is empty.
    private string? ExtractSshSessionName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return null;

        string rest = path.Substring(6);
        int slash = rest.IndexOf('/');
        string name = slash < 0 ? rest : rest.Substring(0, slash);

        return string.IsNullOrEmpty(name) ? null : name;
    }

    // Looks up an SSH session by name or username@host in the saved settings.
    // The match is case-insensitive, same as the rest of the application.
    private SshSession? FindSshSession(string name)
    {
        var settings = AppSettings.Load();
        return settings.SshSessions.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            $"{s.Username}@{s.Host}".Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private void HideTerminal(bool returnFocus)
    {
        RememberTerminalWidth();
        if (_colLeft != null) _colLeft.Width = new GridLength(0);
        if (_colMiddle != null) _colMiddle.Width = new GridLength(0);
        if (_colRight != null) _colRight.Width = new GridLength(0);

        var filesCol = _terminalOnLeft ? _colRight : _colLeft;
        if (filesCol != null) filesCol.Width = new GridLength(1, GridUnitType.Star);

        if (_terminalHost != null) _terminalHost.Visibility = Visibility.Collapsed;
        if (_terminalSplitter != null) _terminalSplitter.Visibility = Visibility.Collapsed;

        lvFiles.Visibility = Visibility.Visible;
        _terminalMode = TerminalMode.Hidden;

        if (returnFocus) FocusPanel();
    }

    // The terminal belongs on the outer edge of the window, so that the two file
    // lists stay side by side in the middle. Decided by geometry rather than by
    // pane name, which keeps working if the panels are ever swapped.
    private bool IsLeftHalfOfWindow()
    {
        var window = Window.GetWindow(this);
        if (window == null || window.ActualWidth <= 0 || ActualWidth <= 0) return true;

        try
        {
            Point center = TransformToAncestor(window).Transform(new Point(ActualWidth / 2, 0));
            return center.X < window.ActualWidth / 2;
        }
        catch { return true; }
    }

    private void RememberTerminalWidth()
    {
        if (_terminalSettings == null || _terminalMode != TerminalMode.Split) return;
        if (_terminal.ActualWidth < MinTerminalWidth) return;

        _terminalSettings.TerminalWidth = _terminal.ActualWidth;
    }

    private void ShutdownTerminal()
    {
        RememberTerminalWidth();
        _terminal.Stop(); // closes the pseudo console and kills a stuck shell
    }

    // Only local, existing folders make sense as a shell working directory:
    // ssh://, \\Network\ and paths inside archives have no on disk equivalent
    private string? LocalWorkingDirectory()
    {
        string path = CurrentPath;
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase)) return null;

        try { return Directory.Exists(path) ? path : null; }
        catch { return null; }
    }
    private void SyncTerminalDirectory()
    {
        // Always track the last known SSH path so we can accurately return to it later
        if (!string.IsNullOrEmpty(CurrentPath) && CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            _lastSshPath = CurrentPath;
        }

        // --- FIX FOR DEAD TERMINAL TRAP ---
        // We used to return immediately if !_terminal.IsRunning.
        // But if the terminal died (timeout/sleep), it's still visibly covering the screen in Full mode!
        // We must allow the code to reach the HideTerminal block below.
        if (_terminalMode == TerminalMode.Hidden) return;

        if (_terminalMode == TerminalMode.Full)
        {
            // Do not hide the terminal if we are actively restoring the SSH UI path.
            // (Only applies if the terminal is actually ALIVE and is an SSH session).
            bool isMatchingSshPath = _terminal.IsRunning && _terminal.IsSshSession &&
                                     CurrentPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

            if (!isMatchingSshPath)
            {
                HideTerminal(returnFocus: true);
                TerminalVisibilityChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        // For Split mode:
        // If the terminal is dead, or it's an SSH session, or a non-local path like \\Network\, local shells can't follow it.
        if (!_terminal.IsRunning || _terminal.IsSshSession) return;

        string? dir = LocalWorkingDirectory();
        if (dir == null) return;

        // Skip while the user is typing in the shell: an injected cd would land
        // in the middle of their half written command
        if (_terminal.IsKeyboardFocusWithin) return;

        _terminal.ChangeDirectory(dir);
    }

}
