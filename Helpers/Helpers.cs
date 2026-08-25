using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace R2Cmd;

public static class Helpers
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

    public static string FormatSize(long bytes)
    {
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < SizeUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {SizeUnits[unit]}";
    }

    public static string LastSegment(string key)
    {
        string trimmed = key.TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    // Updates OS title bar dark/light mode and caption color from the active theme.
    // useSurfaceColor = true  → match status bar (Editor / Viewer)
    // useSurfaceColor = false → match main window background
    public static void SetTitleBarTheme(Window window, bool isDark, bool useSurfaceColor = false)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDarkMode = isDark ? 1 : 0;
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
            const int DWMWA_BORDER_COLOR = 34;
            const int DWMWA_CAPTION_COLOR = 35;

            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDarkMode, sizeof(int));
            }

            if (TryGetThemeCaptionColor(out int caption, useSurfaceColor))
            {
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref caption, sizeof(int));
            }
        }
        catch
        {
            // Older OS: attributes unsupported — ignore
        }
    }

    // Reads theme color → Win32 COLORREF (0x00BBGGRR)
    private static bool TryGetThemeCaptionColor(out int colorRef, bool useSurfaceColor = false)
    {
        colorRef = 0;
        try
        {
            Color color;
            string colorKey = useSurfaceColor ? "Color.Surface" : "Color.Background";
            string brushKey = useSurfaceColor ? "Brush.Surface" : "Brush.Background";

            if (Application.Current?.TryFindResource(colorKey) is Color c)
                color = c;
            else if (Application.Current?.TryFindResource(brushKey) is SolidColorBrush brush)
                color = brush.Color;
            else
                return false;

            colorRef = (color.B << 16) | (color.G << 8) | color.R;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Retained for backward compatibility
    public static void ApplyDarkTitleBar(Window window) => SetTitleBarTheme(window, isDark: true);

    // ===================== Win32 API for forced window activation =====================

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// Forcefully activates window, bypassing Windows restrictions on SetForegroundWindow.
    /// Uses AttachThreadInput trick to steal focus from other apps.
    /// </summary>
    public static void ForceActivate(Window? window)
    {
        if (window == null) return;
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;

            if (window.WindowState == WindowState.Minimized)
            {
                ShowWindow(hwnd, SW_RESTORE);
            }

            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            uint currentThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);

            if (foregroundThread != currentThread && foregroundThread != 0)
            {
                AttachThreadInput(foregroundThread, currentThread, true);
                SetForegroundWindow(hwnd);
                AttachThreadInput(foregroundThread, currentThread, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }

            window.Activate();
        }
        catch { }
    }
}
