using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace R2Cmd.Terminal;

[Flags]
public enum CellFlags : byte { None = 0, Bold = 1, Underline = 2, Inverse = 4, Wrapped = 8 }

// Fg/Bg: -1 means "theme default", 0..255 is the ANSI palette,
// anything >= TrueColorBase is 0xRRGGBB packed with a marker bit.
public struct Cell
{
    public char Ch;
    public int Fg;
    public int Bg;
    public CellFlags Flags;
}

// Mouse tracking modes requested by terminal apps (e.g., Midnight Commander, htop)
public enum MouseMode { None, X10, Normal, ButtonEvent, AnyEvent }

// Encoding format for mouse coordinates
public enum MouseEncoding { Default, UTF8, SGR, URXVT }

// =============================================================================
// Fixed size history of scrolled off lines.
//
// A plain List with RemoveRange(0, excess) moves every remaining element down by
// one on each scrolled line once the limit is reached — five thousand pointer
// copies per line of output, and a build log scrolls thousands of lines. A ring
// overwrites the oldest slot instead and costs nothing.
//
// Indexing stays oldest-first, so the renderer needs no changes.
// =============================================================================
public sealed class ScrollbackBuffer : IReadOnlyList<Cell[]>
{
    private Cell[]?[] _items;
    private int _start;
    private int _count;

    public ScrollbackBuffer(int capacity)
    {
        _items = new Cell[]?[Math.Max(1, capacity)];
    }

    public int Count => _count;
    public int Capacity => _items.Length;

    public Cell[] this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_start + index) % _items.Length]!;
        }
    }

    public void Add(Cell[] line)
    {
        if (_count < _items.Length)
        {
            _items[(_start + _count) % _items.Length] = line;
            _count++;
            return;
        }

        // Full: the oldest slot becomes the newest one
        _items[_start] = line;
        _start = (_start + 1) % _items.Length;
    }

    public void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        _start = 0;
        _count = 0;
    }

    // Keeps the newest lines that still fit
    public void SetCapacity(int capacity)
    {
        capacity = Math.Max(1, capacity);
        if (capacity == _items.Length) return;

        int keep = Math.Min(_count, capacity);
        var next = new Cell[]?[capacity];

        for (int i = 0; i < keep; i++)
            next[i] = this[_count - keep + i];

        _items = next;
        _start = 0;
        _count = keep;
    }

    public IEnumerator<Cell[]> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

// Screen buffer plus a VT/ANSI parser. Deliberately UI free: it knows nothing
// about WPF, so it can be unit tested and swapped for another renderer.
public sealed class VtScreen
{
    public const int TrueColorBase = 0x1000000;

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public Cell[] Buffer { get; private set; }

    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;

    // Tracks whether the terminal requested Application Cursor Keys mode (e.g. for Midnight Commander)
    public bool ApplicationCursorKeys { get; private set; }

    // Advanced Terminal Features exposed to the UI Host
    public MouseMode MouseTrackingMode { get; private set; } = MouseMode.None;
    public MouseEncoding MouseTrackingEncoding { get; private set; } = MouseEncoding.Default;
    public bool BracketedPasteMode { get; private set; }

    // Events for dynamic features
    public event Action<string>? WindowTitleChanged;
    public event Action<string>? ClipboardCopyRequested;

    // Bumped on every visible change so the renderer can skip idle frames
    public long Version { get; private set; }

    private Cell[]? _altBuffer;
    private bool _inAltBuffer;

    // Lines that scrolled off the top. Kept so the user can look back at build
    // output; full screen apps (alt buffer) deliberately do not contribute.
    private readonly ScrollbackBuffer _scrollback = new(5000);
    public IReadOnlyList<Cell[]> Scrollback => _scrollback;

    public int ScrollbackLimit
    {
        get => _scrollback.Capacity;
        set => _scrollback.SetCapacity(value);
    }

    private int _scrollTop;
    private int _scrollBottom;

    private int _curFg = -1, _curBg = -1;
    private CellFlags _curFlags = CellFlags.None;

    private int _savedX, _savedY;
    private bool _wrapPending;

    private enum State { Ground, Esc, Csi, Osc, OscEsc, Charset }
    private State _state = State.Ground;

    private readonly List<int> _params = new();
    private int _paramValue = -1;
    private char _privateMarker;

    // Buffer for reading Operating System Commands (OSC) like Window Titles or OSC 52 clipboard
    private readonly StringBuilder _oscBuffer = new();

    public VtScreen(int cols, int rows)
    {
        Cols = Math.Max(cols, 8);
        Rows = Math.Max(rows, 2);
        Buffer = new Cell[Cols * Rows];
        Clear(Buffer);
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    private void Clear(Cell[] buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = new Cell { Ch = ' ', Fg = -1, Bg = -1, Flags = CellFlags.None };
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(cols, 8);
        rows = Math.Max(rows, 2);
        if (cols == Cols && rows == Rows) return;

        // If in alt buffer (like Midnight Commander), skip reflow and do rigid crop/expand.
        // Full-screen apps manage their own layout via SIGWINCH signals.
        if (_inAltBuffer)
        {
            _altBuffer = ResizeAltBuffer(_altBuffer, cols, rows, Cols, Rows);
            Buffer = ResizeAltBuffer(Buffer, cols, rows, Cols, Rows);

            int copyRowsForCursor = Math.Min(rows, Rows);
            int srcTopForCursor = Rows - copyRowsForCursor;
            int dstTopForCursor = rows - copyRowsForCursor;

            CursorX = Math.Min(CursorX, cols - 1);
            CursorY = Math.Min(Math.Max(CursorY - srcTopForCursor + dstTopForCursor, 0), rows - 1);

            _savedX = Math.Min(_savedX, cols - 1);
            _savedY = Math.Min(Math.Max(_savedY - srcTopForCursor + dstTopForCursor, 0), rows - 1);

            Cols = cols;
            Rows = rows;
            _scrollTop = 0;
            _scrollBottom = rows - 1;
            _wrapPending = false;
            Version++;
            return;
        }

        // --- PRIMARY BUFFER TEXT REFLOW ALGORITHM (Flex) ---

        var logicalLines = new List<List<Cell>>();
        var currentLogicalLine = new List<Cell>();

        int cursorLogicalLine = -1;
        int cursorLogicalChar = 0;

        void ProcessPhysicalLine(Cell[] line, bool isCursorRow)
        {
            bool isWrapped = line.Length > 0 && (line[line.Length - 1].Flags & CellFlags.Wrapped) != 0;

            int keepLen = line.Length;
            if (!isWrapped)
            {
                // Trim trailing blanks so empty space doesn't artificially wrap when shrinking
                while (keepLen > 0)
                {
                    var c = line[keepLen - 1];
                    if (c.Ch != ' ' || c.Fg != -1 || c.Bg != -1 || (c.Flags & ~CellFlags.Wrapped) != CellFlags.None)
                        break;
                    keepLen--;
                }
            }

            if (isCursorRow)
            {
                cursorLogicalLine = logicalLines.Count;
                cursorLogicalChar = currentLogicalLine.Count + CursorX;
            }

            for (int i = 0; i < keepLen; i++)
            {
                var cell = line[i];
                cell.Flags &= ~CellFlags.Wrapped; // Strip the flag; it will be recalculated
                currentLogicalLine.Add(cell);
            }

            if (!isWrapped)
            {
                logicalLines.Add(currentLogicalLine);
                currentLogicalLine = new List<Cell>();
            }
        }

        // 1. Extract and unwrap the scrollback history
        for (int i = 0; i < _scrollback.Count; i++)
        {
            ProcessPhysicalLine(_scrollback[i], false);
        }

        // 2. Extract and unwrap the current visible screen
        for (int y = 0; y < Rows; y++)
        {
            var line = new Cell[Cols];
            Array.Copy(Buffer, y * Cols, line, 0, Cols);
            ProcessPhysicalLine(line, y == CursorY);
        }

        if (currentLogicalLine.Count > 0) logicalLines.Add(currentLogicalLine);

        // 3. Repack the unwrapped paragraphs into the new column width
        var newPhysicalLines = new List<Cell[]>();
        int newCursorY = 0;
        int newCursorX = 0;

        Cell[] CreateBlankLine()
        {
            var line = new Cell[cols];
            for (int i = 0; i < cols; i++)
                line[i] = new Cell { Ch = ' ', Fg = -1, Bg = -1, Flags = CellFlags.None };
            return line;
        }

        for (int i = 0; i < logicalLines.Count; i++)
        {
            var logLine = logicalLines[i];

            if (i == cursorLogicalLine)
            {
                newCursorY = newPhysicalLines.Count + (cursorLogicalChar / cols);
                newCursorX = cursorLogicalChar % cols;
            }

            if (logLine.Count == 0)
            {
                newPhysicalLines.Add(CreateBlankLine());
                continue;
            }

            for (int chunkStart = 0; chunkStart < logLine.Count; chunkStart += cols)
            {
                int chunkLen = Math.Min(cols, logLine.Count - chunkStart);
                var physLine = CreateBlankLine();

                for (int j = 0; j < chunkLen; j++)
                {
                    physLine[j] = logLine[chunkStart + j];
                }

                // If the text continues into the next chunk, mark the new edge as wrapped
                if (chunkStart + cols < logLine.Count)
                {
                    physLine[cols - 1].Flags |= CellFlags.Wrapped;
                }

                newPhysicalLines.Add(physLine);
            }
        }

        // 4. Distribute the reflowed lines back to scrollback and the primary screen
        _scrollback.Clear();
        Buffer = new Cell[cols * rows];
        Clear(Buffer);

        int totalLines = newPhysicalLines.Count;
        int linesToPrimary = Math.Min(totalLines, rows);
        int linesToScrollback = totalLines - linesToPrimary;

        for (int i = 0; i < linesToScrollback; i++)
        {
            _scrollback.Add(newPhysicalLines[i]);
        }

        int primaryStartIndex = totalLines - linesToPrimary;
        int destinationStartY = rows - linesToPrimary;

        for (int i = 0; i < linesToPrimary; i++)
        {
            var sourceLine = newPhysicalLines[primaryStartIndex + i];
            Array.Copy(sourceLine, 0, Buffer, (destinationStartY + i) * cols, cols);
        }

        // 5. Update and clamp the cursor to its new mapped position
        CursorX = Math.Max(0, Math.Min(newCursorX, cols - 1));
        CursorY = Math.Max(0, Math.Min(newCursorY - linesToScrollback + destinationStartY, rows - 1));

        Cols = cols;
        Rows = rows;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        _wrapPending = false;
        Version++;
    }

    private Cell[] ResizeAltBuffer(Cell[]? sourceBuf, int cols, int rows, int oldCols, int oldRows)
    {
        var next = new Cell[cols * rows];
        for (int i = 0; i < next.Length; i++)
            next[i] = new Cell { Ch = ' ', Fg = -1, Bg = -1, Flags = CellFlags.None };

        if (sourceBuf == null) return next;

        int copyRows = Math.Min(rows, oldRows);
        int copyCols = Math.Min(cols, oldCols);
        int srcTop = oldRows - copyRows;
        int dstTop = rows - copyRows;

        for (int y = 0; y < copyRows; y++)
            for (int x = 0; x < copyCols; x++)
                next[(dstTop + y) * cols + x] = sourceBuf[(srcTop + y) * oldCols + x];

        return next;
    }

    public void Write(char[] data, int count)
    {
        for (int i = 0; i < count; i++) Feed(data[i]);
        Version++;
    }

    // ===================== Parser =====================
    private void Feed(char c)
    {
        switch (_state)
        {
            case State.Ground: Ground(c); return;
            case State.Esc: Esc(c); return;
            case State.Csi: Csi(c); return;
            case State.Osc:
                if (c == '\a') { ProcessOsc(); _state = State.Ground; }
                else if (c == '\x1b') _state = State.OscEsc;
                else if (c >= ' ') _oscBuffer.Append(c);
                return;
            case State.OscEsc:
                if (c == '\\') { ProcessOsc(); _state = State.Ground; }
                else _state = State.Ground; // Malformed terminator
                return;
            case State.Charset: _state = State.Ground; return;  // ESC ( B and friends
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1b': _state = State.Esc; return;
            case '\r': CursorX = 0; _wrapPending = false; return;
            case '\n': LineFeed(); return;
            case '\b': if (CursorX > 0) CursorX--; _wrapPending = false; return;
            case '\t': CursorX = Math.Min(((CursorX / 8) + 1) * 8, Cols - 1); return;
            case '\a': return;
            case '\0': return;
        }

        if (c < ' ') return;
        PutChar(c);
    }

    private void Esc(char c)
    {
        switch (c)
        {
            case '[': _params.Clear(); _paramValue = -1; _privateMarker = '\0'; _state = State.Csi; return;
            case ']': _oscBuffer.Clear(); _state = State.Osc; return;
            case '(':
            case ')': _state = State.Charset; return;
            case 'M': ReverseIndex(); _state = State.Ground; return;
            case '7': _savedX = CursorX; _savedY = CursorY; _state = State.Ground; return;
            case '8': CursorX = _savedX; CursorY = _savedY; _state = State.Ground; return;
            case 'c': FullReset(); _state = State.Ground; return;
            default: _state = State.Ground; return;
        }
    }

    private void Csi(char c)
    {
        if (c is '?' or '>' or '<' or '!') { _privateMarker = c; return; }

        if (c is >= '0' and <= '9')
        {
            _paramValue = (_paramValue < 0 ? 0 : _paramValue) * 10 + (c - '0');
            return;
        }

        if (c == ';') { _params.Add(_paramValue); _paramValue = -1; return; }

        if (c is >= ' ' and <= '/') return; // intermediate bytes, ignored

        _params.Add(_paramValue);
        _paramValue = -1;
        _state = State.Ground;
        Execute(c);
    }

    private int P(int index, int fallback)
    {
        if (index >= _params.Count) return fallback;
        int v = _params[index];
        return v < 0 ? fallback : v;
    }

    private void Execute(char c)
    {
        switch (c)
        {
            case 'A': CursorY = Math.Max(CursorY - P(0, 1), 0); break;
            case 'B': CursorY = Math.Min(CursorY + P(0, 1), Rows - 1); break;
            case 'C': CursorX = Math.Min(CursorX + P(0, 1), Cols - 1); break;
            case 'D': CursorX = Math.Max(CursorX - P(0, 1), 0); break;
            case 'E': CursorY = Math.Min(CursorY + P(0, 1), Rows - 1); CursorX = 0; break;
            case 'F': CursorY = Math.Max(CursorY - P(0, 1), 0); CursorX = 0; break;
            case 'G':
            case '`': CursorX = Clamp(P(0, 1) - 1, Cols); break;
            case 'd': CursorY = Clamp(P(0, 1) - 1, Rows); break;

            case 'H':
            case 'f':
                CursorY = Clamp(P(0, 1) - 1, Rows);
                CursorX = Clamp(P(1, 1) - 1, Cols);
                _wrapPending = false;
                break;

            case 'J': EraseInDisplay(P(0, 0)); break;
            case 'K': EraseInLine(P(0, 0)); break;
            case 'L': InsertLines(P(0, 1)); break;
            case 'M': DeleteLines(P(0, 1)); break;
            case '@': InsertChars(P(0, 1)); break;
            case 'P': DeleteChars(P(0, 1)); break;
            case 'X': EraseChars(P(0, 1)); break;
            case 'S': ScrollUp(P(0, 1)); break;
            case 'T': ScrollDown(P(0, 1)); break;

            case 'r':
                _scrollTop = Clamp(P(0, 1) - 1, Rows);
                _scrollBottom = Clamp(P(1, Rows) - 1, Rows);
                if (_scrollBottom <= _scrollTop) { _scrollTop = 0; _scrollBottom = Rows - 1; }
                CursorX = 0; CursorY = _scrollTop;
                break;

            case 'm': ApplySgr(); break;
            case 'h': SetMode(true); break;
            case 'l': SetMode(false); break;
            case 's': _savedX = CursorX; _savedY = CursorY; break;
            case 'u': CursorX = _savedX; CursorY = _savedY; break;
        }
    }

    private static int Clamp(int value, int limit) => Math.Min(Math.Max(value, 0), limit - 1);

    private void SetMode(bool enable)
    {
        if (_privateMarker != '?') return;

        foreach (int p in _params)
        {
            switch (p)
            {
                case 1: ApplicationCursorKeys = enable; break;
                case 9: MouseTrackingMode = enable ? MouseMode.X10 : MouseMode.None; break;
                case 25: CursorVisible = enable; break;
                case 1000: MouseTrackingMode = enable ? MouseMode.Normal : MouseMode.None; break;
                case 1002: MouseTrackingMode = enable ? MouseMode.ButtonEvent : MouseMode.None; break;
                case 1003: MouseTrackingMode = enable ? MouseMode.AnyEvent : MouseMode.None; break;
                case 1005: MouseTrackingEncoding = enable ? MouseEncoding.UTF8 : MouseEncoding.Default; break;
                case 1006: MouseTrackingEncoding = enable ? MouseEncoding.SGR : MouseEncoding.Default; break;
                case 1015: MouseTrackingEncoding = enable ? MouseEncoding.URXVT : MouseEncoding.Default; break;
                case 1049: SwitchAltBuffer(enable); break;
                case 2004: BracketedPasteMode = enable; break;
            }
        }
    }

    // Processes Operating System Commands (OSC) that were buffered
    private void ProcessOsc()
    {
        string payload = _oscBuffer.ToString();

        // OSC 0 or 2: Set window title and/or icon name
        if (payload.StartsWith("0;") || payload.StartsWith("2;"))
        {
            WindowTitleChanged?.Invoke(payload.Substring(2));
        }
        // OSC 52: Pass-through clipboard copy
        else if (payload.StartsWith("52;"))
        {
            var parts = payload.Split(new[] { ';' }, 3);
            if (parts.Length == 3)
            {
                try
                {
                    // Format is usually 52;c;BASE64
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                    ClipboardCopyRequested?.Invoke(decoded);
                }
                catch { /* Ignore malformed Base64 from server */ }
            }
        }
    }

    // Full screen apps (vim, less) draw on a separate buffer and expect the
    // previous screen back untouched when they exit
    private void SwitchAltBuffer(bool useAlt)
    {
        if (useAlt == _inAltBuffer) return;

        if (useAlt)
        {
            _altBuffer = Buffer;
            Buffer = new Cell[Cols * Rows];
            Clear(Buffer);
            _savedX = CursorX; _savedY = CursorY;
            CursorX = 0; CursorY = 0;
        }
        else if (_altBuffer != null)
        {
            Buffer = _altBuffer;
            _altBuffer = null;
            CursorX = _savedX; CursorY = _savedY;
        }
        else
        {
            // The alt buffer was dropped by a resize while the app was running.
            // Leaving its contents on screen would look like the app never
            // exited, so start from a clean primary screen instead.
            Clear(Buffer);
            CursorX = 0; CursorY = 0;
        }

        _inAltBuffer = useAlt;
    }

    private void ApplySgr()
    {
        if (_params.Count == 0 || (_params.Count == 1 && _params[0] < 0))
        {
            _curFg = -1; _curBg = -1; _curFlags = CellFlags.None;
            return;
        }

        for (int i = 0; i < _params.Count; i++)
        {
            int p = _params[i] < 0 ? 0 : _params[i];

            switch (p)
            {
                case 0: _curFg = -1; _curBg = -1; _curFlags = CellFlags.None; break;
                case 1: _curFlags |= CellFlags.Bold; break;
                case 4: _curFlags |= CellFlags.Underline; break;
                case 7: _curFlags |= CellFlags.Inverse; break;
                case 22: _curFlags &= ~CellFlags.Bold; break;
                case 24: _curFlags &= ~CellFlags.Underline; break;
                case 27: _curFlags &= ~CellFlags.Inverse; break;
                case 39: _curFg = -1; break;
                case 49: _curBg = -1; break;

                case 38: _curFg = ReadExtendedColor(ref i); break;
                case 48: _curBg = ReadExtendedColor(ref i); break;

                default:
                    if (p is >= 30 and <= 37) _curFg = p - 30;
                    else if (p is >= 40 and <= 47) _curBg = p - 40;
                    else if (p is >= 90 and <= 97) _curFg = p - 90 + 8;
                    else if (p is >= 100 and <= 107) _curBg = p - 100 + 8;
                    break;
            }
        }
    }

    // Handles both "5;n" (256 colour) and "2;r;g;b" (true colour) forms
    private int ReadExtendedColor(ref int i)
    {
        int mode = P(i + 1, -1);

        if (mode == 5)
        {
            int idx = P(i + 2, 0);
            i += 2;
            return Math.Min(Math.Max(idx, 0), 255);
        }

        if (mode == 2)
        {
            int r = P(i + 2, 0), g = P(i + 3, 0), b = P(i + 4, 0);
            i += 4;
            return TrueColorBase | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
        }

        return -1;
    }

    // ===================== Buffer operations =====================
    private void PutChar(char c)
    {
        if (_wrapPending)
        {
            // Mark the last cell of the current row as soft-wrapped before moving down
            Buffer[CursorY * Cols + Cols - 1].Flags |= CellFlags.Wrapped;

            CursorX = 0;
            LineFeed();
            _wrapPending = false;
        }

        Buffer[CursorY * Cols + CursorX] = new Cell { Ch = c, Fg = _curFg, Bg = _curBg, Flags = _curFlags };

        if (CursorX + 1 >= Cols) _wrapPending = true;  // deferred wrap, matches xterm
        else CursorX++;
    }

    private void LineFeed()
    {
        if (CursorY == _scrollBottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        if (CursorY == _scrollTop) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    private void ScrollUp(int n)
    {
        n = Math.Min(n, _scrollBottom - _scrollTop + 1);

        // Only whole screen scrolling produces history: a scroll region belongs
        // to an app drawing its own layout, and those lines are not transcript
        if (!_inAltBuffer && _scrollTop == 0)
        {
            for (int i = 0; i < n; i++)
            {
                var line = new Cell[Cols];
                Array.Copy(Buffer, i * Cols, line, 0, Cols);
                _scrollback.Add(line);
            }
        }

        for (int y = _scrollTop; y <= _scrollBottom - n; y++)
            Array.Copy(Buffer, (y + n) * Cols, Buffer, y * Cols, Cols);

        for (int y = _scrollBottom - n + 1; y <= _scrollBottom; y++) ClearRow(y);
    }

    private void ScrollDown(int n)
    {
        n = Math.Min(n, _scrollBottom - _scrollTop + 1);
        for (int y = _scrollBottom; y >= _scrollTop + n; y--)
            Array.Copy(Buffer, (y - n) * Cols, Buffer, y * Cols, Cols);

        for (int y = _scrollTop; y < _scrollTop + n; y++) ClearRow(y);
    }

    private void InsertLines(int n)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom) return;
        n = Math.Min(n, _scrollBottom - CursorY + 1);

        for (int y = _scrollBottom; y >= CursorY + n; y--)
            Array.Copy(Buffer, (y - n) * Cols, Buffer, y * Cols, Cols);

        for (int y = CursorY; y < CursorY + n; y++) ClearRow(y);
    }

    private void DeleteLines(int n)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom) return;
        n = Math.Min(n, _scrollBottom - CursorY + 1);

        for (int y = CursorY; y <= _scrollBottom - n; y++)
            Array.Copy(Buffer, (y + n) * Cols, Buffer, y * Cols, Cols);

        for (int y = _scrollBottom - n + 1; y <= _scrollBottom; y++) ClearRow(y);
    }

    private void InsertChars(int n)
    {
        n = Math.Min(n, Cols - CursorX);
        int row = CursorY * Cols;

        for (int x = Cols - 1; x >= CursorX + n; x--) Buffer[row + x] = Buffer[row + x - n];
        for (int x = CursorX; x < CursorX + n; x++) Buffer[row + x] = Blank();
    }

    private void DeleteChars(int n)
    {
        n = Math.Min(n, Cols - CursorX);
        int row = CursorY * Cols;

        for (int x = CursorX; x < Cols - n; x++) Buffer[row + x] = Buffer[row + x + n];
        for (int x = Cols - n; x < Cols; x++) Buffer[row + x] = Blank();
    }

    private void EraseChars(int n)
    {
        n = Math.Min(n, Cols - CursorX);
        int row = CursorY * Cols;
        for (int x = CursorX; x < CursorX + n; x++) Buffer[row + x] = Blank();
    }

    private void EraseInLine(int mode)
    {
        int row = CursorY * Cols;
        int from = mode == 0 ? CursorX : 0;
        int to = mode == 1 ? CursorX : Cols - 1;

        for (int x = from; x <= to && x < Cols; x++) Buffer[row + x] = Blank();
    }

    private void EraseInDisplay(int mode)
    {
        if (mode == 2 || mode == 3)
        {
            if (mode == 3) _scrollback.Clear();

            // Erase uses the current background, unlike a hard reset
            for (int y = 0; y < Rows; y++) ClearRow(y);
            return;
        }

        if (mode == 0)
        {
            EraseInLine(0);
            for (int y = CursorY + 1; y < Rows; y++) ClearRow(y);
        }
        else
        {
            EraseInLine(1);
            for (int y = 0; y < CursorY; y++) ClearRow(y);
        }
    }

    private void ClearRow(int y)
    {
        int row = y * Cols;
        for (int x = 0; x < Cols; x++) Buffer[row + x] = Blank();
    }

    private Cell Blank() => new() { Ch = ' ', Fg = -1, Bg = _curBg, Flags = CellFlags.None };

    private void FullReset()
    {
        Clear(Buffer);
        CursorX = CursorY = 0;
        _curFg = _curBg = -1;
        _curFlags = CellFlags.None;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        CursorVisible = true;
        _wrapPending = false;

        ApplicationCursorKeys = false;

        MouseTrackingMode = MouseMode.None;
        MouseTrackingEncoding = MouseEncoding.Default;
        BracketedPasteMode = false;
    }
}
