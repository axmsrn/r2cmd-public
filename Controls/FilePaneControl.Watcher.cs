using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R2Cmd.Providers;

namespace R2Cmd.Controls;

// Auto-update current folder (FileSystemWatcher): targeted list patches.
public partial class FilePaneControl
{
    // =========================================================================
    // Suspend/Resume is used by the host window while a copy/move/delete is
    // running: the operation writes into the watched folder itself and would
    // otherwise generate hundreds of events per second, each one patching the
    // list. The final RefreshAsync reloads everything anyway.
    //
    // If an earlier version of these two methods lives elsewhere in the project,
    // delete it — this is the only copy that should exist.
    // =========================================================================
    private int _updatesSuspended;

    public void SuspendUpdates() => _updatesSuspended++;

    public void ResumeUpdates()
    {
        if (_updatesSuspended > 0) _updatesSuspended--;
    }

    // _renamingEntry: never touch the list while a row is being edited.
    // _isBusy: a navigation is in flight, the list is about to be replaced.
    private bool UpdatesBlocked => _updatesSuspended > 0 || _renamingEntry != null || _isBusy;

    private void StartWatcher(string path, bool isLocal)
    {
        StopWatcher();
        if (!isLocal || string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,

                // Default is 8 KB. A busy folder overflows it and events are lost
                // silently, leaving the pane showing a stale listing.
                InternalBufferSize = 65536,

                EnableRaisingEvents = true
            };
            _watcher.Created += OnFsCreated;
            _watcher.Deleted += OnFsDeleted;
            _watcher.Renamed += OnFsRenamed;
            _watcher.Changed += OnFsChanged;
            _watcher.Error += OnFsError;
        }
        catch { _watcher = null; }  // e.g. no tracking permissions — silently skip auto-update
    }

    private void StopWatcher()
    {
        if (_watcher == null) return;
        try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
        _watcher = null;
    }

    // Events come from background thread — marshal to UI thread.
    private void OnFsCreated(object s, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => WatcherAddOrUpdate(e.FullPath)));

    private void OnFsDeleted(object s, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => WatcherRemove(e.FullPath)));

    private void OnFsRenamed(object s, RenamedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => { WatcherRemove(e.OldFullPath); WatcherAddOrUpdate(e.FullPath); }));

    private void OnFsChanged(object s, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => WatcherUpdate(e.FullPath)));

    // Buffer overflow: individual events are gone for good, so reload the folder.
    private void OnFsError(object s, ErrorEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (UpdatesBlocked) return;
            await RefreshAsync();
        }));

    private bool BelongsToCurrentDir(string fullPath)
    {
        string? dir = Path.GetDirectoryName(fullPath);
        return string.Equals(dir?.TrimEnd('\\'), CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private void WatcherAddOrUpdate(string fullPath)
    {
        if (UpdatesBlocked) return;
        if (!BelongsToCurrentDir(fullPath)) return;

        var existing = Items.FirstOrDefault(x =>
            string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { WatcherUpdate(fullPath); return; }

        var entry = BuildLocalEntry(fullPath);
        if (entry == null) return;

        InsertSorted(entry);

        // Load native icon for new row, like the others.
        IconService.QueueLoad(new[] { entry }, virtualEntries: false, _requestId, () => _requestId, Dispatcher);
    }

    private void WatcherRemove(string fullPath)
    {
        if (UpdatesBlocked) return;

        var existing = Items.FirstOrDefault(x =>
            string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null && existing.Name != "..") Items.Remove(existing);
    }

    private void WatcherUpdate(string fullPath)
    {
        if (UpdatesBlocked) return;
        if (!BelongsToCurrentDir(fullPath)) return;

        var existing = Items.FirstOrDefault(x =>
            string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing == null) { WatcherAddOrUpdate(fullPath); return; }

        try
        {
            if (!existing.IsFolder && File.Exists(fullPath))
            {
                existing.Size = new FileInfo(fullPath).Length;

                // The Modified column cannot be refreshed here: FileEntry.Modified is
                // declared init-only, so it is settable only in an object initializer.
                // To make the timestamp follow LastWrite events, turn it into a normal
                // notifying property in FileEntry (the same shape as Size) and add:
                //     existing.Modified = new FileInfo(fullPath).LastWriteTime;
                // Until then the date stays as of the last full reload of the pane.
            }
        }
        catch { }
    }

    // =========================================================================
    // Binary search for the insertion point using the pane's own comparer.
    //
    // The previous version copied the whole list, appended, re-sorted and then
    // called IndexOf — O(n log n) plus two O(n) passes for every single event.
    // Extracting an archive into the watched folder made that O(n² log n).
    // =========================================================================
    private void InsertSorted(FileEntry entry)
    {
        var comparer = new FileEntryComparer(SortColumn, SortAscending);

        // ".." always occupies index 0 and must never move
        int lo = (Items.Count > 0 && Items[0].Name == "..") ? 1 : 0;
        int hi = Items.Count;

        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (comparer.Compare(Items[mid], entry) <= 0) lo = mid + 1;
            else hi = mid;
        }

        Items.Insert(lo, entry);
    }

    // Builds FileEntry for real local path (file or folder).
    private static FileEntry? BuildLocalEntry(string fullPath)
    {
        try
        {
            bool isDir = Directory.Exists(fullPath);
            bool isFile = File.Exists(fullPath);
            if (!isDir && !isFile) return null;

            FileSystemInfo info = isDir ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
            var attrs = info.Attributes;
            bool hidden = (attrs & FileAttributes.Hidden) != 0 || (attrs & FileAttributes.System) != 0;
            bool symlink = (attrs & FileAttributes.ReparsePoint) != 0;
            long size = isDir ? 0 : ((FileInfo)info).Length;

            return new FileEntry
            {
                Name = info.Name,
                FullPath = fullPath,
                IsFolder = isDir,
                Size = size,
                Modified = info.LastWriteTime,
                IsHidden = hidden,
                IsSymlink = symlink
            };
        }
        catch { return null; }
    }
}
