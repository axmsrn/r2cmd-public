using System.IO;
using System.Text.Json;

namespace R2Cmd;

public sealed class FavoriteEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class AppSettings
{
    // ==========================================
    // INSTANCE CACHE (Singleton)
    // ==========================================
    private static AppSettings? _instance;

    public List<FavoriteEntry> Favorites { get; set; } = new();

    // List of saved SSH connections
    public List<SshSession> SshSessions { get; set; } = new();

    public bool IsDarkTheme { get; set; } = true;

    public string LastLeftPath { get; set; } = "";
    public string LastRightPath { get; set; } = "";
    public string LastActivePane { get; set; } = "Left";

    public string LastLeftSortColumn { get; set; } = "Name";
    public bool LastLeftSortAscending { get; set; } = true;
    public string LastRightSortColumn { get; set; } = "Name";
    public bool LastRightSortAscending { get; set; } = true;

    // Column widths of each pane, in the order the columns are declared.
    // Empty until the first shutdown: a fresh install lets the pane size the
    // Name column to fit the window once, and after that the user's own layout
    // is what comes back.
    public List<double> LeftColumnWidths { get; set; } = new();
    public List<double> RightColumnWidths { get; set; } = new();

    public bool UseSystemIcons { get; set; } = true;

    // Minutes an SSH session is kept alive after the last pane leaves it, so
    // stepping over to a local drive and back does not re-authenticate. A
    // session still open in a pane is never closed regardless of this value.
    public int SshIdleMinutes { get; set; } = 5;

    // ==========================================
    // UI SCALE (Ctrl+Plus / Ctrl+Minus / Ctrl+0)
    // ==========================================
    // 1.0 is 100%. Settings files written before this property existed simply
    // keep the initializer value, so an upgrade lands on 100% as expected.
    public double UiZoom { get; set; } = 1.0;

    // ==========================================
    // WINDOW PLACEMENT
    // ==========================================
    // All four are 0 until the window is closed for the first time, which is the
    // signal to fall back to whatever MainWindow.xaml declares.
    // The rectangle is always the NORMAL state one: when the window is closed
    // maximized, RestoreBounds is stored here and WindowMaximized carries the state.
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // Saved path to custom editor via F3/F4
    public string CustomEditorPath { get; set; } = "";

    // List of the last 5 search masks
    public List<string> SearchHistory { get; set; } = new();

    // List of the last 10 searched words in file contents
    public List<string> SearchContainsHistory { get; set; } = new();

    // ==========================================
    // EMBEDDED TERMINAL (Ctrl+~)
    // ==========================================

    // Empty means auto detect: pwsh, then powershell, then cmd
    public string TerminalShellPath { get; set; } = "";

    // Width of the terminal column in device independent pixels.
    // Only used when the terminal shares the pane with the file list.
    public double TerminalWidth { get; set; } = 420;

    // Commands executed in the embedded terminal, oldest first (F9 shows them)
    public List<string> TerminalHistory { get; set; } = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "R2Cmd", "settings.json");

    public static AppSettings Load()
    {
        // If the settings were already loaded, hand out the very same instance so
        // MainWindow, SearchWindow and the panes all share one set of values.
        if (_instance != null)
            return _instance;

        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                _instance = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                _instance = new AppSettings();
            }
        }
        catch
        {
            _instance = new AppSettings();
        }

        // A corrupted or hand edited file could carry 0 here, which would collapse
        // the whole interface on startup
        if (_instance.UiZoom <= 0.1 || _instance.UiZoom > 5.0) _instance.UiZoom = 1.0;

        return _instance;
    }

    public void Save()
    {
        string? dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);

        // Copy+Delete had a window where a crash mid-copy could leave settings.json
        // partially written. File.Move(overwrite) on the same volume is a single
        // atomic rename at the filesystem level: either the old file or the new
        // one, never a half-written mix - and it's cheaper than a full copy too.
        File.Move(tmp, FilePath, overwrite: true);
    }
}
