using System;
using System.ComponentModel;
using System.Windows.Media;

namespace R2Cmd;

public sealed class FileEntry : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsFolder { get; set; }

    // New property for custom vector icons
    public string IconType { get; set; } = "Default";
    public DateTime? Modified { get; init; }

    // Flag for hidden or system files
    public bool IsHidden { get; init; }

    // Flag for symbolic links (shortcuts)
    public bool IsSymlink { get; init; }

    // Lowers opacity to make the font look darker for hidden files
    public double ItemOpacity => IsHidden ? 0.5 : 1.0;

    // Fast check if the file is an archive (cached after first call to speed up XAML scrolling)
    private bool? _isArchive;
    public bool IsArchive => _isArchive ??= ArchiveService.IsArchiveFile(Name);

    private long _size;
    public long Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeDisplay)));
        }
    }

    private bool _sizeKnown;
    public bool SizeKnown
    {
        get => _sizeKnown;
        set
        {
            if (_sizeKnown == value) return;
            _sizeKnown = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeKnown)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeDisplay)));
        }
    }

    private bool _sizeCalculating;
    public bool SizeCalculating
    {
        get => _sizeCalculating;
        set
        {
            if (_sizeCalculating == value) return;
            _sizeCalculating = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeCalculating)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeDisplay)));
        }
    }

    public string SizeDisplay => IsFolder
        ? (SizeCalculating ? "…" : SizeKnown ? Helpers.FormatSize(Size) : "<DIR>")
        : Helpers.FormatSize(Size);

    public string ModifiedDisplay => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";

    /// <summary>
    /// Directory part of FullPath (used when showing path of current file in search results).
    /// </summary>
    public string DirectoryDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(FullPath) || Name == "..") return "";

            try
            {
                // SSH paths use forward slash
                if (FullPath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
                {
                    int last = FullPath.LastIndexOf('/');
                    return last > 0 ? FullPath.Substring(0, last) : FullPath;
                }

                return System.IO.Path.GetDirectoryName(FullPath) ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    // Marking a file with Insert key for copy/move/delete.
    // Lives INDEPENDENTLY of WPF system selection (SelectedItems), so
    // moving cursor with arrows in Extended mode doesn't clear it.
    private bool _isMarked;
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_isMarked == value) return;
            _isMarked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMarked)));
        }
    }

    // Native Windows icon. Arrives asynchronously after row display,
    // so it's a notify property, not init: binding must update.
    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    // Inline rename mode: when true, row shows input field instead of label.
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
