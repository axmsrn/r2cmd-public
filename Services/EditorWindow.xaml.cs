using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace R2Cmd;

public partial class EditorWindow : Window
{
    private readonly string _path;
    private readonly string? _remotePath;
    private readonly string _displayName;

    private bool _dirty;
    private bool _suppressTextChanged;
    private string _encodingName = "UTF-8";
    private Encoding? _forcedEncoding;
    private int _encodingIndex;

    private static readonly string[] EncodingNames =
    {
        "Auto", "UTF-8", "UTF-8 BOM", "Windows-1251", "CP866", "UTF-16 LE", "UTF-16 BE"
    };

    private readonly List<int> _textMatches = new();
    private string _matchQuery = "";
    private int _matchIndex = -1;

    public EditorWindow(string localPath, string displayName, string? remotePath = null)
    {
        InitializeComponent();

        _path = localPath;
        _remotePath = remotePath;
        _displayName = displayName;
        Title = $"Edit: {displayName}";

        if (TryFindResource("Brush.Background") is Brush bg)
            Resources[SystemColors.ControlBrushKey] = bg;

        SourceInitialized += (_, _) => Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme, useSurfaceColor: true);
        ContentRendered += (_, _) => LoadFile();
    }

    private Encoding? GetEncodingByIndex(int index)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return index switch
            {
                0 => null,
                1 => new UTF8Encoding(false),
                2 => new UTF8Encoding(true),
                3 => Encoding.GetEncoding(1251),
                4 => Encoding.GetEncoding(866),
                5 => Encoding.Unicode,
                6 => Encoding.BigEndianUnicode,
                _ => null
            };
        }
        catch
        {
            return new UTF8Encoding(false);
        }
    }

    private void LoadFile()
    {
        try
        {
            Encoding enc = _forcedEncoding
                ?? TextSearcher.DetectFileEncoding(_path)
                ?? new UTF8Encoding(false);

            string text = File.ReadAllText(_path, enc);

            _encodingName = _forcedEncoding != null
                ? EncodingNames[_encodingIndex]
                : (enc.CodePage == 65001 ? "UTF-8" : enc.WebName);

            _suppressTextChanged = true;
            txtContent.Text = text;
            _suppressTextChanged = false;

            _dirty = false;
            Title = $"Edit: {_displayName}";

            string ext = Path.GetExtension(_path);
            var highlighting =
                HighlightingManager.Instance.GetDefinitionByExtension(ext);

            // AvalonEdit has no TypeScript mode — reuse JavaScript
            if (highlighting == null &&
          (ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
           ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase)))
            {
                highlighting = HighlightingManager.Instance.GetDefinition("JavaScript");
            }

            string fileName = Path.GetFileName(_path);
            if (highlighting == null &&
                fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            {
                highlighting = HighlightingManager.Instance.GetDefinition("JavaScript");
            }

            txtContent.SyntaxHighlighting = highlighting;

            // Line numbers only for code; plain text stays clean
            string extLower = ext.ToLowerInvariant();
            bool plainText = extLower is ".txt" or ".log" or ".csv" or ".tsv";
            txtContent.ShowLineNumbers = !plainText;

            Dispatcher.BeginInvoke(new Action(ApplyNoirTheme), DispatcherPriority.Loaded);

            UpdateStatus();
            txtStatusRight.Text = "";
            txtContent.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Cannot open file:\n" + ex.Message, "Editor");
            Close();
        }
    }

    private void ApplyNoirTheme()
    {
        var bg = (Brush)FindResource("Brush.Background");
        var fg = (Brush)FindResource("Brush.TextPrimary");
        var lineNo = (Brush)FindResource("Brush.TextSecondary");
        var selection = (Brush)FindResource("Brush.Hover");

        txtContent.Background = bg;
        txtContent.Foreground = fg;
        txtContent.LineNumbersForeground = lineNo;

        txtContent.TextArea.TextView.Margin = new Thickness(6, 0, 0, 0);
        txtContent.Padding = new Thickness(4, 0, 0, 0);
        foreach (var margin in txtContent.TextArea.LeftMargins)
        {
            if (margin is FrameworkElement fe &&
                margin.GetType().Name == "LineNumberMargin")
            {
                fe.Margin = new Thickness(0, 0, 4, 0);
            }
        }

        txtContent.TextArea.Background = bg;
        txtContent.TextArea.Foreground = fg;
        txtContent.TextArea.SelectionBrush = selection;
        txtContent.TextArea.SelectionForeground = fg;
        txtContent.TextArea.SelectionBorder = null;

        txtContent.TextArea.TextView.LinkTextForegroundBrush = MakeBrush(0x8B, 0xE9, 0xFD);
        txtContent.TextArea.TextView.LinkTextBackgroundBrush = Brushes.Transparent;

        txtContent.Options.HighlightCurrentLine = true;
        txtContent.TextArea.TextView.CurrentLineBackground = (Brush)FindResource("Brush.Surface");
        txtContent.TextArea.TextView.CurrentLineBorder =
            new Pen((Brush)FindResource("Brush.Border"), 1);

        // Recolor shared definitions used inside HTML <script> / <style>
        // so keywords don't stay the default blue from the original XSHD
        if (HighlightingManager.Instance.GetDefinition("JavaScript") is { } jsDef)
            ApplyNoirColors(jsDef);
        if (HighlightingManager.Instance.GetDefinition("CSS") is { } cssDef)
            ApplyNoirColors(cssDef);

        var currentDef = txtContent.SyntaxHighlighting;
        if (currentDef != null)
        {
            var freshDef = LoadFreshDefinition(currentDef.Name);
            if (freshDef != null)
            {
                AddCssColorRules(freshDef);
                ApplyNoirColors(freshDef);
                txtContent.SyntaxHighlighting = freshDef;
            }
            else
            {
                ApplyNoirColors(currentDef);
                txtContent.SyntaxHighlighting = null;
                txtContent.SyntaxHighlighting = currentDef;
            }
        }

        txtContent.TextArea.TextView.Redraw();
        txtContent.TextArea.TextView.InvalidateVisual();

        // Keep the separator, only make it solid (not dotted)
        MakeLineNumberSeparatorSolid();
        Dispatcher.BeginInvoke(MakeLineNumberSeparatorSolid, DispatcherPriority.ApplicationIdle);
    }

    private void MakeLineNumberSeparatorSolid()
    {
        var stroke = (Brush)FindResource("Brush.Border");
        foreach (var margin in txtContent.TextArea.LeftMargins)
        {
            if (margin is System.Windows.Shapes.Line line)
            {
                line.StrokeDashArray = new DoubleCollection(); // solid
                line.Stroke = stroke;
                line.StrokeThickness = 1;
            }
        }
    }

    private static void AddCssColorRules(IHighlightingDefinition definition)
    {
        var purple = new SimpleHighlightingBrush(Color.FromRgb(0xBD, 0x93, 0xF9));
        var turquoise = new SimpleHighlightingBrush(Color.FromRgb(0x40, 0xE0, 0xD0));

        var hexColor = new HighlightingColor
        {
            Name = "NoirHexColor",
            Foreground = purple
        };

        var funcColor = new HighlightingColor
        {
            Name = "NoirCssColorFunc",
            Foreground = purple
        };

        var propColor = new HighlightingColor
        {
            Name = "NoirCssProperty",
            Foreground = turquoise
        };

        try
        {
            if (definition.NamedHighlightingColors is IList<HighlightingColor> list)
            {
                list.Add(hexColor);
                list.Add(funcColor);
                list.Add(propColor);
            }
        }
        catch { }

        var hexRule = new HighlightingRule
        {
            Regex = new Regex(
                @"#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})(?![0-9a-fA-F])",
                RegexOptions.Compiled),
            Color = hexColor
        };

        var funcRule = new HighlightingRule
        {
            Regex = new Regex(
                @"(?:rgba?|hsla?)\s*\([^)]*\)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            Color = funcColor
        };

        // property name before ':'  (background, padding, border-bottom, ...)
        var propRule = new HighlightingRule
        {
            Regex = new Regex(
                @"\b[a-zA-Z_-][a-zA-Z0-9_-]*\s*(?=:)",
                RegexOptions.Compiled),
            Color = propColor
        };

        void AddToRuleSet(HighlightingRuleSet? ruleSet)
        {
            if (ruleSet == null) return;
            ruleSet.Rules.Insert(0, hexRule);
            ruleSet.Rules.Insert(0, funcRule);
            ruleSet.Rules.Insert(0, propRule);
        }
        AddToRuleSet(definition.MainRuleSet);
    }

    private static IHighlightingDefinition? LoadFreshDefinition(string name)
    {
        try
        {
            var assembly = typeof(HighlightingManager).Assembly;

            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(r =>
                    r.EndsWith(".xshd", StringComparison.OrdinalIgnoreCase) &&
                    r.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);

            if (resourceName == null) return null;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            using var reader = XmlReader.Create(stream);
            var xshd = HighlightingLoader.LoadXshd(reader);
            return HighlightingLoader.Load(xshd, HighlightingManager.Instance);
        }
        catch
        {
            return null;
        }
    }

    private static bool ApplyNoirColors(IHighlightingDefinition definition)
    {
        bool changed = false;

        foreach (var color in definition.NamedHighlightingColors)
        {
            if (!color.IsFrozen)
            {
                SetNoirColor(color);
                changed = true;
            }
        }

        return changed;
    }

    private static void SetNoirColor(HighlightingColor color)
    {
        if (color.Name == null) return;

        string name = color.Name.ToLowerInvariant();

        // Keep colors we injected in AddCssColorRules
        if (name is "noirhexcolor" or "noircsscolorfunc" or "noircssproperty")
            return;

        var comment = Color.FromRgb(0x62, 0x72, 0xA4);
        var stringColor = Color.FromRgb(0xF1, 0xFA, 0x8C);
        var number = Color.FromRgb(0xBD, 0x93, 0xF9);
        var keyword = Color.FromRgb(0x8B, 0xE9, 0xFD); // cyan
        var type = Color.FromRgb(0x8B, 0xE9, 0xFD);
        var function = Color.FromRgb(0x50, 0xFA, 0x7B);
        var attribute = Color.FromRgb(0xFF, 0xB8, 0x6C);
        var plain = Color.FromRgb(0xF8, 0xF8, 0xF2);
        var tag = Color.FromRgb(0xFF, 0x79, 0xC6); // Dracula pink

        if (NameContains(name, "comment"))
            color.Foreground = new SimpleHighlightingBrush(comment);
        else if (NameContains(name, "string") || NameContains(name, "char") ||
                 NameContains(name, "value") || NameContains(name, "cssvalue") ||
                 NameContains(name, "cssstring") || NameContains(name, "htmlentity") ||
                 NameContains(name, "propertyvalue") || NameContains(name, "attributestring") ||
                 NameContains(name, "csspropertyvalue") || NameContains(name, "htmlattributevalue"))
            color.Foreground = new SimpleHighlightingBrush(stringColor);
        else if (NameContains(name, "number") || NameContains(name, "digit") ||
                 NameContains(name, "truefalse") || NameContains(name, "null") ||
                 NameContains(name, "constant") || NameContains(name, "boolean") ||
                 NameContains(name, "cssnumber") || NameContains(name, "csshexcolor") ||
                 NameContains(name, "hexcolor") || NameContains(name, "colorcode") ||
                 NameContains(name, "unit") || NameContains(name, "cssunit"))
            color.Foreground = new SimpleHighlightingBrush(number);
        else if (NameContains(name, "keyword") || NameContains(name, "modifier") ||
                 NameContains(name, "visibility") || NameContains(name, "operator") ||
                 NameContains(name, "assignment") || NameContains(name, "goto") ||
                 NameContains(name, "context") || NameContains(name, "exception") ||
                 NameContains(name, "getset") || NameContains(name, "thisorbase") ||
                 NameContains(name, "namespace") || NameContains(name, "storage") ||
                 NameContains(name, "javascript") || NameContains(name, "jscript") ||
                 name == "words" || name == "keywords")
            color.Foreground = new SimpleHighlightingBrush(keyword);
        else if (NameContains(name, "htmltag") || NameContains(name, "xmltag") ||
                 NameContains(name, "tagname") ||
                 (NameContains(name, "tag") && (NameContains(name, "html") || NameContains(name, "xml"))) ||
                 NameContains(name, "slash"))
            color.Foreground = new SimpleHighlightingBrush(tag);
        else if (NameContains(name, "type") || NameContains(name, "class") ||
                 NameContains(name, "valuetype") || NameContains(name, "referencetype") ||
                 NameContains(name, "struct") || NameContains(name, "interface") ||
                 NameContains(name, "enum") || NameContains(name, "delegate") ||
                 NameContains(name, "selector") || NameContains(name, "cssselector"))
            color.Foreground = new SimpleHighlightingBrush(type);
        else if (NameContains(name, "method") || NameContains(name, "function") ||
                 NameContains(name, "methodcall") || NameContains(name, "methodname") ||
                 NameContains(name, "property") || NameContains(name, "propertyname") ||
                 NameContains(name, "csspropertyname") || NameContains(name, "htmlattribute") ||
                 NameContains(name, "htmlattributename"))
            color.Foreground = new SimpleHighlightingBrush(function);
        else if (NameContains(name, "attribute") || NameContains(name, "decorator") ||
                 NameContains(name, "annotation") || NameContains(name, "fieldname") ||
                 NameContains(name, "cssatrule") || NameContains(name, "preprocessor") ||
                 NameContains(name, "directive") || NameContains(name, "doctype"))
            color.Foreground = new SimpleHighlightingBrush(attribute);
        else
            color.Foreground = new SimpleHighlightingBrush(plain);
    }

    private static bool NameContains(string name, string part) =>
        name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void TxtContent_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged) return;

        if (!_dirty)
        {
            _dirty = true;
            Title = $"Edit: {_displayName} *";
        }
        UpdateStatus();
    }

    private bool Save()
    {
        try
        {
            Encoding enc = _forcedEncoding
                ?? TextSearcher.DetectFileEncoding(_path)
                ?? new UTF8Encoding(false);

            if (_forcedEncoding != null)
                enc = _forcedEncoding;

            File.WriteAllText(_path, txtContent.Text, enc);

            if (!string.IsNullOrEmpty(_remotePath) &&
                _remotePath.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Providers.SshFileSystemProvider.UploadFromStream(
                    fs, _remotePath, System.Threading.CancellationToken.None, _ => { });
            }

            _dirty = false;
            Title = $"Edit: {_displayName}";
            UpdateStatus();

            txtStatusRight.Text = "•  Saved";
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            t.Tick += (_, _) => { txtStatusRight.Text = ""; t.Stop(); };
            t.Start();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Save failed:\n" + ex.Message, "Editor");
            return false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_dirty) return;

        var dialog = new ConfirmDialog(
            message: "Do you want to save the changes?",
            title: "Unsaved Changes",
            yesText: "Save",
            noText: "Don't Save",
            cancelText: "Cancel");

        dialog.Owner = this;
        dialog.ShowDialog();

        switch (dialog.Result)
        {
            case ConfirmDialog.ConfirmResult.Yes:
                if (!Save())
                    e.Cancel = true;
                break;
            case ConfirmDialog.ConfirmResult.No:
                break;
            case ConfirmDialog.ConfirmResult.Cancel:
                e.Cancel = true;
                break;
        }

        if (!e.Cancel)
        {
            if (Owner != null)
            {
                Owner.Activate();
            }
            else if (Application.Current.MainWindow != null && Application.Current.MainWindow != this)
            {
                Application.Current.MainWindow.Activate();
            }
        }
    }

    private void UpdateStatus()
    {
        txtMode.Text = "Edit";

        txtEncoding.Text = _forcedEncoding == null
            ? (string.IsNullOrEmpty(_encodingName) ? "Auto" : _encodingName)
            : EncodingNames[_encodingIndex];

        txtStatusLeft.Text = _displayName;
        // txtStatusRight is set on Save / left empty while editing
    }

    private void TxtMode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void TxtEncoding_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _encodingIndex = (_encodingIndex + 1) % EncodingNames.Length;
        _forcedEncoding = GetEncodingByIndex(_encodingIndex);

        if (!_dirty)
            LoadFile();
        else
            UpdateStatus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (ctrl && e.Key == Key.S)
        {
            e.Handled = true;
            Save();
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;

            if (!string.IsNullOrEmpty(txtFind.Text) || txtFind.IsFocused)
            {
                txtFind.Text = "";
                txtContent.Focus();
                return;
            }

            Close();
            return;
        }

        if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            txtFind.Focus();
            txtFind.SelectAll();
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        e.Handled = true;
        double size = txtContent.FontSize;
        size = e.Delta > 0 ? Math.Min(72, size + 2) : Math.Max(8, size - 2);
        txtContent.FontSize = size;
    }

    // ===== Search =====

    private void TxtFind_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ExecuteSearch(txtFind.Text, isNew: true);

    private void TxtFind_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        int dir = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
        ExecuteSearch(txtFind.Text, isNew: false, dir);
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e) =>
        ExecuteSearch(txtFind.Text, isNew: false, dir: -1);

    private void BtnNext_Click(object sender, RoutedEventArgs e) =>
        ExecuteSearch(txtFind.Text, isNew: false, dir: 1);

    private void ExecuteSearch(string needle, bool isNew, int dir = 1)
    {
        if (string.IsNullOrEmpty(needle))
        {
            _textMatches.Clear();
            _matchQuery = "";
            _matchIndex = -1;
            txtMatches.Text = "";
            return;
        }

        if (isNew || _matchQuery != needle)
        {
            _matchQuery = needle;
            _textMatches.Clear();

            string text = txtContent.Text;
            int from = 0;
            while (from <= text.Length - needle.Length)
            {
                int i = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                _textMatches.Add(i);
                from = i + 1;
            }

            _matchIndex = _textMatches.Count > 0 ? 0 : -1;
        }
        else if (_textMatches.Count > 0)
        {
            _matchIndex = ((_matchIndex + dir) % _textMatches.Count + _textMatches.Count) % _textMatches.Count;
        }

        if (_matchIndex >= 0 && _matchIndex < _textMatches.Count)
        {
            int offset = _textMatches[_matchIndex];
            txtContent.Select(offset, needle.Length);

            var docLine = txtContent.Document.GetLineByOffset(offset);
            txtContent.ScrollToLine(docLine.LineNumber);

            txtMatches.Text = $"{_matchIndex + 1} / {_textMatches.Count}";
        }
        else
        {
            txtMatches.Text = "0 / 0";
        }
    }
}
