using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace R2Cmd;

// =============================================================================
//  Folder sizes that have already been counted, keyed by full path.
//
// The result used to live only in the FileEntry it was written to, and a pane
// replaces every entry when it reloads a directory. Walking away from a folder
// while the count was running therefore threw the work away: coming back showed
// empty sizes again, even though the scan had finished minutes ago.
//
// Works for local paths and ssh:// alike, since both are just strings here.
// =============================================================================
public static class FolderSizeCache
{
    private static readonly ConcurrentDictionary<string, long> s_sizes =
        new(StringComparer.OrdinalIgnoreCase);

    // Paths currently being counted, so two panes or a repeated hotkey do not
    // scan the same tree twice at the same time
    private static readonly ConcurrentDictionary<string, byte> s_inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string path, out long size) => s_sizes.TryGetValue(path, out size);

    public static void Set(string path, long size) => s_sizes[path] = size;

    public static bool IsRunning(string path) => s_inFlight.ContainsKey(path);

    /// <summary>Returns false when the path is already being counted.</summary>
    public static bool TryBegin(string path) => s_inFlight.TryAdd(path, 0);

    public static void End(string path) => s_inFlight.TryRemove(path, out _);

    // =========================================================================
    // Drops the path, everything under it, and every ancestor.
    //
    // Ancestors matter: a cached total for C:\projects included the subfolder
    // that just got deleted, so leaving it in place would keep reporting a size
    // that no longer exists anywhere on disk.
    // =========================================================================
    public static void Invalidate(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        string target = path.TrimEnd('\\', '/');
        if (target.Length == 0) return;

        foreach (var key in s_sizes.Keys)
        {
            string candidate = key.TrimEnd('\\', '/');

            bool isSelf = candidate.Equals(target, StringComparison.OrdinalIgnoreCase);
            bool isDescendant = IsUnder(candidate, target);
            bool isAncestor = IsUnder(target, candidate);

            if (isSelf || isDescendant || isAncestor) s_sizes.TryRemove(key, out _);
        }
    }

    private static bool IsUnder(string candidate, string root)
    {
        if (candidate.Length <= root.Length) return false;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

        char separator = candidate[root.Length];
        return separator == '\\' || separator == '/';
    }

    // Drops everything under a path prefix. Used when an SSH session is closed:
    // sizes counted on a server nobody is connected to any more are worthless,
    // and the tree behind them may well have changed by the next visit.
    public static void InvalidatePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;

        foreach (var key in s_sizes.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                s_sizes.TryRemove(key, out _);
        }
    }

    public static void Clear()
    {
        s_sizes.Clear();
        s_inFlight.Clear();
    }
}
