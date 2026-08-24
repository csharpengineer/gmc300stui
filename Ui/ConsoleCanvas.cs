namespace Gmc300sTui.Ui;

/// <summary>
/// Small terminal cell buffer that keeps layout independent from ANSI escape
/// sequence widths. It uses the standard Console color API, so it works in
/// Windows Terminal as well as traditional Windows console hosts.
///
/// The detailed CPM graph is rendered through a Braille overlay. Unicode
/// Braille gives each terminal cell a 2x4 dot matrix, allowing connected graph
/// segments to slope smoothly instead of collapsing into vertical jumps.
/// </summary>
internal sealed class ConsoleCanvas
{
    private readonly Cell[,] _cells;
    private readonly byte[,] _brailleMasks;
    private readonly ConsoleColor[,] _brailleForegrounds;
    private readonly bool[,] _brailleActive;
    private (int CellX, int SubX, int SubY)? _lastBrailleGraphPoint;

    private readonly record struct Cell(char Ch, ConsoleColor Foreground, ConsoleColor Background);

    public ConsoleCanvas(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _cells = new Cell[Height, Width];
        _brailleMasks = new byte[Height, Width];
        _brailleForegrounds = new ConsoleColor[Height, Width];
        _brailleActive = new bool[Height, Width];
        Clear();
    }

    public int Width { get; }
    public int Height { get; }

    public void Clear(ConsoleColor foreground = ConsoleColor.Gray, ConsoleColor background = ConsoleColor.Black)
    {
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            _cells[y, x] = new Cell(' ', foreground, background);

        Array.Clear(_brailleMasks, 0, _brailleMasks.Length);
        Array.Clear(_brailleActive, 0, _brailleActive.Length);
        _lastBrailleGraphPoint = null;
    }

    public void Fill(int x, int y, int width, int height, char ch = ' ',
        ConsoleColor foreground = ConsoleColor.Gray,
        ConsoleColor background = ConsoleColor.Black)
    {
        var x0 = Math.Clamp(x, 0, Width);
        var y0 = Math.Clamp(y, 0, Height);
        var x1 = Math.Clamp(x + width, 0, Width);
        var y1 = Math.Clamp(y + height, 0, Height);

        for (var row = y0; row < y1; row++)
        for (var col = x0; col < x1; col++)
        {
            ClearBrailleCell(col, row);
            _cells[row, col] = new Cell(ch, foreground, background);
        }
    }

    public void Put(int x, int y, char ch,
        ConsoleColor foreground = ConsoleColor.Gray,
        ConsoleColor background = ConsoleColor.Black)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return;

        // ResponsiveTuiApp emits its CPM series as cyan point/connector glyphs.
        // Capture the points and rebuild the path in a 2x4 Braille sub-cell grid.
        // Connector glyphs are intentionally ignored here; the point sequence is
        // enough to draw a continuous line and avoids the old overwrite problem.
        if (IsBrailleGraphConnector(ch, foreground))
            return;

        if (IsBrailleGraphPoint(ch, foreground))
        {
            PlotBrailleGraphPoint(x, y, foreground);
            return;
        }

        ClearBrailleCell(x, y);
        _cells[y, x] = new Cell(ch, foreground, background);
    }

    public void Write(int x, int y, string? text,
        ConsoleColor foreground = ConsoleColor.Gray,
        ConsoleColor background = ConsoleColor.Black,
        int? maxWidth = null)
    {
        if (string.IsNullOrEmpty(text) || y < 0 || y >= Height || x >= Width)
            return;

        var limit = Math.Min(maxWidth ?? int.MaxValue, Width - Math.Max(0, x));
        if (limit <= 0)
            return;

        var sourceOffset = x < 0 ? -x : 0;
        var destX = Math.Max(0, x);
        var count = Math.Min(text.Length - sourceOffset, limit);
        if (count <= 0)
            return;

        for (var i = 0; i < count; i++)
        {
            ClearBrailleCell(destX + i, y);
            _cells[y, destX + i] = new Cell(text[sourceOffset + i], foreground, background);
        }
    }

    public void WriteRight(int rightX, int y, string? text,
        ConsoleColor foreground = ConsoleColor.Gray,
        ConsoleColor background = ConsoleColor.Black)
    {
        if (string.IsNullOrEmpty(text)) return;
        Write(rightX - text.Length + 1, y, text, foreground, background);
    }

    public void WriteCentered(int left, int right, int y, string? text,
        ConsoleColor foreground = ConsoleColor.Gray,
        ConsoleColor background = ConsoleColor.Black)
    {
        if (string.IsNullOrEmpty(text)) return;
        var width = Math.Max(0, right - left + 1);
        var x = left + Math.Max(0, (width - text.Length) / 2);
        Write(x, y, text, foreground, background, width);
    }

    public void Horizontal(int x, int y, int width, char ch = '─', ConsoleColor color = ConsoleColor.DarkGray)
    {
        for (var i = 0; i < width; i++)
            Put(x + i, y, ch, color);
    }

    public void Vertical(int x, int y, int height, char ch = '│', ConsoleColor color = ConsoleColor.DarkGray)
    {
        for (var i = 0; i < height; i++)
            Put(x, y + i, ch, color);
    }

    public void Box(int x, int y, int width, int height,
        ConsoleColor color = ConsoleColor.DarkGray,
        string? title = null,
        ConsoleColor titleColor = ConsoleColor.Cyan)
    {
        if (width < 2 || height < 2) return;
        Horizontal(x + 1, y, width - 2, '─', color);
        Horizontal(x + 1, y + height - 1, width - 2, '─', color);
        Vertical(x, y + 1, height - 2, '│', color);
        Vertical(x + width - 1, y + 1, height - 2, '│', color);
        Put(x, y, '┌', color);
        Put(x + width - 1, y, '┐', color);
        Put(x, y + height - 1, '└', color);
        Put(x + width - 1, y + height - 1, '┘', color);

        if (!string.IsNullOrWhiteSpace(title) && width > title.Length + 4)
        {
            Write(x + 2, y, $" {title} ", titleColor, ConsoleColor.Black, width - 4);
        }
    }

    private static bool IsBrailleGraphPoint(char ch, ConsoleColor foreground) =>
        ch is '•' or '●' && foreground is ConsoleColor.Cyan or ConsoleColor.Yellow;

    private static bool IsBrailleGraphConnector(char ch, ConsoleColor foreground) =>
        foreground == ConsoleColor.Cyan && ch is '─' or '│' or '╱' or '╲';

    private void PlotBrailleGraphPoint(int cellX, int cellY, ConsoleColor pointColor)
    {
        // Put the logical point near the center of its terminal cell. Consecutive
        // cell centers are two Braille sub-pixels apart horizontally and four apart
        // vertically, giving Bresenham enough resolution for visible slopes.
        var subX = cellX * 2 + 1;
        var subY = cellY * 4 + 2;

        if (_lastBrailleGraphPoint is { } previous)
        {
            if (cellX > previous.CellX)
            {
                PlotBrailleLine(previous.SubX, previous.SubY, subX, subY, ConsoleColor.Cyan);
            }
            else
            {
                // A non-increasing X means a new plot sequence began on this frame.
                _lastBrailleGraphPoint = null;
            }
        }

        SetBrailleDot(subX, subY, pointColor);
        _lastBrailleGraphPoint = (cellX, subX, subY);
    }

    private void PlotBrailleLine(int x0, int y0, int x1, int y1, ConsoleColor color)
    {
        // Integer Bresenham in Braille sub-pixel coordinates.
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            SetBrailleDot(x0, y0, color);
            if (x0 == x1 && y0 == y1)
                break;

            var e2 = 2 * error;
            if (e2 >= dy)
            {
                error += dy;
                x0 += sx;
            }
            if (e2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private void SetBrailleDot(int subX, int subY, ConsoleColor color)
    {
        if (subX < 0 || subY < 0)
            return;

        var cellX = subX / 2;
        var cellY = subY / 4;
        if (cellX < 0 || cellX >= Width || cellY < 0 || cellY >= Height)
            return;

        var localX = subX % 2;
        var localY = subY % 4;
        var bit = BrailleBit(localX, localY);

        _brailleMasks[cellY, cellX] |= bit;
        if (!_brailleActive[cellY, cellX] || ColorPriority(color) >= ColorPriority(_brailleForegrounds[cellY, cellX]))
            _brailleForegrounds[cellY, cellX] = color;
        _brailleActive[cellY, cellX] = true;
    }

    private static byte BrailleBit(int x, int y) => (x, y) switch
    {
        (0, 0) => 1 << 0, // dot 1
        (0, 1) => 1 << 1, // dot 2
        (0, 2) => 1 << 2, // dot 3
        (0, 3) => 1 << 6, // dot 7
        (1, 0) => 1 << 3, // dot 4
        (1, 1) => 1 << 4, // dot 5
        (1, 2) => 1 << 5, // dot 6
        (1, 3) => 1 << 7, // dot 8
        _ => 0
    };

    private static int ColorPriority(ConsoleColor color) => color switch
    {
        ConsoleColor.Yellow => 2,
        ConsoleColor.Cyan => 1,
        _ => 0
    };

    private void ClearBrailleCell(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return;
        _brailleMasks[y, x] = 0;
        _brailleActive[y, x] = false;
    }

    private Cell EffectiveCell(int x, int y)
    {
        var baseCell = _cells[y, x];
        if (!_brailleActive[y, x] || _brailleMasks[y, x] == 0)
            return baseCell;

        return new Cell(
            (char)(0x2800 + _brailleMasks[y, x]),
            _brailleForegrounds[y, x],
            baseCell.Background);
    }

    public void Render()
    {
        try
        {
            Console.CursorVisible = false;
            for (var y = 0; y < Height; y++)
            {
                Console.SetCursorPosition(0, y);
                var x = 0;
                while (x < Width)
                {
                    var cell = EffectiveCell(x, y);
                    var start = x;
                    x++;
                    while (x < Width)
                    {
                        var next = EffectiveCell(x, y);
                        if (next.Foreground != cell.Foreground || next.Background != cell.Background)
                            break;
                        x++;
                    }

                    Console.ForegroundColor = cell.Foreground;
                    Console.BackgroundColor = cell.Background;
                    var chars = new char[x - start];
                    for (var i = 0; i < chars.Length; i++)
                        chars[i] = EffectiveCell(start + i, y).Ch;
                    Console.Write(chars);
                }
            }
            Console.ResetColor();
        }
        catch (ArgumentOutOfRangeException)
        {
            // The user resized the terminal between measuring it and rendering.
            // The next frame will use the new dimensions.
        }
        catch (IOException)
        {
            // Console host changed/disconnected during a frame. A later frame can retry.
        }
    }
}
