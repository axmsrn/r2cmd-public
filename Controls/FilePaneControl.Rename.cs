using System;
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

public partial class FilePaneControl
{
    // =========================================================================
    // Native Windows kernel imports for atomic file operations
    // =========================================================================
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    private const uint MOVEFILE_REPLACE_EXISTING = 0x00000001;
    // =========================================================================

    // =========================================================================
    // RENAME SELECTION
    // 0 — the stem only ("report" in "report.txt"), so Del or the first typed
    //     character replaces the part that actually changes.
    // 1 — the whole name including the extension.
    // Pressing the rename hotkey again while the box is open switches between them.
    // =========================================================================
    private int _renameSelectionMode;

    private void HandleF2Rename()
    {
        if (lvFiles.SelectedItem is not FileEntry entry || entry.Name == "..") return;

        // Already editing this row: only switch the selection.
        // This must NOT go through SelectRenameText, which resets box.Text and
        // would throw away whatever the user has typed so far.
        if (_renamingEntry != null && ReferenceEquals(_renamingEntry, entry))
        {
            CycleRenameSelection();
            return;
        }

        BeginRename(false);
    }

    private void BeginRename(bool selectAll)
    {
        _renameClickTimer?.Stop();
        if (lvFiles.SelectedItem is not FileEntry entry || entry.Name == "..") return;
        if (_renamingEntry != null) return;

        // A pane in the middle of a listing is about to replace every row,
        // including the one being edited
        if (_isBusy) return;

        _renamingEntry = entry;
        entry.IsEditing = true;
        lvFiles.UpdateLayout();

        _ = Dispatcher.BeginInvoke(new Action(() => SelectRenameText(entry, selectAll)), System.Windows.Threading.DispatcherPriority.Input);
    }

    // Attaches the rename box for a freshly opened editor and applies the initial
    // selection. Resets the text, so it is only for opening the editor.
    private void SelectRenameText(FileEntry entry, bool selectAll)
    {
        if (lvFiles.ItemContainerGenerator.ContainerFromItem(entry) is not ListViewItem container) return;
        if (FindDescendant<TextBox>(container, "txtRename") is not TextBox box) return;

        _renameBox = box;
        box.Text = entry.Name;

        _renameSelectionMode = selectAll ? 1 : 0;

        box.Focus();
        Keyboard.Focus(box);

        ApplyRenameSelection(box, entry);
    }

    // Repeated hotkey press while the editor is open. Text is left untouched.
    private void CycleRenameSelection()
    {
        var box = _renameBox;
        var entry = _renamingEntry;
        if (box == null || entry == null) return;

        // Two modes. For a Total Commander style three-step cycle
        // (stem -> extension -> everything) raise the modulus to 3 and add the
        // extension branch in ApplyRenameSelection.
        _renameSelectionMode = (_renameSelectionMode + 1) % 2;

        box.Focus();
        Keyboard.Focus(box);

        ApplyRenameSelection(box, entry);
    }

    private void ApplyRenameSelection(TextBox box, FileEntry entry)
    {
        string text = box.Text ?? string.Empty;

        // Creating a new item: the field is empty, there is nothing to select
        if (text.Length == 0)
        {
            box.CaretIndex = 0;
            return;
        }

        int stemLength = GetStemLength(text, entry.IsFolder);

        if (_renameSelectionMode == 0 && stemLength > 0 && stemLength < text.Length)
            box.Select(0, stemLength);
        else
            box.SelectAll();
    }

    // =========================================================================
    // How much of the name counts as "the part you usually retype".
    //
    // Folders: everything. A folder called "v1.2" or "node_modules.bak" has no
    // extension, and treating ".2" as one would leave the user editing half a
    // name — Explorer selects folder names whole for exactly this reason.
    //
    // Files starting with a dot (".gitignore", ".env"): everything as well,
    // since there is no stem in front of the dot to select.
    //
    // "archive.tar.gz" resolves to "archive.tar", because the extension is what
    // follows the last dot. Same as Explorer.
    // =========================================================================
    private static int GetStemLength(string name, bool isFolder)
    {
        if (isFolder) return name.Length;

        int lastDot = name.LastIndexOf('.');
        if (lastDot <= 0) return name.Length;

        return lastDot;
    }

    private async void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; if (sender is TextBox box) await CommitRenameAsync(box); }
        else if (e.Key == Key.Escape) { e.Handled = true; CancelRename(); }

        // Fallback for the case where the pane no longer sees F2 once the focus
        // sits inside the text box. Harmless if the pane handler already ran:
        // the mode simply switches once, here or there, never twice.
        else if (e.Key == Key.F2 && !e.Handled)
        {
            e.Handled = true;
            CycleRenameSelection();
        }
    }

    private async void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) await CommitRenameAsync(box);
    }

    private async Task CommitRenameAsync(TextBox box)
    {
        if (_renamingEntry == null || _renameInProgress) return;
        _renameInProgress = true;

        try
        {
            var entry = _renamingEntry;
            _renamingEntry = null;
            _renameBox = null;
            entry.IsEditing = false;

            string oldPath = entry.FullPath;
            string newName = box.Text.Trim();
            bool isCreatingNew = oldPath == ":::NEW:::";

            if (string.IsNullOrEmpty(newName) || (!isCreatingNew && string.Equals(newName, entry.Name, StringComparison.Ordinal)))
            {
                if (isCreatingNew) { Items.Remove(entry); FocusPanel(); }
                else RestoreRowFocus(entry);
                return;
            }

            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageDialog.Show(Window.GetWindow(this), "Invalid file name.", isCreatingNew ? "Create" : "Rename");
                if (isCreatingNew) { Items.Remove(entry); FocusPanel(); }
                else RestoreRowFocus(entry);
                return;
            }

            // Handle special case: rename saved SSH session connection name
            if (CurrentPath.Equals(@"\\Network\", StringComparison.OrdinalIgnoreCase) && !isCreatingNew)
            {
                if (oldPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var settings = (Window.GetWindow(this) as MainWindow)?.AppSettings ?? AppSettings.Load();
                        var session = settings.SshSessions.FirstOrDefault(s =>
                            s.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) ||
                            $"{s.Username}@{s.Host}".Equals(entry.Name, StringComparison.OrdinalIgnoreCase));

                        if (session != null)
                        {
                            if (settings.SshSessions.Any(s => string.Equals(s.Name, newName, StringComparison.OrdinalIgnoreCase)))
                            {
                                MessageDialog.Show(Window.GetWindow(this), $"A session named \"{newName}\" already exists.", "Rename");
                                RestoreRowFocus(entry);
                                return;
                            }

                            Providers.SshFileSystemProvider.CloseConnection(entry.Name);
                            session.Name = newName;
                            settings.Save();
                            await NavigateAsync(CurrentPath, newName);
                            DirectoryModified?.Invoke(this, EventArgs.Empty);
                        }
                        else RestoreRowFocus(entry);
                    }
                    catch (Exception ex)
                    {
                        MessageDialog.Show(Window.GetWindow(this), $"Cannot rename session:\n{ex.Message}", "Rename Error");
                        RestoreRowFocus(entry);
                    }
                    return;
                }
            }

            var provider = FileSystemFactory.GetProvider(CurrentPath);
            string dir = isCreatingNew ? CurrentPath : provider.GetParentPath(oldPath);
            string newPath = provider.CombinePaths(dir, newName);

            // =========================================================================
            // COLLISION HANDLING LOGIC
            // =========================================================================
            bool handledAtomicallyByWin32 = false;

            if (provider.Exists(newPath))
            {
                var targetItem = Items.FirstOrDefault(i => string.Equals(i.Name, newName, StringComparison.OrdinalIgnoreCase));
                bool isTargetFolder = false;

                if (targetItem != null)
                {
                    isTargetFolder = targetItem.IsFolder;
                }
                else
                {
                    if (!CurrentPath.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase))
                        isTargetFolder = Directory.Exists(newPath);
                    else
                    {
                        var (testEntries, testErr) = await provider.ReadDirectoryAsync(newPath);
                        isTargetFolder = testErr == null && testEntries != null;
                    }
                }

                bool canOverwrite = !isCreatingNew;

                // Ensure folders are empty before allowing overwrite
                if (canOverwrite && isTargetFolder)
                {
                    var (targetEntries, err) = await provider.ReadDirectoryAsync(newPath);
                    if (err == null && targetEntries != null)
                        canOverwrite = !targetEntries.Any(e => e.Name != "..");
                    else
                        canOverwrite = false;
                }

                var conflictDialog = new RenameConflictDialog(newName, isTargetFolder, canOverwrite)
                {
                    Owner = Window.GetWindow(this)
                };

                conflictDialog.ShowDialog();

                if (conflictDialog.Result == RenameConflictResult.Rename)
                {
                    _renamingEntry = entry;
                    entry.IsEditing = true;
                    lvFiles.UpdateLayout();

                    // Whole name selected: the user came back specifically to type
                    // a different one, so nothing should be kept
                    _ = Dispatcher.BeginInvoke(new Action(() => SelectRenameText(entry, true)), System.Windows.Threading.DispatcherPriority.Input);
                    return;
                }
                else if (conflictDialog.Result == RenameConflictResult.Overwrite)
                {
                    bool isLocal = !CurrentPath.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase);

                    // ===================================================================
                    // ATOMIC OVERWRITE VIA WIN32 API
                    // Used exclusively for local files (not directories or SSH sessions).
                    // ===================================================================
                    if (isLocal && !isTargetFolder && !isCreatingNew)
                    {
                        if (!MoveFileEx(oldPath, newPath, MOVEFILE_REPLACE_EXISTING))
                        {
                            int error = Marshal.GetLastWin32Error();
                            MessageDialog.Show(Window.GetWindow(this), $"Win32 Kernel atomic overwrite failed.\nError code: {error}", "Overwrite Error");
                            RestoreRowFocus(entry);
                            return;
                        }
                        handledAtomicallyByWin32 = true;
                    }
                    else
                    {
                        // ===================================================================
                        // FALLBACK DELETION FOR DIRECTORIES AND NETWORK PATHS
                        // ===================================================================
                        try
                        {
                            if (isLocal)
                            {
                                try
                                {
                                    if (Directory.Exists(newPath))
                                    {
                                        var di = new DirectoryInfo(newPath);
                                        di.Attributes &= ~FileAttributes.ReadOnly;
                                    }
                                    else if (File.Exists(newPath))
                                    {
                                        File.SetAttributes(newPath, FileAttributes.Normal);
                                    }
                                }
                                catch { }
                            }

                            int deleteRetries = 3;
                            while (true)
                            {
                                try
                                {
                                    await provider.DeleteAsync(newPath);
                                    break;
                                }
                                catch (Exception ex) when ((ex is UnauthorizedAccessException || ex is IOException) && deleteRetries > 0)
                                {
                                    deleteRetries--;
                                    await Task.Delay(150);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageDialog.Show(Window.GetWindow(this), $"Cannot delete existing item to overwrite:\n{ex.Message}", "Overwrite Error");
                            if (isCreatingNew) { Items.Remove(entry); FocusPanel(); }
                            else RestoreRowFocus(entry);
                            return;
                        }
                    }
                }
                else // Cancel
                {
                    if (isCreatingNew) { Items.Remove(entry); FocusPanel(); }
                    else RestoreRowFocus(entry);
                    return;
                }
            }

            try
            {
                // Skip standard provider rename if already handled atomically by Win32
                if (!handledAtomicallyByWin32)
                {
                    if (isCreatingNew)
                    {
                        if (entry.IsFolder) provider.CreateDirectory(newPath);
                        else provider.CreateFile(newPath);
                    }
                    else
                    {
                        int renameRetries = 3;
                        while (true)
                        {
                            try
                            {
                                provider.Rename(oldPath, newPath);
                                break;
                            }
                            catch (Exception ex) when ((ex is UnauthorizedAccessException || ex is IOException) && renameRetries > 0)
                            {
                                renameRetries--;
                                await Task.Delay(150);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageDialog.Show(Window.GetWindow(this), $"Cannot {(isCreatingNew ? "create" : "rename")}:\n{ex.Message}", isCreatingNew ? "Create" : "Rename");
                if (isCreatingNew) { Items.Remove(entry); FocusPanel(); }
                else RestoreRowFocus(entry);
                return;
            }

            await NavigateAsync(CurrentPath, newName);
            DirectoryModified?.Invoke(this, EventArgs.Empty);
        }
        finally { _renameInProgress = false; }
    }

    private void CancelRename()
    {
        if (_renamingEntry == null) return;
        var entry = _renamingEntry;
        _renamingEntry = null;
        _renameBox = null;
        _renameSelectionMode = 0;
        entry.IsEditing = false;

        if (entry.FullPath == ":::NEW:::")
        {
            Items.Remove(entry);
            FocusPanel();
        }
        else
        {
            RestoreRowFocus(entry);
        }
    }

    private void RestoreRowFocus(FileEntry entry)
    {
        lvFiles.UpdateLayout();
        if (lvFiles.ItemContainerGenerator.ContainerFromItem(entry) is ListViewItem container) container.Focus();
        else lvFiles.Focus();
    }

    private bool ClickInsideRenameBox(MouseButtonEventArgs e)
    {
        return _renameBox != null && e.OriginalSource is DependencyObject d && FindAncestor<TextBox>(d) is TextBox tb && ReferenceEquals(tb, _renameBox);
    }

    private void CommitActiveRename()
    {
        if (_renameBox is TextBox box) _ = CommitRenameAsync(box);
        else CancelRename();
    }

    // =========================================================================
    // Rename by a slow second click is a familiar gesture for real files, but it
    // must not apply to the virtual entries in the Network root: a double click
    // there opens an SSH connection, which takes seconds, and a pending rename
    // timer would fire in the middle of it. Missing the system double click
    // threshold by a few milliseconds was enough to land in the edit box instead
    // of the session.
    //
    // Those entries are renamed with F2 or Shift+F6, and Alt+Enter opens the full
    // session editor — host, port, user and key, not just the name.
    // =========================================================================
    private bool IsMouseRenameAllowed(FileEntry item)
    {
        if (CurrentPath.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase)) return false;
        if (item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return false;
        if (item.FullPath == ":::ADD_SSH:::") return false;

        return true;
    }

    private void ScheduleMouseRename(FileEntry item)
    {
        _renameClickTimer?.Stop();
        _renameClickTimer = null;

        if (_isBusy || !IsMouseRenameAllowed(item)) return;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 250) };
        _renameClickTimer = timer;
        timer.Tick += (s, ev) =>
        {
            timer.Stop();
            if (ReferenceEquals(_renameClickTimer, timer)) _renameClickTimer = null;

            // A navigation may have started between the click and this tick
            if (_isBusy || _renamingEntry != null) return;
            if (!ReferenceEquals(lvFiles.SelectedItem, item)) return;

            BeginRename(false);
        };
        timer.Start();
    }

    public void StartCreation(bool isFolder)
    {
        if (_isBusy || _renamingEntry != null) return;

        if (string.IsNullOrEmpty(CurrentPath) || CurrentPath.Equals(@"\\Network\", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage?.Invoke(this, "Cannot create items in this location.");
            return;
        }

        var newEntry = new FileEntry
        {
            Name = "",
            IsFolder = isFolder,
            FullPath = ":::NEW:::",
            IsEditing = true
        };

        int insertIdx = Items.Count > 0 && Items[0].Name == ".." ? 1 : 0;
        Items.Insert(insertIdx, newEntry);

        lvFiles.SelectedItem = newEntry;
        lvFiles.ScrollIntoView(newEntry);

        _renamingEntry = newEntry;
        lvFiles.UpdateLayout();

        _ = Dispatcher.BeginInvoke(new Action(() => SelectRenameText(newEntry, false)), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void RequestRename() => HandleF2Rename();
}
