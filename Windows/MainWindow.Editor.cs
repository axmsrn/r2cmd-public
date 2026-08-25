using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using R2Cmd.Controls;

namespace R2Cmd;

public partial class MainWindow
{
    // Sentinel stored in CustomEditorPath when the built-in Avalon editor is selected
    private const string InternalEditorId = "__internal__";

    private const string Npp64Path = @"C:\Program Files\Notepad++\notepad++.exe";
    private const string Npp32Path = @"C:\Program Files (x86)\Notepad++\notepad++.exe";
    private const string Np3Path = @"C:\Program Files\Notepad3\Notepad3.exe";

    // =========================================================================
    // The list costs four or five File.Exists calls to build, and it was rebuilt
    // on every button click (twice per click), on every right click and on every
    // status update. Nothing in it changes while the application runs except the
    // custom entry, so the chosen path is all the cache has to be keyed on.
    // =========================================================================
    private List<(string ButtonName, string MenuName, string Path)>? _editorsCache;
    private string? _editorsCacheKey;

    private static string SelectedEditorKey(string? customPath) =>
        string.IsNullOrEmpty(customPath) ? InternalEditorId : customPath;

    private List<(string ButtonName, string MenuName, string Path)> GetAvailableEditors()
    {
        if (_editorsCache != null &&
            string.Equals(_editorsCacheKey, _settings.CustomEditorPath, StringComparison.OrdinalIgnoreCase))
        {
            return _editorsCache;
        }

        var list = new List<(string ButtonName, string MenuName, string Path)>();

        // Built-in Avalon editor (always available)
        list.Add(("Internal Editor", "Internal Editor", InternalEditorId));

        string winNotepad = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

        if (File.Exists(Npp64Path)) list.Add(("Notepad++", "Notepad++", Npp64Path));
        else if (File.Exists(Npp32Path)) list.Add(("Notepad++", "Notepad++", Npp32Path));

        if (File.Exists(Np3Path)) list.Add(("Notepad3", "Notepad3", Np3Path));

        list.Add(("Win Notepad", "Windows Notepad", winNotepad));

        bool isStandard = string.IsNullOrEmpty(_settings.CustomEditorPath) ||
                          _settings.CustomEditorPath.Equals(InternalEditorId, StringComparison.OrdinalIgnoreCase) ||
                          _settings.CustomEditorPath.Equals(Npp64Path, StringComparison.OrdinalIgnoreCase) ||
                          _settings.CustomEditorPath.Equals(Npp32Path, StringComparison.OrdinalIgnoreCase) ||
                          _settings.CustomEditorPath.Equals(Np3Path, StringComparison.OrdinalIgnoreCase) ||
                          _settings.CustomEditorPath.Equals(winNotepad, StringComparison.OrdinalIgnoreCase);

        if (!isStandard && File.Exists(_settings.CustomEditorPath))
        {
            string fileName = System.IO.Path.GetFileName(_settings.CustomEditorPath);
            list.Add(($"Editor: {fileName}", $"Custom: {fileName}", _settings.CustomEditorPath));
        }

        _editorsCacheKey = _settings.CustomEditorPath;
        _editorsCache = list;

        return list;
    }

    private void UpdateEditorButton()
    {
        var editors = GetAvailableEditors();
        string selected = SelectedEditorKey(_settings.CustomEditorPath);

        var current = editors.FirstOrDefault(x =>
            string.Equals(x.Path, selected, StringComparison.OrdinalIgnoreCase));

        if (current.ButtonName == null)
            current = editors[0];

        btnSettings.Content = current.ButtonName;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var editors = GetAvailableEditors();
        string selected = SelectedEditorKey(_settings.CustomEditorPath);

        int currentIndex = editors.FindIndex(x =>
            string.Equals(x.Path, selected, StringComparison.OrdinalIgnoreCase));

        if (currentIndex == -1) currentIndex = 0;

        int nextIndex = (currentIndex + 1) % editors.Count;

        _settings.CustomEditorPath = editors[nextIndex].Path;
        _settings.Save();

        // Invalidate cache so custom entry rebuilds if needed
        _editorsCache = null;

        UpdateEditorButton();
        SetStatus($"Editor changed to: {editors[nextIndex].ButtonName}");
    }

    private void OnSettingsRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var parentPanel = (FrameworkElement)btnSettings.Parent;
        var menu = new ContextMenu
        {
            PlacementTarget = parentPanel,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom
        };

        // Align the popup perfectly flush against the right-most edge of the containing panel
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
        {
            return new[]
            {
                new System.Windows.Controls.Primitives.CustomPopupPlacement(
                    new Point(targetSize.Width - popupSize.Width + 5, -popupSize.Height - 5),
                    System.Windows.Controls.Primitives.PopupPrimaryAxis.None)
            };
        };

        menu.Items.Add(new MenuItem { Header = "Editor Settings (F3/F4)", IsEnabled = false });
        menu.Items.Add(new Separator());

        var editors = GetAvailableEditors();
        string selected = SelectedEditorKey(_settings.CustomEditorPath);

        foreach (var ed in editors)
        {
            if (ed.MenuName.StartsWith("Custom:")) continue;

            bool active = string.Equals(selected, ed.Path, StringComparison.OrdinalIgnoreCase);

            // The themed MenuItem template has no check mark area, so IsChecked
            // would be invisible. A marker in the header is what actually shows.
            var item = new MenuItem { Header = active ? "● " + ed.MenuName : "    " + ed.MenuName };

            item.Click += (s, ev) =>
            {
                _settings.CustomEditorPath = ed.Path;
                _settings.Save();
                _editorsCache = null;
                UpdateEditorButton();
                SetStatus($"Editor set to: {ed.ButtonName}");
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        bool hasCustom = editors.Any(x => x.MenuName.StartsWith("Custom:"));

        var customItem = new MenuItem
        {
            Header = hasCustom
                ? $"● Browse... (Current: {System.IO.Path.GetFileName(_settings.CustomEditorPath)})"
                : "    Browse for custom editor (.exe)..."
        };

        customItem.Click += (s, ev) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
                Title = "Select Editor Executable"
            };
            if (dlg.ShowDialog() == true)
            {
                _settings.CustomEditorPath = dlg.FileName;
                _settings.Save();
                _editorsCache = null;
                UpdateEditorButton();
                SetStatus($"Editor set to: {System.IO.Path.GetFileName(dlg.FileName)}");
            }
        };
        menu.Items.Add(customItem);

        menu.IsOpen = true;
    }

    // =========================================================================
    // F3 — the built in viewer, in a window of its own.
    //
    // Non-modal on purpose: several files can be open at once and the manager
    // stays usable behind them, which is what F3 does everywhere else.
    // =========================================================================
    private async void OpenInViewer()
    {
        var items = _activePane.SelectedItems;
        if (items.Count == 0) return;

        var item = items[0];
        if (item.Name == "..") return;

        // Resolve local symlink to the real file before the built-in viewer opens it.
        // F4 works because external editors let Windows follow the link; F3 reads via FileStream.
        string path = item.FullPath;
        if (item.IsSymlink &&
            !path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                System.IO.FileSystemInfo? target = null;

                try { target = new FileInfo(path).ResolveLinkTarget(true); } catch { }
                if (target == null)
                {
                    try { target = new DirectoryInfo(path).ResolveLinkTarget(true); } catch { }
                }

                if (target != null)
                    path = target.FullName;
            }
            catch
            {
                // Fall back to the original path
            }
        }

        // After resolving, skip real directories
        if (Directory.Exists(path) || (item.IsFolder && !item.IsSymlink))
            return;

        bool isLocal = File.Exists(path);

        string? pathToView = isLocal ? path : await MaterializeToTempAsync(item);
        if (pathToView == null) return;

        var viewer = new ViewerWindow(pathToView, item.Name) { Owner = this };
        viewer.Show();
    }

    // =========================================================================
    // F4
    //
    // Remote files and files inside archives go through MaterializeToTempAsync,
    // the same helper the Enter key uses. That matters for more than tidiness:
    // it puts each copy in its own subfolder keyed by the source path, so two
    // files with the same name from two different servers no longer overwrite
    // each other — and with them, the watcher no longer uploads one file's
    // contents over the other's remote path. It also puts the copy under
    // %TEMP%\R2Cmd, which is cleaned up when the application closes.
    // =========================================================================
    private async void OpenFileInEditor(bool readOnly)
    {
        var items = _activePane.SelectedItems;
        if (items.Count == 0) return;

        var item = items[0];
        if (item.IsFolder || item.Name == "..") return;

        bool isLocal = File.Exists(item.FullPath);
        bool isSsh = item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

        // No SetBusy here: the wait cursor and the hotkey lock are not worth it
        // for a download that reports itself in the background status line
        string? pathToOpen = isLocal ? item.FullPath : await MaterializeToTempAsync(item);
        if (pathToOpen == null) return;

        // A copy pulled out of an archive has nowhere to be written back to
        if (!isLocal && !isSsh) readOnly = true;

        bool isArchive = !isLocal && !isSsh;

        // Use built-in editor only when Internal is selected (default if path empty)
        bool useInternal =
            string.IsNullOrEmpty(_settings.CustomEditorPath) ||
            string.Equals(_settings.CustomEditorPath, InternalEditorId, StringComparison.OrdinalIgnoreCase);

        if (useInternal && !isArchive && !readOnly && IsInternalEditable(item.Name, pathToOpen))
        {
            string? remotePath = isSsh ? item.FullPath : null;
            var editor = new EditorWindow(pathToOpen, item.Name, remotePath) { Owner = this };
            editor.Show();
            return;
        }

        string editorPath = ResolveEditorPath();

        try
        {
            string args = $"\"{pathToOpen}\"";

            if (readOnly && editorPath.EndsWith("notepad++.exe", StringComparison.OrdinalIgnoreCase))
            {
                args = $"-ro \"{pathToOpen}\"";
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = editorPath,
                Arguments = args,
                UseShellExecute = true
            });

            if (isSsh && !readOnly) WatchAndUploadSshFile(pathToOpen, item.FullPath);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Failed to find or launch the editor.\n\n{ex.Message}", "F3/F4 Error");
        }
    }

    private static bool IsInternalEditable(string name, string path)
    {
        string ext = Path.GetExtension(name);
        var textExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".c", ".cpp", ".h", ".hpp", ".java", ".py", ".js", ".ts",
            ".html", ".css", ".xml", ".json", ".yaml", ".yml", ".sh", ".bat",
            ".cmd", ".ps1", ".php", ".rb", ".go", ".rs", ".swift", ".sql",
            ".ini", ".cfg", ".conf", ".xaml", ".fs", ".vb", ".lua", ".kt",
            ".csproj", ".md", ".txt", ".log", ".csv", ".tsv", ".mjs",
            ".gitignore", ".gitattributes", ".dockerignore", ".env", ".ps1"
        };

        if (textExt.Contains(ext))
            return true;

        // Fallback: treat as text if encoding detection succeeds
        return TextSearcher.DetectFileEncoding(path) != null;
    }

    // The fallback order lives in GetAvailableEditors and is read from there
    // rather than duplicated: the first entry is the best editor installed.
    private string ResolveEditorPath()
    {
        string editorPath = _settings.CustomEditorPath;
        if (!string.IsNullOrWhiteSpace(editorPath) &&
            !string.Equals(editorPath, InternalEditorId, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(editorPath))
        {
            return editorPath;
        }

        var editors = GetAvailableEditors();
        // Skip Internal sentinel when falling back to an external tool
        var external = editors.FirstOrDefault(e =>
            !string.Equals(e.Path, InternalEditorId, StringComparison.OrdinalIgnoreCase));

        return external.Path ?? "notepad.exe";
    }

    // =========================================================================
    // Watches the temporary copy of a remote file and sends it back on save.
    //
    // The watchers are kept in a dictionary and disposed. The previous version
    // created one as a local variable with EnableRaisingEvents set and no way to
    // reach it again: every F4 on a remote file added another watcher that ran
    // until the process exited.
    // =========================================================================
    private readonly Dictionary<string, FileSystemWatcher> _editorWatchers =
        new(StringComparer.OrdinalIgnoreCase);

    private void WatchAndUploadSshFile(string localPath, string remotePath)
    {
        // Reopening the same file replaces its watcher rather than stacking one
        if (_editorWatchers.TryGetValue(localPath, out var previous))
        {
            try { previous.EnableRaisingEvents = false; previous.Dispose(); } catch { }
            _editorWatchers.Remove(localPath);
        }

        string? directory = Path.GetDirectoryName(localPath);
        if (string.IsNullOrEmpty(directory)) return;

        var watcher = new FileSystemWatcher
        {
            Path = directory,
            Filter = Path.GetFileName(localPath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        DateTime lastUpload = DateTime.MinValue;

        watcher.Changed += async (s, e) =>
        {
            // Editors write in several steps; one save must not become three uploads
            if ((DateTime.Now - lastUpload).TotalSeconds < 1.5) return;
            lastUpload = DateTime.Now;

            try
            {
                Dispatcher.Invoke(() => SetStatus($"Uploading changes to {Path.GetFileName(remotePath)}..."));

                await Task.Run(async () =>
                {
                    const int maxRetries = 10;
                    const int delayOnRetry = 500;

                    for (int i = 0; i < maxRetries; i++)
                    {
                        try
                        {
                            using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            Providers.SshFileSystemProvider.UploadFromStream(fs, remotePath, System.Threading.CancellationToken.None, _ => { });
                            break;
                        }
                        catch (IOException)
                        {
                            // The editor still has the file open mid-save
                            if (i == maxRetries - 1) throw;

                            await Task.Delay(delayOnRetry);
                        }
                    }
                });

                Dispatcher.Invoke(() => SetStatus($"Saved {Path.GetFileName(remotePath)} to SSH server."));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => MessageDialog.Show(this, $"Failed to upload changes:\n{ex.Message}", "SSH Upload Error"));
            }
        };

        _editorWatchers[localPath] = watcher;
    }

    /// <summary>Called when the window closes; nothing is watched after that.</summary>
    private void StopEditorWatchers()
    {
        foreach (var watcher in _editorWatchers.Values)
        {
            try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
        }

        _editorWatchers.Clear();
    }
}
