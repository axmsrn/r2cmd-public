using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace R2Cmd;

/// <summary>
/// Advanced syntax highlighter inspired by VS Code + Noir theme.
/// Supports multi-line comments, functions, attributes and more.
/// Used by the Viewer (F3).
/// </summary>
public static class CodeHighlighter
{
    // ===== Noir (dark) =====
    private static readonly Color NoirFg = Color.FromRgb(0xF8, 0xF8, 0xF2);
    private static readonly Color NoirComment = Color.FromRgb(0x62, 0x72, 0xA4);
    private static readonly Color NoirPink = Color.FromRgb(0xFF, 0x79, 0xC6);
    private static readonly Color NoirYellow = Color.FromRgb(0xF1, 0xFA, 0x8C);
    private static readonly Color NoirPurple = Color.FromRgb(0xBD, 0x93, 0xF9);
    private static readonly Color NoirCyan = Color.FromRgb(0x8B, 0xE9, 0xFD);
    private static readonly Color NoirGreen = Color.FromRgb(0x50, 0xFA, 0x7B);
    private static readonly Color NoirOrange = Color.FromRgb(0xFF, 0xB8, 0x6C);
    private static readonly Color NoirRed = Color.FromRgb(0xFF, 0x55, 0x55);

    // ===== Light theme =====
    private static readonly Color LightFg = Color.FromRgb(0x1F, 0x1F, 0x1F);
    private static readonly Color LightComment = Color.FromRgb(0x6C, 0x66, 0x4B);
    private static readonly Color LightKeyword = Color.FromRgb(0xA3, 0x14, 0x4D);
    private static readonly Color LightString = Color.FromRgb(0x84, 0x6E, 0x15);
    private static readonly Color LightNumber = Color.FromRgb(0x64, 0x4A, 0xC9);
    private static readonly Color LightType = Color.FromRgb(0x03, 0x6A, 0x96);
    private static readonly Color LightFunction = Color.FromRgb(0x14, 0x71, 0x0A);
    private static readonly Color LightAttribute = Color.FromRgb(0xA3, 0x4D, 0x14);

    private enum TokenKind
    {
        Comment, Keyword, String, Number, Type, Function,
        Attribute, Preprocessor, Operator, Plain
    }

    private sealed class Rule
    {
        public Regex Regex { get; }
        public TokenKind Kind { get; }

        public Rule(string pattern, TokenKind kind, RegexOptions extra = RegexOptions.None)
        {
            Regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | extra);
            Kind = kind;
        }
    }

    private static SolidColorBrush B(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public static FlowDocument Highlight(string code, string extension, double fontSize)
    {
        bool dark = ThemeManager.IsDarkTheme;

        var brushes = new Dictionary<TokenKind, SolidColorBrush>
        {
            [TokenKind.Comment] = B(dark ? NoirComment : LightComment),
            [TokenKind.Keyword] = B(dark ? NoirPink : LightKeyword),
            [TokenKind.String] = B(dark ? NoirYellow : LightString),
            [TokenKind.Number] = B(dark ? NoirPurple : LightNumber),
            [TokenKind.Type] = B(dark ? NoirCyan : LightType),
            [TokenKind.Function] = B(dark ? NoirGreen : LightFunction),
            [TokenKind.Attribute] = B(dark ? NoirOrange : LightAttribute),
            [TokenKind.Preprocessor] = B(dark ? NoirOrange : LightAttribute),
            [TokenKind.Operator] = B(dark ? NoirFg : LightFg),
            [TokenKind.Plain] = B(dark ? NoirFg : LightFg),
        };

        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = Math.Max(12, fontSize * 0.95),
            PagePadding = new Thickness(16, 12, 16, 16),
            LineHeight = fontSize * 1.38,
            Background = Application.Current.TryFindResource("Brush.Background") as Brush
        };

        var rules = BuildRules(extension);
        var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        bool inMultiLineComment = false;

        foreach (string rawLine in lines)
        {
            var para = new Paragraph { Margin = new Thickness(0) };
            string line = rawLine;
            int pos = 0;

            if (inMultiLineComment)
            {
                int end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end >= 0)
                {
                    para.Inlines.Add(new Run(line.Substring(0, end + 2)) { Foreground = brushes[TokenKind.Comment] });
                    pos = end + 2;
                    inMultiLineComment = false;
                }
                else
                {
                    para.Inlines.Add(new Run(line) { Foreground = brushes[TokenKind.Comment] });
                    doc.Blocks.Add(para);
                    continue;
                }
            }

            while (pos < line.Length)
            {
                bool matched = false;

                foreach (var rule in rules)
                {
                    var m = rule.Regex.Match(line, pos);
                    if (!m.Success || m.Index != pos) continue;

                    if (rule.Kind == TokenKind.Comment && m.Value.StartsWith("/*") && !m.Value.Contains("*/"))
                        inMultiLineComment = true;

                    para.Inlines.Add(new Run(m.Value) { Foreground = brushes[rule.Kind] });
                    pos += m.Length;
                    matched = true;
                    break;
                }

                if (!matched)
                {
                    int next = line.Length;
                    foreach (var rule in rules)
                    {
                        var m = rule.Regex.Match(line, pos);
                        if (m.Success && m.Index > pos && m.Index < next)
                            next = m.Index;
                    }

                    string plain = line.Substring(pos, next - pos);
                    para.Inlines.Add(new Run(plain) { Foreground = brushes[TokenKind.Plain] });
                    pos = next;
                }
            }

            if (para.Inlines.Count == 0)
                para.Inlines.Add(new Run(" ") { Foreground = brushes[TokenKind.Plain] });

            doc.Blocks.Add(para);
        }

        return doc;
    }

    private static List<Rule> BuildRules(string extension)
    {
        extension = (extension ?? "").ToLowerInvariant();
        var rules = new List<Rule>();

        // Comments (highest priority)
        rules.Add(new Rule(@"//.*?$", TokenKind.Comment));
        rules.Add(new Rule(@"/\*.*?\*/", TokenKind.Comment));
        rules.Add(new Rule(@"/\*.*$", TokenKind.Comment));
        rules.Add(new Rule(@"#.*?$", TokenKind.Comment));

        // Strings
        rules.Add(new Rule(@"""""[\s\S]*?""""|'''[\s\S]*?'''", TokenKind.String));
        rules.Add(new Rule(@"""(?:\\.|[^""\\])*""", TokenKind.String));
        rules.Add(new Rule(@"'(?:\\.|[^'\\])*'", TokenKind.String));
        rules.Add(new Rule(@"`(?:\\.|[^`\\])*`", TokenKind.String));
        rules.Add(new Rule(@"@""(?:[^""]|"""")*""", TokenKind.String));

        // Attributes / Decorators
        rules.Add(new Rule(@"^\s*\[[^\]]+\]", TokenKind.Attribute));
        rules.Add(new Rule(@"@\w+", TokenKind.Attribute));

        // Preprocessor
        rules.Add(new Rule(@"^\s*#\s*\w+.*$", TokenKind.Preprocessor));

        // Numbers
        rules.Add(new Rule(@"\b0[xX][0-9a-fA-F_]+\b", TokenKind.Number));
        rules.Add(new Rule(@"\b0[bB][01_]+\b", TokenKind.Number));
        rules.Add(new Rule(@"\b\d[\d_]*(\.\d[\d_]*)?([eE][+-]?\d+)?[fFdDmM]?\b", TokenKind.Number));

        // Functions
        rules.Add(new Rule(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?=\()", TokenKind.Function));

        // Operators
        rules.Add(new Rule(@"->|=>|::|\?\?|\?\.|&&|\|\||<<|>>|[=<>!+\-*/%&|^~]+", TokenKind.Operator));

        // Keywords
        string keywords = GetKeywords(extension);
        if (!string.IsNullOrEmpty(keywords))
            rules.Add(new Rule(keywords, TokenKind.Keyword));

        // Built-in types
        rules.Add(new Rule(@"\b(string|int|bool|byte|char|decimal|double|float|long|object|short|uint|ulong|ushort|void|var|dynamic|nint|nuint|Int32|Int64|Boolean|String|Object|List|Dictionary|Task|IEnumerable|IList|Array|Option|Result)\b", TokenKind.Type));

        return rules;
    }

    private static string GetKeywords(string ext) => ext switch
    {
        ".cs" or ".fs" or ".vb" =>
            @"\b(abstract|as|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|goto|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|private|protected|public|readonly|ref|return|sbyte|sealed|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|virtual|void|volatile|while|record|var|async|await|required|file|scoped|when|with|init|get|set|value|yield|nameof|notnull|unmanaged)\b",

        ".c" or ".cpp" or ".h" or ".hpp" or ".cc" =>
            @"\b(alignas|alignof|and|and_eq|asm|auto|bitand|bitor|bool|break|case|catch|char|char8_t|char16_t|char32_t|class|compl|concept|const|consteval|constexpr|constinit|const_cast|continue|co_await|co_return|co_yield|decltype|default|delete|do|double|dynamic_cast|else|enum|explicit|export|extern|false|float|for|friend|goto|if|inline|int|long|mutable|namespace|new|noexcept|not|not_eq|nullptr|operator|or|or_eq|private|protected|public|register|reinterpret_cast|requires|return|short|signed|sizeof|static|static_assert|static_cast|struct|switch|template|this|thread_local|throw|true|try|typedef|typeid|typename|union|unsigned|using|virtual|void|volatile|wchar_t|while|xor|xor_eq)\b",

        ".py" =>
            @"\b(and|as|assert|async|await|break|class|continue|def|del|elif|else|except|False|finally|for|from|global|if|import|in|is|lambda|None|nonlocal|not|or|pass|raise|return|True|try|while|with|yield|match|case)\b",

        ".js" or ".ts" or ".mjs" or ".jsx" or ".tsx" =>
            @"\b(abstract|arguments|async|await|boolean|break|byte|case|catch|char|class|const|continue|debugger|default|delete|do|double|else|enum|eval|export|extends|false|final|finally|float|for|function|goto|if|implements|import|in|instanceof|int|interface|let|long|native|new|null|of|package|private|protected|public|return|short|static|super|switch|synchronized|this|throw|throws|transient|true|try|typeof|var|void|volatile|while|with|yield|type|namespace|module|declare|as|from|readonly|keyof|infer|unique|symbol|bigint|any|unknown|never)\b",

        ".java" or ".kt" =>
            @"\b(abstract|assert|boolean|break|byte|case|catch|char|class|const|continue|default|do|double|else|enum|extends|final|finally|float|for|goto|if|implements|import|instanceof|int|interface|long|native|new|package|private|protected|public|return|short|static|strictfp|super|switch|synchronized|this|throw|throws|transient|try|void|volatile|while|true|false|null|var|record|sealed|permits|when|is|in|object|fun|val|companion|data|inline|reified|suspend|override|open|lateinit)\b",

        ".go" =>
            @"\b(break|case|chan|const|continue|default|defer|else|fallthrough|for|func|go|goto|if|import|interface|map|package|range|return|select|struct|switch|type|var|true|false|nil|iota)\b",

        ".rs" =>
            @"\b(as|async|await|break|const|continue|crate|dyn|else|enum|extern|false|fn|for|if|impl|in|let|loop|match|mod|move|mut|pub|ref|return|self|Self|static|struct|super|trait|true|type|unsafe|use|where|while|abstract|become|box|do|final|macro|override|priv|typeof|unsized|virtual|yield)\b",

        ".php" =>
            @"\b(abstract|and|array|as|break|callable|case|catch|class|clone|const|continue|declare|default|do|echo|else|elseif|empty|enddeclare|endfor|endforeach|endif|endswitch|endwhile|eval|exit|extends|final|finally|fn|for|foreach|function|global|goto|if|implements|include|include_once|instanceof|insteadof|interface|isset|list|match|namespace|new|or|print|private|protected|public|require|require_once|return|static|switch|throw|trait|try|unset|use|var|while|xor|yield|true|false|null)\b",

        ".rb" =>
            @"\b(alias|and|begin|break|case|class|def|defined|do|else|elsif|end|ensure|false|for|if|in|module|next|nil|not|or|redo|rescue|retry|return|self|super|then|true|undef|unless|until|when|while|yield)\b",

        ".swift" =>
            @"\b(associatedtype|class|deinit|enum|extension|fileprivate|func|import|init|inout|internal|let|open|operator|private|protocol|public|rethrows|static|struct|subscript|typealias|var|break|case|continue|default|defer|do|else|fallthrough|for|guard|if|in|repeat|return|switch|where|while|as|Any|catch|false|is|nil|super|self|Self|throw|throws|true|try|async|await|actor|some|nonisolated)\b",

        ".sql" =>
            @"\b(SELECT|FROM|WHERE|AND|OR|NOT|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|DROP|ALTER|INDEX|JOIN|LEFT|RIGHT|INNER|OUTER|FULL|ON|GROUP|BY|ORDER|HAVING|LIMIT|OFFSET|AS|DISTINCT|UNION|ALL|EXISTS|BETWEEN|LIKE|IN|IS|NULL|TRUE|FALSE|CASE|WHEN|THEN|ELSE|END|WITH|RECURSIVE|WINDOW|OVER|PARTITION|ROWS|RANGE|UNBOUNDED|PRECEDING|FOLLOWING|CURRENT|ROW)\b",

        ".json" => "",

        _ =>
            @"\b(if|else|for|while|do|switch|case|break|continue|return|function|class|struct|enum|interface|public|private|protected|static|const|var|let|new|this|true|false|null|void|int|string|bool|float|double|async|await|import|from|export|default|try|catch|finally|throw|typeof|instanceof)\b"
    };
}
