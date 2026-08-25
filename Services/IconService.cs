using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace R2Cmd;

public static class IconService
{
    public static bool Enabled = true;

    private static readonly ConcurrentDictionary<string, ImageSource?> s_extCache =
        new(StringComparer.OrdinalIgnoreCase);

    private const string NoExtKey = "<none>";

    // A plain array, matched as a span. The HashSet lookup needed a real string,
    // which meant Path.GetExtension allocating one per file per listing — on the
    // UI thread, inside TryApplyCachedIcon.
    private static readonly string[] s_perFileExtensions =
        { ".exe", ".scr", ".lnk", ".ico", ".cur", ".ani", ".msc", ".cpl" };

    private static bool NeedsPerFileLookup(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0) return false;

        var ext = name.AsSpan(dot);

        foreach (string candidate in s_perFileExtensions)
        {
            if (ext.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string ExtensionKey(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? NoExtKey : name.Substring(dot);
    }

    // A value type key: the previous string form allocated one interpolated
    // string per file per listing, thousands of them on a large folder, and all
    // of it on the UI thread inside TryApplyCachedIcon.
    private readonly record struct IconKey(string Name, long Ticks, long Size);

    private static readonly ConcurrentDictionary<IconKey, ImageSource?> s_fileCache = new();

    // Insertion order, used to evict the oldest entries instead of wiping the
    // whole cache when it fills up
    private static readonly ConcurrentQueue<IconKey> s_fileOrder = new();

    // Tracked separately: ConcurrentDictionary.Count takes every lock in the
    // table, and it was being read on each single icon that got cached
    private static int s_fileCacheCount;

    private const int FileCacheLimit = 4096;

    private const int BatchSize = 128;

    // Delays before asking the shell again about files it had no icon for.
    //
    // A file written a moment ago often has no icon yet: the icon handler has not
    // been loaded, and on a large executable Defender is still scanning it — the
    // shell simply waits. Explorer and Total Commander show the same lag, so the
    // answer is to ask again rather than to try to outsmart the scanner.
    //
    // The last delay is deliberately generous: a self-extracting archive of
    // several hundred megabytes takes seconds to clear.
    private static readonly int[] RetryDelaysMs = { 1200, 3500, 8000 };

    // =========================================================================
    // ONE QUEUE, ONE CONSUMER
    //
    // QueueLoad used to start a thread per call. That is fine for navigation —
    // two calls, one per pane — but the directory watcher calls it once per
    // created file, so moving a folder with hundreds of files spawned hundreds
    // of threads at once. Under that thrashing the shell calls stopped
    // producing anything, which is why moved executables kept the placeholder
    // icon while a restart, with only two calls, resolved them fine.
    //
    // Work now goes through a queue drained by a single ThreadPool task. The
    // pool is MTA, which is what this code always used and what SHGetFileInfo
    // handles: an STA worker would have to pump messages, and one that simply
    // blocks on a queue can deadlock in-process shell handlers.
    // =========================================================================
    private sealed class IconJob
    {
        public required List<FileEntry> Items { get; init; }
        public required bool VirtualEntries { get; init; }
        public required int RequestId { get; init; }
        public required Func<int> CurrentRequestId { get; init; }
        public required Dispatcher Dispatcher { get; init; }
        public required int Attempt { get; init; }
    }

    private static readonly ConcurrentQueue<IconJob> s_queue = new();

    // Two consumers, not one. A single one was enough to stop the thread storm,
    // but SHGetFileInfo on a large executable that Defender is scanning blocks
    // for seconds, and everything queued behind it waited too. Two keeps one slow
    // file from stalling the rest while staying nowhere near a storm.
    private const int MaxWorkers = 2;
    private static int s_workers;

    public static void QueueLoad(
        IReadOnlyList<FileEntry> items,
        bool virtualEntries,
        int requestId,
        Func<int> currentRequestId,
        Dispatcher dispatcher)
    {
        if (!Enabled || items.Count == 0) return;

        // =====================================================================
        // FIX: ICON FLICKER ON REFRESH
        // Anything already in the cache is applied right here, synchronously,
        // before the list is rendered. Going through a worker + BeginInvoke for
        // known icons meant every refresh drew a frame with no icons at all,
        // which looked like the whole pane blinking - very visible while a copy
        // keeps triggering refreshes of the destination pane.
        // =====================================================================
        List<FileEntry>? uncached = null;

        foreach (var entry in items)
        {
            if (!TryApplyCachedIcon(entry, virtualEntries))
                (uncached ??= new List<FileEntry>()).Add(entry);
        }

        if (uncached == null) return;

        Enqueue(new IconJob
        {
            Items = uncached,
            VirtualEntries = virtualEntries,
            RequestId = requestId,
            CurrentRequestId = currentRequestId,
            Dispatcher = dispatcher,
            Attempt = 0
        });
    }

    private static void Enqueue(IconJob job)
    {
        s_queue.Enqueue(job);
        TryStartWorker();
    }

    private static void TryStartWorker()
    {
        while (true)
        {
            int running = Volatile.Read(ref s_workers);
            if (running >= MaxWorkers) return;

            if (Interlocked.CompareExchange(ref s_workers, running + 1, running) == running)
            {
                Task.Run(Drain);
                return;
            }
        }
    }

    private static void Drain()
    {
        try
        {
            while (s_queue.TryDequeue(out var job)) Process(job);
        }
        finally
        {
            Interlocked.Decrement(ref s_workers);

            // A job enqueued between the last dequeue and the decrement would
            // otherwise sit there with nobody to pick it up
            if (!s_queue.IsEmpty) TryStartWorker();
        }
    }

    private static void Process(IconJob job)
    {
        var pending = new List<KeyValuePair<FileEntry, ImageSource>>(BatchSize);
        List<FileEntry>? missed = null;

        foreach (var entry in job.Items)
        {
            if (job.CurrentRequestId() != job.RequestId) return;

            ImageSource? icon = Resolve(entry, job.VirtualEntries);

            if (icon == null)
            {
                // Only per-file icons are worth asking about again; an extension
                // with no icon will not grow one in a second and a half
                if (job.Attempt < RetryDelaysMs.Length && NeedsPerFileIcon(entry, job.VirtualEntries))
                    (missed ??= new List<FileEntry>()).Add(entry);

                continue;
            }

            pending.Add(new(entry, icon));
            if (pending.Count >= BatchSize)
                Flush(pending, job);
        }

        if (pending.Count > 0)
            Flush(pending, job);

        if (missed != null) ScheduleRetry(job, missed);
    }

    // The shell is asked once more a moment later. Without this a file that was
    // just written keeps the placeholder until something else causes a reload.
    private static void ScheduleRetry(IconJob job, List<FileEntry> missed)
    {
        var retry = new IconJob
        {
            Items = missed,
            VirtualEntries = job.VirtualEntries,
            RequestId = job.RequestId,
            CurrentRequestId = job.CurrentRequestId,
            Dispatcher = job.Dispatcher,
            Attempt = job.Attempt + 1
        };

        int delay = RetryDelaysMs[job.Attempt];

        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            if (retry.CurrentRequestId() == retry.RequestId) Enqueue(retry);
        }, null, delay, Timeout.Infinite);
    }

    private static bool NeedsPerFileIcon(FileEntry entry, bool virtualEntries)
    {
        if (virtualEntries || entry.IsFolder || entry.IsArchive) return false;
        return NeedsPerFileLookup(entry.Name);
    }

    // =========================================================================
    // Per-file icons are keyed by name, size and timestamp — deliberately NOT by
    // path.
    //
    // A move changes only the path: name, size and stamp survive it, so the icon
    // loaded while the file was still in the source folder is a hit the moment it
    // appears in the destination. Keying by path instead threw that away and left
    // the user watching the shell load the vendor's icon handler from scratch.
    //
    // The stamp keeps the key honest in the other direction: drop a different
    // build over an old file and the size or the timestamp differs, so it misses
    // the cache rather than showing the previous icon. A collision needs two
    // files with the same name, the same byte count and the same timestamp to the
    // tick — at which point they are the same file anyway.
    // =========================================================================
    private static IconKey FileIdentityKey(FileEntry entry) =>
        new(entry.Name, entry.Modified?.Ticks ?? 0, entry.Size);

    // Returns true when the entry needs no background work: it already has an
    // icon, it is drawn by XAML as a vector, or the cache already has an answer.
    // Pure dictionary lookups, no shell calls, so this is safe on the UI thread.
    private static bool TryApplyCachedIcon(FileEntry entry, bool virtualEntries)
    {
        if (entry.Icon != null) return true;

        if (entry.Name == "..") return true;
        if (!string.IsNullOrEmpty(entry.IconType) && entry.IconType != "Default") return true;
        if (entry.IsFolder || entry.IsArchive) return true;

        if (!virtualEntries && NeedsPerFileLookup(entry.Name))
        {
            if (s_fileCache.TryGetValue(FileIdentityKey(entry), out var byFile) && byFile != null)
            {
                entry.Icon = byFile;
                return true;
            }
            return false;
        }

        if (s_extCache.TryGetValue(ExtensionKey(entry.Name), out var byExt))
        {
            if (byExt != null) entry.Icon = byExt;
            return true;
        }

        return false;
    }

    private static void Flush(List<KeyValuePair<FileEntry, ImageSource>> batch, IconJob job)
    {
        var snapshot = batch.ToArray();
        batch.Clear();

        job.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (job.CurrentRequestId() != job.RequestId) return;

            foreach (var pair in snapshot)
                pair.Key.Icon = pair.Value;
        }), DispatcherPriority.Background);
    }

    private static ImageSource? Resolve(FileEntry entry, bool virtualEntries)
    {
        // 1. Custom elements and navigation (they don't need Windows icons)
        if (entry.Name == "..")
            return null;

        if (!string.IsNullOrEmpty(entry.IconType) && entry.IconType != "Default")
            return null;

        // 2. Folders and archives are always vector in XAML. Asking Windows for
        // their icons would waste both time and memory.
        if (entry.IsFolder || entry.IsArchive)
            return null;

        // 3. Regular files
        //
        // No File.Exists guard: the entry came out of a directory listing, so the
        // check was an extra round trip per executable — painful on a network
        // share — and SHGetFileInfo answers with zero for a file that has gone.
        if (!virtualEntries && NeedsPerFileLookup(entry.Name))
        {
            // A miss here means the shell was not ready, not that the file has
            // no icon. Remembering it would freeze the placeholder in place for
            // the rest of the session.
            return GetOrAddFileIcon(FileIdentityKey(entry), () => LoadFromPath(entry.FullPath));
        }

        string key = ExtensionKey(entry.Name);

        // By extension the answer is deterministic — SHGFI_USEFILEATTRIBUTES does
        // not touch the file at all — so a miss is worth remembering
        if (s_extCache.TryGetValue(key, out var cachedByExt)) return cachedByExt;

        ImageSource? byExtension = LoadByAttributes(entry.Name, directory: false);
        s_extCache[key] = byExtension;
        return byExtension;
    }

    private static ImageSource? GetOrAddFileIcon(IconKey key, Func<ImageSource?> loader)
    {
        if (s_fileCache.TryGetValue(key, out var cached)) return cached;

        ImageSource? icon = loader();
        if (icon == null) return null;      // failures are never remembered

        if (s_fileCache.TryAdd(key, icon))
        {
            s_fileOrder.Enqueue(key);

            if (Interlocked.Increment(ref s_fileCacheCount) > FileCacheLimit) TrimFileCache();
        }

        return icon;
    }

    // =========================================================================
    // Evicts the oldest quarter rather than clearing everything.
    //
    // A full wipe at the limit dropped the icons of the folder currently on
    // screen along with the rest, so crossing the threshold showed up as every
    // visible icon reloading at once — the exact stutter this cache exists to
    // avoid.
    // =========================================================================
    private static void TrimFileCache()
    {
        int toEvict = FileCacheLimit / 4;

        for (int i = 0; i < toEvict && s_fileOrder.TryDequeue(out var old); i++)
        {
            if (s_fileCache.TryRemove(old, out _)) Interlocked.Decrement(ref s_fileCacheCount);
        }
    }

    /// <summary>Drops every cached icon, so the next listing asks the shell again.</summary>
    public static void Clear()
    {
        s_extCache.Clear();
        s_fileCache.Clear();

        while (s_fileOrder.TryDequeue(out _)) { }

        Volatile.Write(ref s_fileCacheCount, 0);
    }

    // --- Win32 ---------------------------------------------------------------

    // Marshal.SizeOf walks the layout every time it is called, and it was called
    // once per icon
    private static readonly uint s_shfiSize = (uint)Marshal.SizeOf<SHFILEINFO>();

    private static ImageSource? LoadFromPath(string path)
    {
        var shfi = new SHFILEINFO();
        IntPtr res = SHGetFileInfo(path, 0, ref shfi, s_shfiSize, SHGFI_ICON | SHGFI_LARGEICON);
        return FromShfi(res, ref shfi);
    }

    private static ImageSource? LoadByAttributes(string name, bool directory)
    {
        uint attr = directory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        var shfi = new SHFILEINFO();
        IntPtr res = SHGetFileInfo(name, attr, ref shfi, s_shfiSize,
            SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        return FromShfi(res, ref shfi);
    }

    private static ImageSource? FromShfi(IntPtr res, ref SHFILEINFO shfi)
    {
        if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
