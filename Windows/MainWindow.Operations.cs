using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using R2Cmd.Controls;

namespace R2Cmd;

public partial class MainWindow
{
    // Read-only / hidden / system entries refuse to delete, so strip the flags and retry.
    // Directories keep their Directory flag, which is why they are handled separately.
    private static void ClearBlockingAttributes(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                var di = new DirectoryInfo(path);
                di.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            }
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
        catch { }
    }

    // =========================================================================
    // AttributesToSkip MUST be 0 in every one of these.
    //
    // Its default value is Hidden|System, so desktop.ini, Thumbs.db and any hidden
    // file are invisible to the enumerator. That under-counts sizes and progress,
    // and it makes recursive delete fail outright: the files are never removed and
    // Directory.Delete then throws "The directory is not empty".
    //
    // Reparse points are a separate concern: EnumerationOptions has no way to stop
    // recursion at a junction, so every recursive walk below sets its own
    // ShouldRecursePredicate. Without it a junction is followed, which inflates
    // folder sizes and — far worse — deletes the contents of the link target.
    // =========================================================================
    private static readonly EnumerationOptions s_recursiveEnumOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true
    };

    private static readonly EnumerationOptions s_flatEnumOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true
    };

    private static bool IsReparse(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    // =========================================================================
    // FOLDER SIZES — background work, never a modal wait
    //
    // Counting a node_modules tree over SSH takes minutes. It used to run under
    // SetBusy, which put the wait cursor on the whole desktop and made _busy
    // swallow every hotkey, so the only option was to sit and watch.
    //
    // Now it runs detached: no busy flag, no cursor, progress in its own status
    // field. Results are not cached across navigation — like Total Commander,
    // leaving the folder means the number is counted again next time, which is
    // far simpler than keeping a cache honest as the tree changes underneath it.
    //
    // The only thing remembered is which scans are in flight, so a second Space
    // on the same folder — or the same folder open in both panes — does not walk
    // the tree twice at once. Touched from the UI thread only.
    // =========================================================================
    private readonly HashSet<string> _sizeScansRunning = new(StringComparer.OrdinalIgnoreCase);
    private int _sizeJobsRunning;

    private void QueueFolderSize(FileEntry entry)
    {
        if (!entry.IsFolder || entry.Name == "..") return;

        string path = entry.FullPath;

        if (!_sizeScansRunning.Add(path))
        {
            entry.SizeCalculating = true;
            return;
        }

        _ = RunFolderSizeAsync(entry, path);
    }

    private async Task RunFolderSizeAsync(FileEntry entry, string path)
    {
        entry.SizeCalculating = true;
        _sizeJobsRunning++;
        UpdateBackgroundStatus();

        try
        {
            long size = await Task.Run(() => CalculateFolderSize(path));

            if (size >= 0)
            {
                entry.Size = size;
                entry.SizeKnown = true;
            }
            else entry.SizeKnown = false;
        }
        catch { entry.SizeKnown = false; }
        finally
        {
            entry.SizeCalculating = false;
            _sizeScansRunning.Remove(path);

            _sizeJobsRunning--;
            UpdateBackgroundStatus();
        }
    }

    private void QueueAllFolderSizes(FilePaneControl pane)
    {
        var targets = pane.Items
            .Where(e => e.IsFolder && e.Name != ".." && !e.SizeKnown && !e.SizeCalculating)
            .ToList();

        if (targets.Count == 0) return;

        _ = RunAllFolderSizesAsync(targets);
    }

    private async Task RunAllFolderSizesAsync(List<FileEntry> targets)
    {
        int done = 0;

        foreach (var entry in targets)
        {
            string path = entry.FullPath;
            done++;

            if (!_sizeScansRunning.Add(path)) continue;

            entry.SizeCalculating = true;
            SetBackgroundStatus($"Folder sizes: {done} / {targets.Count}");

            try
            {
                long size = await Task.Run(() => CalculateFolderSize(path));

                if (size >= 0)
                {
                    entry.Size = size;
                    entry.SizeKnown = true;
                }
                else entry.SizeKnown = false;
            }
            catch { entry.SizeKnown = false; }
            finally
            {
                entry.SizeCalculating = false;
                _sizeScansRunning.Remove(path);
            }
        }

        UpdateBackgroundStatus();
    }

    private void UpdateBackgroundStatus()
    {
        SetBackgroundStatus(_sizeJobsRunning switch
        {
            <= 0 => null,
            1 => "Calculating folder size...",
            _ => $"Calculating {_sizeJobsRunning} folder sizes..."
        });
    }

    private static long CalculateFolderSize(string path)
    {
        try
        {
            if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                var result = Providers.SshFileSystemProvider.RemoteSumTree(path);
                return result.Bytes;
            }

            long total = 0;
            var sizes = new FileSystemEnumerable<long>(
                path,
                (ref FileSystemEntry entry) => entry.IsDirectory ? 0 : entry.Length,
                s_recursiveEnumOptions)
            {
                // A junction belongs to its target, not to this folder
                ShouldRecursePredicate = (ref FileSystemEntry e) => !IsReparse(e.Attributes)
            };

            foreach (var size in sizes) total += size;
            return total;
        }
        catch { return -1; }
    }

    // =========================================================================
    // Blocks two distinct mistakes:
    //   1. dropping a folder into itself or into one of its own subfolders;
    //   2. copying an entry into the directory it already lives in, where the
    //      source and the destination path are literally the same file.
    // =========================================================================
    private FileEntry? FindSelfOperationConflict(List<FileEntry> items, string destPath)
    {
        return items.FirstOrDefault(i => IsSelfOperationConflict(i, destPath));
    }

    // Blocks two mistakes for both local and ssh:// paths:
    // 1) copying/moving an item into the directory it already lives in;
    // 2) dropping a folder into itself or into one of its own subfolders.
    private static bool IsSelfOperationConflict(FileEntry item, string destPath)
    {
        if (item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            destPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            // Mixed local/SSH is never "same location"
            if (!item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
                !destPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                return false;

            string src = item.FullPath.TrimEnd('/');
            string dest = destPath.TrimEnd('/');

            // Different SSH sessions cannot be the same tree
            string srcSession = SshSessionName(src);
            string destSession = SshSessionName(dest);
            if (!string.Equals(srcSession, destSession, StringComparison.OrdinalIgnoreCase))
                return false;

            // Parent of source == destination → copy into its own folder
            int lastSlash = src.LastIndexOf('/');
            if (lastSlash > "ssh://".Length)
            {
                string parent = src.Substring(0, lastSlash);
                if (string.Equals(parent, dest, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Folder into itself or into a child: dest is src or starts with src/
            if (!item.IsFolder) return false;

            return string.Equals(dest, src, StringComparison.OrdinalIgnoreCase) ||
                   dest.StartsWith(src + "/", StringComparison.OrdinalIgnoreCase);
        }

        // ----- Local paths -----
        string localDest = destPath.TrimEnd('\\');
        string localSrc = item.FullPath.TrimEnd('\\');

        string? parentLocal = Path.GetDirectoryName(localSrc);
        if (parentLocal != null &&
            string.Equals(parentLocal.TrimEnd('\\'), localDest, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!item.IsFolder) return false;

        string srcPrefix = localSrc.EndsWith("\\") ? localSrc : localSrc + "\\";
        return (localDest + "\\").StartsWith(srcPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string SshSessionName(string sshPath)
    {
        // ssh://SessionName/rest...
        string rest = sshPath.Length > 6 ? sshPath.Substring(6) : "";
        int slash = rest.IndexOf('/');
        return slash < 0 ? rest : rest.Substring(0, slash);
    }

    // =========================================================================
    // OPENING A FILE WITH THE SHELL
    //
    // ShellExecute is only correct for a real path on disk. An SSH entry carries
    // "ssh://session/dir/server.js", and Windows reads that as a URL: it looks up
    // the registered handler for the ssh: scheme — PuTTY, OpenSSH, Windows
    // Terminal, whatever is installed — and launches a console session. That is
    // why clicking a remote file opened a terminal instead of the file.
    //
    // A path inside an archive fails differently: it looks local but nothing
    // exists there.
    //
    // Both cases are materialised into TEMP first and the local copy is opened.
    // Edits to that copy are NOT sent back to the server or the archive.
    // =========================================================================
    private async Task OpenFileExternallyAsync(FileEntry entry)
    {
        if (File.Exists(entry.FullPath))
        {
            ShellOpen(entry.FullPath);
            return;
        }

        string? localCopy = await MaterializeToTempAsync(entry);
        if (localCopy == null) return;

        ShellOpen(localCopy);
        SetStatus($"Opened a temporary copy of {entry.Name}. Changes are not sent back.");
    }

    private void ShellOpen(string localPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = localPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Failed to launch file.\n\n{ex.Message}", "Execution Error");
        }
    }

    private async Task<string?> MaterializeToTempAsync(FileEntry entry)
    {
        const long LargeFileThreshold = 64L * 1024 * 1024;

        if (entry.Size > LargeFileThreshold)
        {
            var confirm = new ConfirmDialog(
                $"{entry.Name} is {Helpers.FormatSize(entry.Size)}.\nDownload a temporary copy to open it?",
                "Open file")
            { Owner = this };

            if (confirm.ShowDialog() != true) return null;
        }

        string folder = Path.Combine(Path.GetTempPath(), "R2Cmd", TempFolderFor(entry.FullPath));
        string localPath = Path.Combine(folder, SafeFileName(entry.Name));

        SetBackgroundStatus($"Downloading {entry.Name}...");

        try
        {
            Directory.CreateDirectory(folder);

            var (archivePath, internalPath) = ArchiveService.ParseVirtualPath(entry.FullPath);

            if (archivePath != null && !string.IsNullOrEmpty(internalPath) && File.Exists(archivePath))
            {
                await Task.Run(() => ArchiveService.ExtractFile(archivePath, internalPath, localPath));
            }
            else
            {
                var provider = Providers.FileSystemFactory.GetProvider(entry.FullPath);

                await using var source = await provider.OpenReadAsync(entry.FullPath);
                await using var target = new FileStream(localPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 81920, useAsync: true);

                await source.CopyToAsync(target);
            }

            return localPath;
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Cannot open {entry.Name}:\n{ex.Message}", "Open");
            return null;
        }
        finally
        {
            // Restores whatever the folder size scan is reporting, if anything
            UpdateBackgroundStatus();
        }
    }

    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    // One folder per source path, so two server.js from different hosts do not
    // overwrite each other. FNV-1a rather than String.GetHashCode, which is
    // randomised per process — the same remote file would otherwise land in a
    // different temp folder on every run of the application.
    //
    // Case folding happens per character: ToLowerInvariant() on the whole path
    // allocated a throwaway string for nothing.
    private static string TempFolderFor(string fullPath)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in fullPath)
            {
                hash ^= char.ToLowerInvariant(c);
                hash *= 16777619;
            }
            return hash.ToString("x8");
        }
    }

    private async Task RefreshTargetAsync(FilePaneControl targetPane)
    {
        await targetPane.RefreshAsync();
        await SyncPanesIfSamePath(targetPane);
    }

    // =========================================================================
    // SHARED PROGRESS WINDOW HOST
    // Runs a copy/move/pack dialog and mirrors its progress line (including the
    // elapsed timer) into the bottom status bar, exactly like delete does.
    //
    // The summary line is written LAST, after pane refresh and focus callbacks
    // have drained, and the status lock is released only after that. Otherwise
    // the panes would immediately overwrite the result with their own messages
    // and the statistics would vanish the moment the window closes.
    // =========================================================================
    private async Task RunFileOperationAsync(ProgressWindow dialog, Func<Task>? onSuccess = null, FilePaneControl? sourcePane = null)
    {
        this.IsEnabled = false;

        // Lock the status bar so pane messages cannot overwrite operation progress
        IsStatusLocked = true;

        EventHandler<string> onStatus = (s, text) => SetStatus(text, forceUpdate: true);
        dialog.StatusUpdated += onStatus;

        var tcs = new TaskCompletionSource<bool>();
        dialog.Closed += (s, e) => tcs.TrySetResult(true);
        dialog.BackgroundRequested += (s, e) => { this.IsEnabled = true; };

        try
        {
            dialog.Show();
            await tcs.Task;
        }
        finally
        {
            dialog.StatusUpdated -= onStatus;
            if (!this.IsEnabled) this.IsEnabled = true;
        }

        // =========================================================================
        // Everything below runs under the status lock, so the panes cannot clobber
        // the summary. The try/finally matters: without it an exception from
        // onSuccess() (a disconnected network drive is enough) would leave
        // IsStatusLocked stuck at true and the status bar dead until restart.
        // =========================================================================
        string finalStatus = dialog.GetFinalStatus();

        try
        {
            if (!dialog.IsCancelled && dialog.SuccessfullyProcessedFiles > 0 && onSuccess != null)
            {
                await onSuccess();
            }

            sourcePane?.ClearSelection();
        }
        finally
        {
            // ApplicationIdle runs after the panes' own Background/Input priority
            // callbacks, so this is the last thing written to the status bar
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                sourcePane?.FocusPanel();
                SetStatus(finalStatus, forceUpdate: true);

                // Release only now: the line stays until the user does something else
                IsStatusLocked = false;
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    // =========================================================================
    // Single entry point for Copy / Move / drag-and-drop.
    // explicitDestPath allows drag-and-drop to target a specific subfolder.
    // =========================================================================
    private async Task StartTransferAsync(
        List<FileEntry> items,
        FilePaneControl? sourcePane,
        FilePaneControl targetPane,
        FileOperation operation,
        string? explicitDestPath = null)
    {
        if (items.Count == 0) return;

        // Use the explicitly provided path (from a subfolder drop), or fallback to the pane's root
        string destPath = explicitDestPath ?? targetPane.CurrentPath;
        bool isMove = operation == FileOperation.Move;

        var conflict = FindSelfOperationConflict(items, destPath);
        if (conflict != null)
        {
            MessageDialog.Show(this,
                $"Cannot {(isMove ? "move" : "copy")} '{conflict.Name}': the destination is the item's own location or a subfolder of it.\n\n" +
                $"Source: {conflict.FullPath}\nDestination: {destPath}",
                isMove ? "Move Error" : "Copy Error");
            return;
        }

        var dialog = new ProgressWindow(items, destPath, operation) { Owner = this };

        // Move touches both panes, copy only the target one
        Func<Task> onSuccess = isMove
            ? () => DoRefreshAsync()
            : () => RefreshTargetAsync(targetPane);

        await RunFileOperationAsync(dialog, onSuccess, sourcePane);
    }

    private Task DoCopyAsync() =>
        StartTransferAsync(_activePane.SelectedItems, _activePane, _inactivePane, FileOperation.Copy);

    private Task DoMoveAsync() =>
        StartTransferAsync(_activePane.SelectedItems, _activePane, _inactivePane, FileOperation.Move);

    private Task HandleFilesDroppedAsync(FilePaneControl targetPane, Controls.FilePaneControl.FilesDroppedEventArgs args) =>
        StartTransferAsync(args.Items, null, targetPane, args.IsMove ? FileOperation.Move : FileOperation.Copy, args.TargetPath);

    private async Task DoPackAsync()
    {
        var sourcePane = _activePane;
        var targetPane = _inactivePane;

        var itemsToPack = sourcePane.SelectedItems;
        if (itemsToPack.Count == 0) return;

        string currentDirPath = sourcePane.CurrentPath;
        string destDirPath = targetPane.CurrentPath;

        string defaultName = Path.GetFileName(currentDirPath.TrimEnd('\\'));
        if (string.IsNullOrEmpty(defaultName) || defaultName == "Network") defaultName = "archive";
        defaultName += ".zip";

        string? zipName = ShowInputBox("Pack files", $"Pack {itemsToPack.Count} item(s) to:", defaultName);
        if (string.IsNullOrWhiteSpace(zipName)) return;

        if (!zipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zipName += ".zip";
        string targetZipPath = Path.Combine(destDirPath, zipName);

        if (File.Exists(targetZipPath))
        {
            MessageDialog.Show(this, "Archive already exists!", "Pack Error");
            return;
        }

        var dialog = new ProgressWindow(itemsToPack, targetZipPath, FileOperation.Pack) { Owner = this };

        await RunFileOperationAsync(dialog, () => RefreshTargetAsync(targetPane), sourcePane);
    }

    private string? ShowInputBox(string title, string prompt, string defaultText)
    {
        var window = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = this.Background,
            Foreground = this.Foreground
        };

        // Laid out by the panel instead of by hand-tuned margins like
        // "Thickness(0, 0, 105, 15)", which broke as soon as a button changed width.
        var root = new StackPanel { Margin = new Thickness(15) };

        var lbl = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) };

        var txt = new TextBox
        {
            Text = defaultText,
            Height = 25,
            Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 0, 15),
            Background = this.Background,
            Foreground = this.Foreground,
            CaretBrush = this.Foreground
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var btnOk = new Button { Content = "OK", Width = 80, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 10, 0) };
        var btnCancel = new Button { Content = "Cancel", Width = 80, Height = 30, IsCancel = true };

        btnOk.Click += (s, e) => window.DialogResult = true;

        buttons.Children.Add(btnOk);
        buttons.Children.Add(btnCancel);

        root.Children.Add(lbl);
        root.Children.Add(txt);
        root.Children.Add(buttons);

        window.Content = root;
        window.SourceInitialized += (s, e) => Helpers.SetTitleBarTheme(window, ThemeManager.IsDarkTheme);
        window.Loaded += (s, e) => { txt.Focus(); txt.CaretIndex = txt.Text.Length; };

        return window.ShowDialog() == true ? txt.Text : null;
    }

    // =========================================================================
    // Counts how many real files a selected entry represents, so the status bar
    // can report "1234 / 7348 files" instead of "1 item" for a whole folder.
    // Reparse points are never walked; an empty folder still counts as one unit.
    //
    // FileSystemEnumerable rather than DirectoryInfo.EnumerateFiles: the latter
    // builds a full FileInfo per file only to be thrown away one line later.
    // =========================================================================
    private static int CountFilesForDelete(string path, bool isFolder, CancellationToken token)
    {
        if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (files, _) = Providers.SshFileSystemProvider.RemoteSumTree(path);
                return Math.Max(1, files);
            }
            catch { return 1; }
        }

        if (!isFolder) return 1;

        try
        {
            var di = new DirectoryInfo(path);
            if (IsReparse(di.Attributes)) return 1;

            int count = 0;

            var files = new FileSystemEnumerable<byte>(
                path,
                (ref FileSystemEntry entry) => 0,
                s_recursiveEnumOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
                ShouldRecursePredicate = (ref FileSystemEntry e) => !IsReparse(e.Attributes)
            };

            foreach (var _ in files)
            {
                // Instant cancellation so ESC works during huge folder scans
                token.ThrowIfCancellationRequested();
                count++;
            }

            return Math.Max(1, count);
        }
        catch (OperationCanceledException) { throw; }
        catch { return 1; }
    }

    // What the delete loops do with a file they could not remove
    private enum FailureAction { Retry, Skip, Abort }

    // Called from background and STA threads; shows the dialog on the UI thread
    private delegate FailureAction FailureHandler(string path, string reason);

    // The dialog names ten of them; collecting a hundred thousand strings from a
    // locked build folder would cost more than the operation itself
    private const int MaxCollectedErrors = 200;

    // =========================================================================
    // ONE STA THREAD PER OPERATION
    //
    // SHFileOperation needs an STA apartment. Spawning a fresh thread per chunk
    // meant thousands of thread creations plus COM initialisation on a large
    // delete, and the cost showed up as a visible stall between chunks. A single
    // worker for the whole operation does the same job.
    // =========================================================================
    private sealed class StaWorker : IDisposable
    {
        private readonly BlockingCollection<(Action Work, TaskCompletionSource<bool> Done)> _queue = new();
        private readonly Thread _thread;

        public StaWorker()
        {
            _thread = new Thread(() =>
            {
                foreach (var (work, done) in _queue.GetConsumingEnumerable())
                {
                    try { work(); done.TrySetResult(true); }
                    catch (Exception ex) { done.TrySetException(ex); }
                }
            })
            { IsBackground = true };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task RunAsync(Action work)
        {
            var done = new TaskCompletionSource<bool>();
            _queue.Add((work, done));
            return done.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
        }
    }

    // =========================================================================
    // DELETE
    //
    // Windows treats a directory as one object. If a single file deep inside is
    // held by another process, SHFileOperation refuses the whole tree, the
    // hundreds of unlocked files around it stay on disk, and the error names the
    // top folder rather than the file actually holding the lock.
    //
    // Both delete paths therefore walk the tree themselves and ask what to do
    // with each file they cannot remove: Retry after closing the program that
    // holds it, Skip this one, Skip all — take everything that is free and leave
    // the rest — or Cancel the whole operation. Whatever is left behind is listed
    // by full path at the end.
    // =========================================================================
    private async Task DoDeleteAsync(bool permanent)
    {
        var sourcePane = _activePane;
        var items = sourcePane.SelectedItems.ToList();
        if (items.Count == 0) return;

        bool anySsh = items.Any(i => i.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase));

        if (!ConfirmDelete(items.Select(i => i.Name), permanent, anySsh)) return;

        string startingPath = sourcePane.CurrentPath;

        // Sort items properly
        var itemIndices = new Dictionary<FileEntry, int>();
        for (int i = 0; i < sourcePane.Items.Count; i++)
        {
            itemIndices[sourcePane.Items[i]] = i;
        }
        items = items.OrderBy(x => itemIndices.TryGetValue(x, out int idx) ? idx : int.MaxValue).ToList();

        bool finished = false; // set once the delete loop is over, checked by queued UI updates
        int okItems = 0;       // top level entries removed
        int deletedFiles = 0;  // real files removed, shown in the status bar
        int totalFiles = 0;    // result of the recursive pre-scan
        bool skipAllFailures = false;
        var errors = new List<string>();
        var fileCounts = new Dictionary<FileEntry, int>();
        string? itemToRestore = null;

        var deletedSet = new HashSet<FileEntry>(items);
        var ordered = sourcePane.Items;
        int firstDeletedIndex = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (deletedSet.Contains(ordered[i])) { firstDeletedIndex = i; break; }
        }

        if (firstDeletedIndex > 0)
        {
            itemToRestore = ordered[firstDeletedIndex - 1].Name;
        }

        if (string.IsNullOrEmpty(itemToRestore) && firstDeletedIndex >= 0)
        {
            for (int i = firstDeletedIndex + 1; i < ordered.Count; i++)
            {
                if (!deletedSet.Contains(ordered[i]))
                {
                    itemToRestore = ordered[i].Name;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(itemToRestore) && ordered.Any(x => x.Name == ".."))
        {
            itemToRestore = "..";
        }

        var totalTimeStopwatch = System.Diagnostics.Stopwatch.StartNew();
        IsStatusLocked = true;

        using var cts = new CancellationTokenSource();
        using var sta = new StaWorker();

        KeyEventHandler cancelHandler = (s, e) =>
        {
            if (e.Key == Key.Escape && !cts.IsCancellationRequested)
            {
                if (e.OriginalSource is TextBox) return;

                if (sourcePane.CurrentPath == startingPath && _activePane == sourcePane)
                {
                    cts.Cancel();
                    e.Handled = true;
                    SetStatus("Canceling deletion... Please wait.", forceUpdate: true);
                }
            }
        };

        // Hooked before the scan so ESC can also abort counting
        this.PreviewKeyDown += cancelHandler;

        // =====================================================================
        // The single place that decides what happens to a file that would not go.
        // Runs on whichever thread hit the failure and marshals the dialog to the
        // UI thread, which is free at that moment: the operation is awaited, not
        // blocking it.
        // =====================================================================
        FailureAction OnFailure(string path, string reason)
        {
            if (cts.IsCancellationRequested) return FailureAction.Abort;

            if (!skipAllFailures)
            {
                var choice = ErrorChoice.SkipAll;

                Dispatcher.Invoke(() =>
                {
                    string message =
                        $"{path}{Environment.NewLine}{Environment.NewLine}" +
                        $"{reason}{Environment.NewLine}{Environment.NewLine}" +
                        "It is usually open in another program. Close it and press Retry, " +
                        "or skip it and finish the rest.";

                    var dialog = new ErrorActionDialog(
                        message,
                        permanent ? "Cannot delete" : "Cannot move to Recycle Bin",
                        allowRetry: true,
                        focusSkipAll: true)
                    { Owner = this };

                    dialog.ShowDialog();
                    choice = dialog.Choice;
                });

                switch (choice)
                {
                    case ErrorChoice.Retry:
                        return FailureAction.Retry;

                    case ErrorChoice.SkipAll:
                        skipAllFailures = true;
                        break;

                    case ErrorChoice.Cancel:
                        cts.Cancel();
                        return FailureAction.Abort;
                }
            }

            lock (errors)
            {
                if (errors.Count < MaxCollectedErrors) errors.Add($"{path}: {reason}");
            }

            return FailureAction.Skip;
        }

        try
        {
            // ---------------------------------------------------------------------
            // PASS 1: count files so progress can be reported per file
            //
            // Skipped entirely for a selection of plain local files: the count is
            // then the number of items, and the scan machinery costs more than the
            // delete itself.
            // ---------------------------------------------------------------------
            bool needsScan = items.Any(i =>
                i.IsFolder || i.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase));

            if (!needsScan)
            {
                foreach (var item in items) fileCounts[item] = 1;
                totalFiles = items.Count;
            }
            else
            {
                SetStatus("Counting files to delete... [ESC to cancel]", forceUpdate: true);

                try
                {
                    await Task.Run(() =>
                    {
                        foreach (var item in items)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            int n = CountFilesForDelete(item.FullPath, item.IsFolder, cts.Token);
                            fileCounts[item] = n;
                            totalFiles += n;
                        }
                    }, cts.Token);
                }
                catch (OperationCanceledException) { }
            }

            if (cts.IsCancellationRequested)
            {
                SetStatus("Deletion canceled.");
                return;
            }

            SetStatus(permanent
                ? $"Deleting {totalFiles} file(s)... [ESC to cancel]"
                : $"Moving {totalFiles} file(s) to Recycle Bin... [ESC to cancel]", forceUpdate: true);

            // ---------------------------------------------------------------------
            // PASS 2: delete
            // ---------------------------------------------------------------------
            await Task.Run(async () =>
            {
                // Every chunk is one shell call and one Recycle Bin entry, so a
                // larger chunk means fewer round trips. Progress is time-throttled
                // anyway, so responsiveness does not depend on this number.
                const int ChunkSize = 25;

                bool canceled = false;

                var uiUpdateStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var uisToRemove = new List<FileEntry>();

                // Throttled status refresh, also callable from inside recursive deletion
                void ReportProgress(bool force)
                {
                    if (cts.IsCancellationRequested) return;
                    if (!force && uiUpdateStopwatch.ElapsedMilliseconds < 66) return;
                    uiUpdateStopwatch.Restart();

                    List<FileEntry> batchToRemove;
                    lock (uisToRemove)
                    {
                        batchToRemove = uisToRemove.ToList();
                        uisToRemove.Clear();
                    }

                    int currentFiles = Volatile.Read(ref deletedFiles);
                    string timeElapsed = $"{totalTimeStopwatch.Elapsed.TotalSeconds:0.000}s";

                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (sourcePane.CurrentPath == startingPath)
                        {
                            // The entries in the batch are the very objects the pane
                            // holds, so removal is a reference lookup. Searching by
                            // full path made this O(n2) on a large selection.
                            foreach (var rm in batchToRemove) sourcePane.Items.Remove(rm);
                        }

                        // Checked here, on the UI thread: an update queued a moment before
                        // the last file was removed must not repaint "[ESC to cancel]"
                        // over the final summary.
                        if (!Volatile.Read(ref finished) && !cts.IsCancellationRequested)
                        {
                            bool isForeground = sourcePane.CurrentPath == startingPath && _activePane == sourcePane;
                            string escHint = isForeground ? " [ESC to cancel]" : " (Background)";

                            SetStatus(permanent
                                ? $"Deleting... ({currentFiles} / {totalFiles} files) — Time: {timeElapsed}{escHint}"
                                : $"Moving to Recycle Bin... ({currentFiles} / {totalFiles} files) — Time: {timeElapsed}{escHint}", forceUpdate: true);
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }

                void MarkRemoved(FileEntry entry)
                {
                    Interlocked.Increment(ref okItems);
                    lock (uisToRemove) { uisToRemove.Add(entry); }
                }

                void CountFilesOf(FileEntry entry)
                {
                    Interlocked.Add(ref deletedFiles, fileCounts.TryGetValue(entry, out int n) ? n : 1);
                }

                var recycleBatch = new List<FileEntry>(ChunkSize);

                for (int i = 0; i < items.Count && !canceled;)
                {
                    if (cts.Token.IsCancellationRequested) break;

                    int end = Math.Min(i + ChunkSize, items.Count);
                    recycleBatch.Clear();

                    for (; i < end; i++)
                    {
                        var item = items[i];

                        if (cts.Token.IsCancellationRequested) { canceled = true; break; }

                        bool isSsh = item.FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

                        if (!permanent && !isSsh)
                        {
                            recycleBatch.Add(item);
                            continue;
                        }

                        bool itemSuccess = true;

                        if (isSsh)
                        {
                            while (true)
                            {
                                try
                                {
                                    Providers.SshFileSystemProvider.RemoteDelete(item.FullPath);
                                    CountFilesOf(item);
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    var action = OnFailure(item.FullPath, ex.Message);
                                    if (action == FailureAction.Retry) continue;
                                    if (action == FailureAction.Abort) canceled = true;
                                    itemSuccess = false;
                                    break;
                                }
                            }
                        }
                        else if (item.IsFolder)
                        {
                            try
                            {
                                // Per-file decisions are taken inside, so the unlocked
                                // files around a locked one are still removed
                                Win32FastDeleteDirectory(item.FullPath,
                                    () => { Interlocked.Increment(ref deletedFiles); ReportProgress(false); },
                                    OnFailure, cts.Token);
                            }
                            catch (OperationCanceledException) { canceled = true; }

                            if (Directory.Exists(item.FullPath)) itemSuccess = false;
                        }
                        else
                        {
                            while (true)
                            {
                                try
                                {
                                    File.Delete(item.FullPath);
                                    Interlocked.Increment(ref deletedFiles);
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    // Read-only or hidden alone is enough to be refused,
                                    // and that is worth clearing without asking
                                    ClearBlockingAttributes(item.FullPath, isDirectory: false);

                                    try
                                    {
                                        File.Delete(item.FullPath);
                                        Interlocked.Increment(ref deletedFiles);
                                        break;
                                    }
                                    catch { }

                                    var action = OnFailure(item.FullPath, ex.Message);
                                    if (action == FailureAction.Retry) continue;
                                    if (action == FailureAction.Abort) canceled = true;
                                    itemSuccess = false;
                                    break;
                                }
                            }
                        }

                        if (itemSuccess) MarkRemoved(item);
                        if (canceled) break;
                    }

                    if (recycleBatch.Count > 0)
                    {
                        var batch = recycleBatch;

                        await sta.RunAsync(() =>
                        {
                            try
                            {
                                // One shell call for the whole chunk. The shell returns a
                                // single verdict for it, so a failure says nothing about
                                // which item is to blame — that is what the retry is for.
                                if (batch.Count > 1 &&
                                    RecycleHelper.SendToRecycleBin(batch.Select(x => x.FullPath), silent: true))
                                {
                                    foreach (var item in batch)
                                    {
                                        CountFilesOf(item);
                                        MarkRemoved(item);
                                    }
                                    return;
                                }

                                foreach (var item in batch)
                                {
                                    if (cts.Token.IsCancellationRequested) { canceled = true; return; }

                                    // The batch may have moved this one before failing on a
                                    // later file; asking about it now would be a phantom
                                    // failure
                                    if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
                                    {
                                        CountFilesOf(item);
                                        MarkRemoved(item);
                                        continue;
                                    }

                                    bool done = false;

                                    while (!done)
                                    {
                                        if (RecycleHelper.SendToRecycleBin(new[] { item.FullPath }, silent: true))
                                        {
                                            CountFilesOf(item);
                                            MarkRemoved(item);
                                            break;
                                        }

                                        if (item.IsFolder)
                                        {
                                            // Rejected as a whole. Go inside and take
                                            // everything that is not held; the questions
                                            // are asked there, about real files.
                                            RecycleTreeContents(item.FullPath, OnFailure,
                                                () => { Interlocked.Increment(ref deletedFiles); ReportProgress(false); },
                                                cts.Token);

                                            if (!Directory.Exists(item.FullPath)) MarkRemoved(item);
                                            break;
                                        }

                                        var action = OnFailure(item.FullPath, "The file is open in another program or access is denied.");

                                        switch (action)
                                        {
                                            case FailureAction.Retry: continue;
                                            case FailureAction.Abort: canceled = true; done = true; break;
                                            default: done = true; break;
                                        }
                                    }

                                    if (canceled) return;
                                }
                            }
                            catch (OperationCanceledException) { canceled = true; }
                        });
                    }

                    ReportProgress(force: false);
                }

                Volatile.Write(ref finished, true);

                // Final pass: only drop the remaining rows from the pane. No status
                // text here, otherwise a queued "[ESC to cancel]" line would land
                // after the summary and claim the operation is still running.
                ReportProgress(force: true);
            });
        }
        finally
        {
            this.PreviewKeyDown -= cancelHandler;

            totalTimeStopwatch.Stop();
            IsStatusLocked = false;
        }

        if (sourcePane.CurrentPath == startingPath)
        {
            if (!string.IsNullOrEmpty(itemToRestore))
            {
                var target = sourcePane.Items.FirstOrDefault(x => x.Name.Equals(itemToRestore, StringComparison.OrdinalIgnoreCase));
                if (target != null) sourcePane.SetSelectedItem(target);
                else if (sourcePane.Items.Count > 0) sourcePane.SetSelectedItem(sourcePane.Items[0]);
            }

            await SyncPanesIfSamePath(sourcePane);
            _ = Dispatcher.BeginInvoke(new Action(() => sourcePane.FocusPanel()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // Final operation time in the required format
        string finalTimeElapsed = $"{totalTimeStopwatch.Elapsed.TotalSeconds:0.000}s";

        if (cts.IsCancellationRequested)
        {
            SetStatus($"Deletion canceled. {deletedFiles} out of {totalFiles} file(s) deleted. — Time: {finalTimeElapsed}");
        }
        else
        {
            ShowSummary(permanent ? "Delete" : "Recycle", deletedFiles, okItems, errors, finalTimeElapsed);
        }
    }

    // =========================================================================
    // Recycles the contents of a directory the shell refused as a whole.
    //
    // One call per directory level, not per file: a shell operation costs
    // milliseconds and creates its own undo record, so a folder with five
    // thousand files would otherwise mean five thousand calls and a Recycle Bin
    // full of individual entries. Only the level that actually fails is retried
    // file by file — which is also what turns "access denied on obj" into the
    // name of the file holding the lock.
    //
    // Must run on an STA thread.
    // =========================================================================
    private static void RecycleTreeContents(string path, FailureHandler onFailure, Action onFileRecycled, CancellationToken token)
    {
        try
        {
            var files = Directory.GetFiles(path, "*", s_flatEnumOptions);

            if (files.Length > 0)
            {
                if (files.Length > 1 && RecycleHelper.SendToRecycleBin(files, silent: true))
                {
                    for (int i = 0; i < files.Length; i++) onFileRecycled();
                }
                else
                {
                    foreach (var file in files)
                    {
                        token.ThrowIfCancellationRequested();

                        while (true)
                        {
                            if (RecycleHelper.SendToRecycleBin(new[] { file }, silent: true))
                            {
                                onFileRecycled();
                                break;
                            }

                            // Read-only or hidden on its own is enough to be refused,
                            // and clearing that needs no question
                            ClearBlockingAttributes(file, isDirectory: false);

                            if (RecycleHelper.SendToRecycleBin(new[] { file }, silent: true))
                            {
                                onFileRecycled();
                                break;
                            }

                            var action = onFailure(file, "The file is open in another program or access is denied.");

                            if (action == FailureAction.Retry) continue;
                            if (action == FailureAction.Abort) throw new OperationCanceledException();
                            break;
                        }
                    }
                }
            }

            foreach (var dir in Directory.GetDirectories(path, "*", s_flatEnumOptions))
            {
                token.ThrowIfCancellationRequested();

                // A junction is recycled as the link it is. Walking into it would
                // empty the folder it points at.
                if (IsReparse(File.GetAttributes(dir)))
                {
                    RecycleHelper.SendToRecycleBin(new[] { dir }, silent: true);
                    continue;
                }

                RecycleTreeContents(dir, onFailure, onFileRecycled, token);
            }

            // Empty by now unless something inside was left behind
            if (!Directory.EnumerateFileSystemEntries(path).Any())
                RecycleHelper.SendToRecycleBin(new[] { path }, silent: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            onFailure(path, ex.Message);
        }
    }

    // =========================================================================
    // HIGH-PERFORMANCE RECURSIVE DELETE
    //
    // Every file is deleted on its own and a failure asks what to do instead of
    // aborting: one locked file used to take the whole directory down with it and
    // leave the hundreds of unlocked files beside it untouched.
    //
    // One directory read per level, into an array. Two reasons: EnumerateFiles
    // followed by EnumerateDirectories opened the same directory twice, and
    // deleting during a lazy enumeration can make FindNextFile skip entries,
    // which then shows up as an unexplained "directory is not empty".
    //
    // s_flatEnumOptions sets AttributesToSkip = 0. With the default value hidden
    // and system files are never enumerated, never deleted, and the final
    // Directory.Delete fails because the folder still has content in it.
    // =========================================================================
    private static void Win32FastDeleteDirectory(string path, Action? onFileDeleted, FailureHandler onFailure, CancellationToken token = default)
    {
        try
        {
            var entries = new FileSystemEnumerable<(string Path, FileAttributes Attributes, bool IsDirectory)>(
                path,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.Attributes, e.IsDirectory),
                s_flatEnumOptions).ToArray();

            foreach (var entry in entries)
            {
                if (entry.IsDirectory) continue;

                token.ThrowIfCancellationRequested();

                while (true)
                {
                    try
                    {
                        File.Delete(entry.Path);
                        onFileDeleted?.Invoke();
                        break;
                    }
                    catch (Exception ex)
                    {
                        ClearBlockingAttributes(entry.Path, isDirectory: false);

                        try
                        {
                            File.Delete(entry.Path);
                            onFileDeleted?.Invoke();
                            break;
                        }
                        catch { }

                        var action = onFailure(entry.Path, ex.Message);

                        if (action == FailureAction.Retry) continue;
                        if (action == FailureAction.Abort) throw new OperationCanceledException();
                        break;
                    }
                }
            }

            foreach (var entry in entries)
            {
                if (!entry.IsDirectory) continue;

                token.ThrowIfCancellationRequested();

                // A junction or symlink is removed as the link itself. Recursing
                // into it would delete the contents of whatever it points at.
                if (IsReparse(entry.Attributes))
                {
                    try { Directory.Delete(entry.Path, false); }
                    catch (Exception ex) { onFailure(entry.Path, ex.Message); }
                    continue;
                }

                Win32FastDeleteDirectory(entry.Path, onFileDeleted, onFailure, token);
            }

            try { Directory.Delete(path, false); }
            catch (UnauthorizedAccessException)
            {
                try
                {
                    ClearBlockingAttributes(path, isDirectory: true);
                    Directory.Delete(path, false);
                }
                catch { /* not empty: something inside was left behind and reported */ }
            }
            catch { /* not empty: something inside was left behind and reported */ }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            onFailure(path, ex.Message);
        }
    }

    private bool ConfirmDelete(IEnumerable<string> names, bool permanent, bool anySsh)
    {
        var list = names.ToList();
        string preview = string.Join(", ", list.Take(5)) + (list.Count > 5 ? $", +{list.Count - 5} more" : "");
        string title = permanent ? "Confirm Permanent Delete" : "Confirm Delete";
        string action = permanent ? $"Permanently delete {list.Count} item(s)?" : $"Delete {list.Count} item(s) to Recycle Bin?";

        if (anySsh)
            action = $"Permanently delete {list.Count} item(s)?\nRemote (SSH) items cannot be sent to the Recycle Bin and will be deleted permanently.";

        var dlg = new ConfirmDialog($"{action}\n{preview}", title) { Owner = this };
        return dlg.ShowDialog() == true;
    }

    // =========================================================================
    // Final line in the status bar, plus a dialog when something was left behind.
    //
    // Naming what stayed on disk is the whole point of the dialog: the shell
    // reports "access denied" on the top folder, this reports the full path of
    // every file that is actually holding it.
    // =========================================================================
    private void ShowSummary(string action, int files, int items, List<string> errors, string timeElapsed)
    {
        string done = $"{action}: {files} file(s) in {items} item(s) done";

        if (errors.Count == 0)
        {
            SetStatus($"{done}. \u2014 Time: {timeElapsed}");
            return;
        }

        SetStatus($"{done}, {errors.Count} failed. \u2014 Time: {timeElapsed}");

        const int maxShown = 10;

        string list = string.Join(Environment.NewLine, errors.Take(maxShown).Select(e => $"  - {e}"));

        if (errors.Count > maxShown)
            list += $"{Environment.NewLine}{Environment.NewLine}  ...and {errors.Count - maxShown} more.";

        string message =
            $"{action} completed, but some items were left behind.{Environment.NewLine}{Environment.NewLine}" +
            $"Succeeded: {files}{Environment.NewLine}" +
            $"Failed: {errors.Count}{Environment.NewLine}{Environment.NewLine}" +
            $"Still on disk:{Environment.NewLine}{list}";

        MessageDialog.Show(this, message, $"{action} finished with errors");
    }
}
