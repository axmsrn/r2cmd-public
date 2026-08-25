using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace R2Cmd;

// =============================================================================
// UI SCALING
//
// Every metric lives in App.xaml and is consumed through DynamicResource, so
// changing the value inside Application.Current.Resources is enough: panes,
// breadcrumbs and rows re-measure themselves. Nothing has to be reloaded.
//
// Ctrl+Plus / Ctrl+Minus step through the levels, Ctrl+0 returns to 100%,
// Ctrl+MouseWheel does the same as the keys.
// =============================================================================
public static class ZoomManager
{
    // Resource keys taken from App.xaml
    private const string KeyFontSize = "AppFontSize";
    private const string KeyRowHeight = "AppRowHeight";
    private const string KeyPaneFontSize = "AppFilePaneFontSize";
    private const string KeyPaneRowHeight = "AppFilePaneRowHeight";
    private const string KeyFileIconSize = "AppFileIconSize";
    private const string KeyFolderIconSize = "AppFolderIconSize";

    // Fine near 100% where it is used most, coarser at the extremes
    private static readonly double[] s_levels =
    {
        0.80, 0.85, 0.90, 0.95, 1.00, 1.10, 1.15, 1.20, 1.25, 1.30, 1.35, 1.40
    };

    private static readonly Dictionary<string, double> s_baseValues = new();
    private static bool s_baseCaptured;

    private static Action<string?>? s_report;
    private static DispatcherTimer? s_hideTimer;

    public static double Zoom { get; private set; } = 1.0;

    /// <summary>Raised after every applied change, so the caller can persist the value.</summary>
    public static event Action<double>? ZoomChanged;

    // =========================================================================
    // report(text)  — show this line in the status bar
    // report(null)  — the zoom indicator has expired, restore the usual text
    // =========================================================================
    public static void Attach(Window window, Action<string?> report)
    {
        CaptureBaseValues();
        s_report = report;

        window.PreviewKeyDown += OnPreviewKeyDown;
        window.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    /// <summary>Restores a saved zoom level, e.g. from AppSettings on startup.</summary>
    public static void SetZoom(double zoom, bool silent = false)
    {
        CaptureBaseValues();

        Zoom = Clamp(zoom);
        ApplyToResources();

        if (!silent) ShowIndicator();
        ZoomChanged?.Invoke(Zoom);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            // OemPlus / OemMinus are the main row, Add / Subtract are the numpad
            case Key.OemPlus:
            case Key.Add:
                Step(+1);
                e.Handled = true;
                break;

            case Key.OemMinus:
            case Key.Subtract:
                Step(-1);
                e.Handled = true;
                break;

            case Key.D0:
            case Key.NumPad0:
                SetZoom(1.0);
                e.Handled = true;
                break;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Delta == 0) return;

        Step(e.Delta > 0 ? +1 : -1);
        e.Handled = true;   // otherwise the pane scrolls at the same time
    }

    private static void Step(int direction)
    {
        int index = NearestLevelIndex(Zoom) + direction;
        if (index < 0 || index >= s_levels.Length)
        {
            // Already at the end of the range: still flash the current value so
            // the key press does not feel dead
            ShowIndicator();
            return;
        }

        SetZoom(s_levels[index]);
    }

    private static int NearestLevelIndex(double zoom)
    {
        int best = 0;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < s_levels.Length; i++)
        {
            double distance = Math.Abs(s_levels[i] - zoom);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }

        return best;
    }

    private static double Clamp(double zoom) =>
        Math.Min(s_levels[s_levels.Length - 1], Math.Max(s_levels[0], zoom));

    // Read the untouched App.xaml values exactly once. Reading them later would
    // compound the scaling, because by then they hold zoomed numbers already.
    private static void CaptureBaseValues()
    {
        if (s_baseCaptured) return;

        var res = Application.Current?.Resources;
        if (res == null) return;

        foreach (string key in new[]
        {
            KeyFontSize, KeyRowHeight, KeyPaneFontSize,
            KeyPaneRowHeight, KeyFileIconSize, KeyFolderIconSize
        })
        {
            if (res[key] is double value) s_baseValues[key] = value;
        }

        s_baseCaptured = s_baseValues.Count > 0;
    }

    private static void ApplyToResources()
    {
        var res = Application.Current?.Resources;
        if (res == null || !s_baseCaptured) return;

        // Whole pixels only: fractional icon sizes and row heights produce blurry
        // edges, since the icons are pixel-snapped
        double paneFont = Math.Max(8, Math.Round(Base(KeyPaneFontSize) * Zoom));
        double fileIcon = Math.Max(12, Math.Round(Base(KeyFileIconSize) * Zoom));
        double folderIcon = Math.Max(12, Math.Round(Base(KeyFolderIconSize) * Zoom));
        double font = Math.Max(8, Math.Round(Base(KeyFontSize) * Zoom));

        // =====================================================================
        // ROW HEIGHT — the part that decides whether the listing stays compact.
        //
        // The proportional value alone is not trustworthy: rounding two numbers
        // independently can leave the row several pixels taller than its own
        // content. So the scaled height is used only as a starting point, and
        // what actually matters is the floor below it — the icon plus the same
        // 2px gap the layout has today (icon 22 / row 24), and enough room for
        // the text line. The row never grows past what it needs to hold.
        // =====================================================================
        double iconFloor = Math.Max(fileIcon, folderIcon) + 2;
        double textFloor = Math.Ceiling(paneFont * 1.35);

        double paneRow = Math.Max(Math.Round(Base(KeyPaneRowHeight) * Zoom),
                                  Math.Max(iconFloor, textFloor));

        double row = Math.Max(Math.Round(Base(KeyRowHeight) * Zoom),
                              Math.Ceiling(font * 1.35));

        res[KeyFontSize] = font;
        res[KeyRowHeight] = row;
        res[KeyPaneFontSize] = paneFont;
        res[KeyPaneRowHeight] = paneRow;
        res[KeyFileIconSize] = fileIcon;
        res[KeyFolderIconSize] = folderIcon;
    }

    private static double Base(string key) =>
        s_baseValues.TryGetValue(key, out double value) ? value : 0;

    // =========================================================================
    // The indicator is temporary: it appears while the user is stepping through
    // the levels and gives the status bar back afterwards.
    // =========================================================================
    private static void ShowIndicator()
    {
        s_report?.Invoke($"Zoom: {Math.Round(Zoom * 100)}%");

        s_hideTimer ??= CreateHideTimer();
        s_hideTimer.Stop();
        s_hideTimer.Start();
    }

    private static DispatcherTimer CreateHideTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };

        timer.Tick += (s, e) =>
        {
            timer.Stop();
            s_report?.Invoke(null);
        };

        return timer;
    }
}
