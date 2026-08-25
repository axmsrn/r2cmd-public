using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using R2Cmd.Providers;

namespace R2Cmd;

public enum FileOperation { Copy, Move, Pack }

public partial class ProgressWindow : Window
{
    // =========================================================================
    // WIN32 API CORE INTEGRATION FOR EXTREME PERFORMANCE
    // =========================================================================
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string lpExistingFileName, string lpNewFileName, CopyProgressRoutine lpProgressRoutine, IntPtr lpData, ref int pbCancel, uint dwCopyFlags);

    private delegate CopyProgressResult CopyProgressRoutine(
        long TotalFileSize, long TotalBytesTransferred, long StreamSize, long StreamBytesTransferred,
        uint dwStreamNumber, uint dwCallbackReason, IntPtr hSourceFile, IntPtr hDestinationFile, IntPtr lpData);

    private enum CopyProgressResult : uint { PROGRESS_CONTINUE = 0, PROGRESS_CANCEL = 1 }
    // =========================================================================

    private readonly List<FileEntry> _sourceItems;
    private readonly string _destination; // target folder, or the .zip path when packing
    private readonly FileOperation _operation;
    private readonly CancellationTokenSource _cts = new();

    // Timer for the whole operation
    private readonly System.Diagnostics.Stopwatch _operationStopwatch = new();

    private long _totalBytes;
    private long _copiedBytes;
    private int _totalFiles;
    private int _copiedFiles;

    // Set whenever an entry was skipped or failed. Reported in the final line so a
    // partial Move is never mistaken for a complete one.
    private bool _anythingSkipped;

    public bool IsCancelled { get; private set; } = false;
    public int SuccessfullyProcessedFiles { get; private set; } = 0;

    private OverwriteChoice _globalChoice = OverwriteChoice.None;
    private bool _symlinkPrivilegeMissing;
    private bool _skipAllErrors = false;

    private long _lastUiUpdateMs = 0;
    private const int UiUpdateIntervalMs = 40; // ~25 FPS UI refresh rate

    private const int TransferBufferSize = 1048576;

    // Enumeration must include hidden and system files. The default value of
    // AttributesToSkip is Hidden|System, which silently under-counts the totals.
    private static readonly EnumerationOptions s_recursiveEnumOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true
    };

    public event EventHandler? BackgroundRequested;

    // =========================================================================
    // Progress line consumed by the main window status bar (same place where the
    // delete operation reports its own timer).
    // =========================================================================
    public event EventHandler<string>? StatusUpdated;

    // Short operation name used by the host window when it builds the final message
    public string OperationName { get; }

    // Total operation time, readable by the host window after the window is closed
    public TimeSpan Elapsed => _operationStopwatch.Elapsed;

    public ProgressWindow(List<FileEntry> sourceItems, string destination, FileOperation operation)
    {
        InitializeComponent();
        _sourceItems = sourceItems;
        _destination = destination;
        _operation = operation;
        this.ShowInTaskbar = false;

        Title = _operation switch
        {
            FileOperation.Copy => "Copying...",
            FileOperation.Move => "Moving...",
            FileOperation.Pack => "Archiving...",
            _ => "Processing..."
        };

        OperationName = _operation switch
        {
            FileOperation.Copy => "Copy",
            FileOperation.Move => "Move",
            FileOperation.Pack => "Pack",
            _ => "Operation"
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme);
    }

    // Raises the status line for the main window status bar. Always called on the UI thread.
    private void PushStatus(string text) => StatusUpdated?.Invoke(this, text);

    // =========================================================================
    // Final summary line. The host window keeps it in the status bar after this
    // window is closed, so the result of the operation stays readable.
    // =========================================================================
    public string GetFinalStatus()
    {
        string time = FormatElapsed(_operationStopwatch.Elapsed);
        string head = IsCancelled ? $"{OperationName} canceled" : $"{OperationName} finished";
        string skipped = _anythingSkipped ? ", some items skipped" : "";

        return $"{head}: {SuccessfullyProcessedFiles} / {_totalFiles} files " +
               $"({FormatSizeStable(_copiedBytes, padded: false)} / {FormatSizeStable(_totalBytes, padded: false)}){skipped} — Time: {time}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Force the OS to activate this window and bring it to the foreground.
        // Since the parent window is disabled, focus might drop to the desktop otherwise.
        this.Activate();
        this.Focus();

        FocusCancelButton();

        try
        {
            await RunOperationAsync();
        }
        catch (OperationCanceledException)
        {
            IsCancelled = true;
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, $"Critical Error: {ex.Message}", "Error");
            IsCancelled = true;
        }
        finally
        {
            // Stop the timer when the operation is completely done or canceled
            _operationStopwatch.Stop();
            Close();
        }
    }

    private void BtnBackground_Click(object sender, RoutedEventArgs e)
    {
        BackgroundRequested?.Invoke(this, EventArgs.Empty);
        this.ShowInTaskbar = true;
        this.WindowState = WindowState.Minimized;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => CancelOperation();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The window is shown with Show(), not ShowDialog(), so ESC must be handled
        // here. A button with IsCancel="True" would try to set DialogResult and throw.
        if (e.Key == Key.Escape)
        {
            CancelOperation();
            e.Handled = true;
        }
    }

    private void CancelOperation()
    {
        if (btnCancel.IsEnabled)
        {
            btnCancel.IsEnabled = false;
            btnCancel.Content = "Canceling...";
            _cts.Cancel();
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (btnCancel.IsEnabled) _cts.Cancel();
    }

    // Both logical and keyboard focus, deferred so it survives window activation
    private void FocusCancelButton()
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            btnCancel.Focus();
            Keyboard.Focus(btnCancel);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EnsureWindowIsVisible()
    {
        if (this.WindowState == WindowState.Minimized)
        {
            this.WindowState = WindowState.Normal;
        }
        ForceActivateSelfAndOwner();
    }

    private void ForceActivateSelfAndOwner()
    {
        Helpers.ForceActivate(this);
        if (this.Owner != null) Helpers.ForceActivate(this.Owner);

        // Reclaim keyboard focus after a dialog stole it
        FocusCancelButton();
    }

    private void HandleErrorDecision(string sourcePath, Exception ex, string title)
    {
        if (_skipAllErrors)
        {
            UpdateTotalProgressOnSkip(sourcePath);
            return;
        }

        ErrorChoice choice = ErrorChoice.Cancel;
        Application.Current.Dispatcher.Invoke(() =>
        {
            EnsureWindowIsVisible();
            var dlg = new ErrorActionDialog($"Cannot process path:\n{Helpers.LastSegment(sourcePath)}\n\n[{ex.GetType().Name}] {ex.Message}\n\nWhat would you like to do?", title) { Owner = this };
            dlg.ShowDialog();
            choice = dlg.Choice;
            ForceActivateSelfAndOwner();
        });

        if (choice == ErrorChoice.Cancel)
        {
            _cts.Cancel();
            throw new OperationCanceledException();
        }

        if (choice == ErrorChoice.SkipAll) _skipAllErrors = true;

        UpdateTotalProgressOnSkip(sourcePath);
    }

    private async Task RunOperationAsync()
    {
        // Start the timer at the very beginning of the operation
        _operationStopwatch.Start();

        if (_operation == FileOperation.Pack)
        {
            await PackOperationAsync();
            return;
        }

        txtCurrentFile.Text = "Calculating total size...";
        PushStatus($"{OperationName}: calculating total size...");

        await Task.Run(() => CalculateTotals(_sourceItems), _cts.Token);

        var destProvider = FileSystemFactory.GetProvider(_destination);

        // =========================================================================
        // Two phases: work out what every item is, then carry it out.
        //
        // The split exists for the archive batch. Extracting one file at a time
        // reopens the archive for each of them, and in a solid archive — 7z or rar,
        // which is what an installer usually is — reaching entry N means
        // decompressing everything in front of it again. Five files from the end of
        // a solid archive cost five full passes that way; grouped by archive they
        // cost one.
        // =========================================================================
        var plan = BuildTransferPlan(destProvider);

        // =========================================================================
        // The whole loop runs off the UI thread: synchronous fast paths such as a
        // local File.Move would otherwise block WPF and the window would never paint.
        // =========================================================================
        await Task.Run(async () =>
        {
            var archivesDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in plan.Steps)
            {
                _cts.Token.ThrowIfCancellationRequested();

                switch (step.Kind)
                {
                    case PlanKind.Normal:
                        await ProcessEntryUniversalAsync(step.Item.FullPath, step.TargetPath, step.Item.IsFolder);
                        break;

                    case PlanKind.ArchiveFolder:
                        ExtractArchiveFolder(step, destProvider);
                        break;

                    case PlanKind.ArchiveFile:
                        // The whole group leaves with the first of its files; the
                        // rest are already covered by that single pass
                        if (archivesDone.Add(step.ArchivePath!))
                            ExtractArchiveFileGroup(step.ArchivePath!, plan.FileGroups[step.ArchivePath!]);
                        break;
                }
            }
        });
    }

    private enum PlanKind { Normal, ArchiveFolder, ArchiveFile }

    private sealed record PlanStep(PlanKind Kind, FileEntry Item, string TargetPath, string? ArchivePath, string? InternalPath);

    private sealed record TransferPlan(List<PlanStep> Steps, Dictionary<string, List<PlanStep>> FileGroups);

    // Classification only: no disk writing happens here, so an unsupported
    // destination is refused before a single byte has been copied.
    private TransferPlan BuildTransferPlan(IFileSystemProvider destProvider)
    {
        var steps = new List<PlanStep>(_sourceItems.Count);
        var fileGroups = new Dictionary<string, List<PlanStep>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _sourceItems)
        {
            string targetPath = destProvider.CombinePaths(_destination, item.Name);

            var (destArchive, destInternal) = ArchiveService.ParseVirtualPath(targetPath);
            if (destArchive != null && !string.IsNullOrEmpty(destInternal) && File.Exists(destArchive))
                throw new InvalidOperationException("Writing files INTO an archive is not supported.");

            var (srcArchive, srcInternal) = ArchiveService.ParseVirtualPath(item.FullPath);
            bool insideArchive = srcArchive != null && !string.IsNullOrEmpty(srcInternal) && File.Exists(srcArchive);

            if (!insideArchive)
            {
                steps.Add(new PlanStep(PlanKind.Normal, item, targetPath, null, null));
                continue;
            }

            if (destProvider is not LocalDiskProvider)
                throw new InvalidOperationException("Extracting archives directly to remote servers is not supported yet.");

            if (item.IsFolder)
            {
                steps.Add(new PlanStep(PlanKind.ArchiveFolder, item, targetPath, srcArchive, srcInternal));
                continue;
            }

            var step = new PlanStep(PlanKind.ArchiveFile, item, targetPath, srcArchive, srcInternal);
            steps.Add(step);

            if (!fileGroups.TryGetValue(srcArchive!, out var group))
                fileGroups[srcArchive!] = group = new List<PlanStep>();

            group.Add(step);
        }

        return new TransferPlan(steps, fileGroups);
    }

    // A whole folder out of an archive is already a single pass
    private void ExtractArchiveFolder(PlanStep step, IFileSystemProvider destProvider)
    {
        UpdateUi($"Extracting: {step.Item.Name}", 0, 0, force: false);

        destProvider.CreateDirectory(step.TargetPath);

        ArchiveService.ExtractFolder(step.ArchivePath!, step.InternalPath!, step.TargetPath,
            ConfirmArchiveOverwrite, MakeArchiveProgress());

        SuccessfullyProcessedFiles++;
    }

    // =========================================================================
    // Turns the extractor's byte level reports into the two counters this window
    // keeps.
    //
    // The extractor sends absolute positions inside the current file, so the
    // delta has to be worked out here. A report of zero marks the start of a new
    // file, and a report that reaches the total marks its end — which is the only
    // place _copiedFiles may be raised.
    // =========================================================================
    private Action<string, long, long> MakeArchiveProgress()
    {
        long reported = 0;

        return (fileName, copied, total) =>
        {
            if (copied == 0) reported = 0;

            long delta = copied - reported;
            if (delta > 0)
            {
                _copiedBytes += delta;
                reported = copied;
            }

            if (copied >= total) _copiedFiles++;

            UpdateUi(fileName, copied, total, force: false);
        };
    }

    // Every file selected from one archive, in one pass over it
    private void ExtractArchiveFileGroup(string archivePath, List<PlanStep> group)
    {
        UpdateUi($"Extracting {group.Count} file(s) from {Path.GetFileName(archivePath)}...", 0, 0, force: true);

        // Built by hand rather than with ToDictionary: the same entry selected
        // twice would throw there
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in group) map[step.InternalPath!] = step.TargetPath;

        int written = ArchiveService.ExtractFiles(archivePath, map, ConfirmArchiveOverwrite, MakeArchiveProgress());

        SuccessfullyProcessedFiles += written;

        // Skipped by the overwrite dialog, or simply not present in the archive
        if (written < map.Count) _anythingSkipped = true;
    }

    private void CalculateTotals(IEnumerable<FileEntry> entries)
    {
        foreach (var entry in entries)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var provider = FileSystemFactory.GetProvider(entry.FullPath);

            if (provider is SshFileSystemProvider)
            {
                try
                {
                    var (files, bytes) = SshFileSystemProvider.RemoteSumTree(entry.FullPath);
                    _totalFiles += files;
                    _totalBytes += bytes;
                }
                catch
                {
                    _totalFiles++;
                    _totalBytes += entry.Size;
                }
                continue;
            }

            var (srcArchive, srcInternal) = ArchiveService.ParseVirtualPath(entry.FullPath);
            if (srcArchive != null && !string.IsNullOrEmpty(srcInternal) && File.Exists(srcArchive))
            {
                if (entry.IsFolder)
                {
                    try
                    {
                        // ArchiveService opens the archive itself, so the payload
                        // offset of a self-extracting executable is handled there.
                        // This used to reach for its private OpenArchiveStream by
                        // reflection.
                        var (files, bytes) = ArchiveService.SumTree(srcArchive, srcInternal);
                        _totalFiles += files;
                        _totalBytes += bytes;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        _totalFiles++;
                        _totalBytes += entry.Size;
                    }
                }
                else
                {
                    _totalFiles++;
                    _totalBytes += entry.Size;
                }
                continue;
            }

            if (entry.IsFolder)
            {
                if (provider is LocalDiskProvider)
                {
                    var di = new DirectoryInfo(entry.FullPath);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        _totalFiles++;
                        continue;
                    }
                    try
                    {
                        foreach (var file in di.EnumerateFiles("*", s_recursiveEnumOptions))
                        {
                            // Instant cancellation check to prevent UI hanging during large scans
                            _cts.Token.ThrowIfCancellationRequested();

                            _totalFiles++;
                            _totalBytes += file.Length;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch { } // Ignore access denied and other system-level read errors
                }
                else
                {
                    _totalFiles++;
                }
            }
            else
            {
                _totalFiles++;
                _totalBytes += entry.Size;
            }
        }
    }

    // =========================================================================
    // Returns TRUE only when the entry (and, for folders, its whole subtree) was
    // fully transferred.
    //
    // This return value is what makes Move safe: the source is deleted only when
    // everything below it actually arrived at the destination. Previously the
    // delete ran unconditionally, so a file skipped by the overwrite dialog or by
    // an error was removed from the source without ever being copied.
    // =========================================================================
    private async Task<bool> ProcessEntryUniversalAsync(string sourcePath, string destPath, bool isFolder)
    {
        _cts.Token.ThrowIfCancellationRequested();
        var srcProvider = FileSystemFactory.GetProvider(sourcePath);
        var dstProvider = FileSystemFactory.GetProvider(destPath);

        try
        {
            if (srcProvider is LocalDiskProvider)
            {
                FileAttributes attr;
                try { attr = File.GetAttributes(sourcePath); } catch { attr = FileAttributes.Normal; }
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    return HandleSymlinkEntry(sourcePath, destPath, isFolder);
                }
            }

            if (isFolder)
            {
                if (!dstProvider.Exists(destPath))
                {
                    dstProvider.CreateDirectory(destPath);
                }

                var (children, error) = await srcProvider.ReadDirectoryAsync(sourcePath, _cts.Token);
                if (error != null) throw new IOException(error);

                bool allChildrenOk = true;

                foreach (var child in children)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    if (child.Name == ".." || child.Name == ".") continue;
                    string childSrc = srcProvider.CombinePaths(sourcePath, child.Name);
                    string childDst = dstProvider.CombinePaths(destPath, child.Name);

                    bool childOk = await ProcessEntryUniversalAsync(childSrc, childDst, child.IsFolder);
                    if (!childOk) allChildrenOk = false;
                }

                // Delete the source folder ONLY when nothing inside was left behind
                if (_operation == FileOperation.Move && allChildrenOk)
                {
                    await srcProvider.DeleteAsync(sourcePath, _cts.Token);
                }

                return allChildrenOk;
            }

            if (dstProvider.Exists(destPath))
            {
                if (_globalChoice == OverwriteChoice.SkipAll) { UpdateTotalProgressOnSkip(sourcePath); return false; }
                if (!ResolveOverwriteDecision(Helpers.LastSegment(destPath))) { UpdateTotalProgressOnSkip(sourcePath); return false; }
            }

            if (srcProvider is LocalDiskProvider && dstProvider is LocalDiskProvider)
            {
                CopySingleFileWin32(sourcePath, destPath);
            }
            else
            {
                await CopySingleFileUniversalAsync(sourcePath, destPath, srcProvider, dstProvider);
            }

            if (_operation == FileOperation.Move)
            {
                await srcProvider.DeleteAsync(sourcePath, _cts.Token);
            }

            _copiedFiles++;
            SuccessfullyProcessedFiles++;

            // force: false — forcing a dispatcher update per tiny file floods the
            // WPF message queue when processing something like node_modules
            UpdateUi(Helpers.LastSegment(sourcePath), 1, 1, force: false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            HandleErrorDecision(sourcePath, ex, "Transfer Error");
            return false;
        }
    }

    private void CopySingleFileWin32(string sourcePath, string destPath)
    {
        if (_operation == FileOperation.Move)
        {
            if (string.Equals(Path.GetPathRoot(sourcePath), Path.GetPathRoot(destPath), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(destPath)) File.SetAttributes(destPath, FileAttributes.Normal);
                File.Move(sourcePath, destPath, true);

                try { _copiedBytes += new FileInfo(destPath).Length; } catch { }
                return;
            }
        }

        if (File.Exists(destPath))
        {
            try { File.SetAttributes(destPath, FileAttributes.Normal); } catch { }
        }

        int cancelFlag = 0;
        using var ctr = _cts.Token.Register(() => cancelFlag = 1);
        long previousTransferred = 0;
        string fileName = Helpers.LastSegment(sourcePath);

        CopyProgressRoutine callback = (TotalFileSize, TotalBytesTransferred, StreamSize, StreamBytesTransferred, dwStreamNumber, dwCallbackReason, hSourceFile, hDestinationFile, lpData) =>
        {
            long delta = TotalBytesTransferred - previousTransferred;
            if (delta > 0)
            {
                _copiedBytes += delta;
                previousTransferred = TotalBytesTransferred;
            }

            UpdateUi(fileName, TotalBytesTransferred, TotalFileSize, force: false);
            return cancelFlag == 1 ? CopyProgressResult.PROGRESS_CANCEL : CopyProgressResult.PROGRESS_CONTINUE;
        };

        // Runs in the ThreadPool thanks to the parent Task.Run, keeping the UI responsive
        bool success = CopyFileEx(sourcePath, destPath, callback, IntPtr.Zero, ref cancelFlag, 0);

        if (!success)
        {
            if (cancelFlag == 1) throw new OperationCanceledException();
            int err = Marshal.GetLastWin32Error();
            throw new IOException($"Win32 Kernel Copy failed. Error code: {err}");
        }

        try { File.SetAttributes(destPath, File.GetAttributes(sourcePath)); } catch { }
    }

    private async Task CopySingleFileUniversalAsync(string sourcePath, string destPath, IFileSystemProvider srcProvider, IFileSystemProvider dstProvider)
    {
        await using Stream srcStream = await srcProvider.OpenReadAsync(sourcePath, _cts.Token);
        await using Stream dstStream = await dstProvider.OpenWriteAsync(destPath, _cts.Token);

        // Pooled buffer: a fresh 1 MB array per file goes straight to the LOH and
        // makes copying thousands of small files a GC problem.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            int bytesRead;
            long currentFileCopied = 0;
            long totalFileSize = 0;

            try { totalFileSize = srcStream.Length; } catch { }

            string fileName = Helpers.LastSegment(sourcePath);

            while ((bytesRead = await srcStream.ReadAsync(buffer, _cts.Token)) > 0)
            {
                await dstStream.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token);
                currentFileCopied += bytesRead;
                _copiedBytes += bytesRead;

                UpdateUi(fileName, currentFileCopied, totalFileSize, force: false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Called per file while extracting, including from inside ExtractFolder.
    // Honours a previous "all" answer and cancels the whole operation on Cancel.
    private bool ConfirmArchiveOverwrite(string destPath) =>
        ResolveOverwriteDecision(Helpers.LastSegment(destPath));

    private bool ResolveOverwriteDecision(string destName)
    {
        if (_globalChoice == OverwriteChoice.SkipAll) return false;
        if (_globalChoice == OverwriteChoice.OverwriteAll) return true;

        OverwriteChoice choice = OverwriteChoice.Cancel;

        // Safe cross-thread UI invoker since the loop runs in Task.Run
        Application.Current.Dispatcher.Invoke(() =>
        {
            EnsureWindowIsVisible();
            var dlg = new OverwriteDialog($"File already exists:\n{destName}\n\nWhat would you like to do?") { Owner = this };
            dlg.ShowDialog();
            choice = dlg.Choice;
            ForceActivateSelfAndOwner();
        });

        switch (choice)
        {
            case OverwriteChoice.Cancel: _cts.Cancel(); throw new OperationCanceledException();
            case OverwriteChoice.SkipAll: _globalChoice = OverwriteChoice.SkipAll; return false;
            case OverwriteChoice.Skip: return false;
            case OverwriteChoice.OverwriteAll: _globalChoice = OverwriteChoice.OverwriteAll; return true;
            default: return true;
        }
    }

    private bool HandleSymlinkEntry(string sourcePath, string destPath, bool isDir)
    {
        string? rawTarget = isDir ? new DirectoryInfo(sourcePath).LinkTarget : new FileInfo(sourcePath).LinkTarget;

        if (rawTarget is null || _symlinkPrivilegeMissing)
        {
            UpdateTotalProgressOnSkip(sourcePath);
            return false;
        }

        string target = rawTarget;
        if (!Path.IsPathRooted(rawTarget))
        {
            string? sourceDir = Path.GetDirectoryName(sourcePath);
            if (sourceDir != null) target = Path.GetFullPath(Path.Combine(sourceDir, rawTarget));
        }

        try
        {
            if (Directory.Exists(destPath)) Directory.Delete(destPath, true);
            else if (File.Exists(destPath))
            {
                File.SetAttributes(destPath, FileAttributes.Normal);
                File.Delete(destPath);
            }
        }
        catch { }

        try
        {
            if (isDir) Directory.CreateSymbolicLink(destPath, target);
            else File.CreateSymbolicLink(destPath, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _symlinkPrivilegeMissing = true;
            Application.Current.Dispatcher.Invoke(() =>
            {
                EnsureWindowIsVisible();
                MessageDialog.Show(this, "Cannot create symbolic links without elevated rights.\n\n" +
                    "To copy links AS links, either run this app as Administrator, or enable Windows Developer Mode.\n\n" +
                    "Remaining symbolic links will be skipped.", "Symbolic links");
                ForceActivateSelfAndOwner();
            });
            UpdateTotalProgressOnSkip(sourcePath);
            return false;
        }

        if (_operation == FileOperation.Move)
        {
            try { if (isDir) Directory.Delete(sourcePath, false); else File.Delete(sourcePath); } catch { }
        }

        _copiedFiles++;

        // Without this the pane is never reloaded when only symlinks were copied
        SuccessfullyProcessedFiles++;

        UpdateUi(Helpers.LastSegment(sourcePath), 0, 0, force: false);
        return true;
    }

    private void UpdateTotalProgressOnSkip(string sourcePath)
    {
        _anythingSkipped = true;

        if (FileSystemFactory.GetProvider(sourcePath) is LocalDiskProvider)
        {
            try
            {
                if (!Directory.Exists(sourcePath)) _copiedBytes += new FileInfo(sourcePath).Length;
            }
            catch { }
        }

        _copiedFiles++;
        UpdateUi(Helpers.LastSegment(sourcePath), 1, 1, force: false);
    }

    private void UpdateUi(string? currentFileName, long currentFileCopied, long totalFileSize, bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastUiUpdateMs < UiUpdateIntervalMs) return;

        _lastUiUpdateMs = now;
        long copiedBytesSnapshot = _copiedBytes;
        int copiedFilesSnapshot = _copiedFiles;

        string timeElapsed = FormatElapsed(_operationStopwatch.Elapsed);

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (currentFileName != null) txtCurrentFile.Text = currentFileName;

            if (totalFileSize > 0)
            {
                int percentage = (int)(currentFileCopied * 100 / totalFileSize);
                pbCurrentFile.Value = percentage;
                txtFileProgress.Text = $"{percentage}%";
            }
            else
            {
                pbCurrentFile.Value = 100;
                txtFileProgress.Text = "100%";
            }

            if (_totalBytes > 0) pbTotal.Value = (int)(copiedBytesSnapshot * 100 / _totalBytes);

            // The window shows counters and volume, the timer lives in the status bar
            txtTotalStatus.Text = $"Processed: {copiedFilesSnapshot} / {_totalFiles} files " +
                                  $"({FormatSizeStable(copiedBytesSnapshot)} / {FormatSizeStable(_totalBytes)})";

            PushStatus($"{OperationName}: {copiedFilesSnapshot} / {_totalFiles} files " +
                       $"({FormatSizeStable(copiedBytesSnapshot, padded: false)} / {FormatSizeStable(_totalBytes, padded: false)}) — Time: {timeElapsed}");
        });
    }

    private static readonly string[] s_sizeUnits = { "B", "KB", "MB", "GB", "TB" };

    // =========================================================================
    // File size without decimals and with fixed spacing, so the text never jumps.
    // =========================================================================
    private static string FormatSizeStable(long bytes, bool padded = true)
    {
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < s_sizeUnits.Length - 1)
        {
            size /= 1024;
            i++;
        }

        // Padded form is exactly 7 chars ("  45 MB") for the window label
        return padded ? $"{Math.Round(size),4} {s_sizeUnits[i],-2}" : $"{Math.Round(size)} {s_sizeUnits[i]}";
    }

    // =========================================================================
    // Elapsed time as mm:ss.fff. Minutes keep accumulating past 60 (e.g. 75:03.120),
    // so no hours field is needed and long operations still read correctly.
    // =========================================================================
    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds:000}";

    private async Task PackOperationAsync()
    {
        if (_sourceItems.Any(i => FileSystemFactory.GetProvider(i.FullPath) is not LocalDiskProvider))
            throw new InvalidOperationException("Packing is only supported for local files currently.");

        txtCurrentFile.Text = "Calculating total size...";
        PushStatus($"{OperationName}: calculating total size...");

        await Task.Run(() => CalculateTotals(_sourceItems), _cts.Token);

        await Task.Run(async () =>
        {
            bool success = false;
            try
            {
                using var fs = new FileStream(_destination, FileMode.Create);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

                foreach (var item in _sourceItems)
                {
                    await PackEntryAsync(zip, item.FullPath, item.Name);
                }
                success = true;
            }
            finally
            {
                if (!success && File.Exists(_destination)) { try { File.Delete(_destination); } catch { } }
            }
        });
    }

    private async Task PackEntryAsync(ZipArchive zip, string sourcePath, string entryName)
    {
        _cts.Token.ThrowIfCancellationRequested();

        try
        {
            FileAttributes attr;
            try { attr = File.GetAttributes(sourcePath); } catch { attr = FileAttributes.Normal; }

            if ((attr & FileAttributes.Directory) != 0)
            {
                var dir = new DirectoryInfo(sourcePath);

                // Fast empty directory check
                if (!dir.EnumerateFileSystemInfos().Any())
                    zip.CreateEntry(entryName + "/");

                foreach (var file in dir.EnumerateFiles())
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    await PackEntryAsync(zip, file.FullName, entryName + "/" + file.Name);
                }

                foreach (var subDir in dir.EnumerateDirectories())
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    await PackEntryAsync(zip, subDir.FullName, entryName + "/" + subDir.Name);
                }
                return;
            }

            string fileName = Path.GetFileName(sourcePath);

            // Optimal instead of SmallestSize: the latter is several times slower for
            // a size gain that is usually under one percent.
            var zipEntry = zip.CreateEntry(entryName, CompressionLevel.Optimal);

            await using var entryStream = zipEntry.Open();
            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
            try
            {
                int bytesRead;
                long currentFileCopied = 0;
                long totalFileSize = sourceStream.Length;

                while ((bytesRead = await sourceStream.ReadAsync(buffer, _cts.Token)) > 0)
                {
                    await entryStream.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token);
                    currentFileCopied += bytesRead;
                    _copiedBytes += bytesRead;

                    UpdateUi(fileName, currentFileCopied, totalFileSize, force: false);
                }

                _copiedFiles++;
                SuccessfullyProcessedFiles++;
                UpdateUi(fileName, totalFileSize, totalFileSize, force: false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            HandleErrorDecision(sourcePath, ex, "Pack Error");
        }
    }
}
