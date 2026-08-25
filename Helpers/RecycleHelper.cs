using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace R2Cmd;

// Batch delete to the Recycle Bin with a single shell call (SHFileOperation), like in
// Total Commander. Single-item FileSystem.DeleteFile on 1000+ files creates as many
// separate shell transactions (writing recovery metadata for each file) — hence taking
// dozens of seconds. A single batch call is exponentially faster.
public static class RecycleHelper
{
    // Sends all provided paths to the Recycle Bin in a single call.
    // Returns true on success (no errors and no user cancellation).
    public static bool SendToRecycleBin(IEnumerable<string> paths, bool silent)
    {
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (list.Count == 0) return true;

        // pFrom - double-null-terminated list of paths: "a\0b\0c\0\0".
        string from = string.Join("\0", list) + "\0\0";

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI
                              | (silent ? FOF_SILENT : 0))
        };

        int result = SHFileOperation(ref op);
        // 0 = success. fAnyOperationsAborted = cancelled by the user.
        return result == 0 && !op.fAnyOperationsAborted;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;   // this specifically means "to Recycle Bin", not permanently
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPTStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPTStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
