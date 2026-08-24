namespace Gmc300sTui.Ui;

/// <summary>
/// Small terminal cell buffer that keeps layout independent from ANSI escape
/// sequence widths.  It uses the standard Console color API, so it works in
/// Windows Terminal as well as traditional Windows console hosts.
/// </summary>
internal sealed class ConsoleCanvas
{
    private readonly Cell[,] _cells;

    private readonly record struct Cell(char Ch, ConsoleColor Foreground, ConsoleColor Background);

    public ConsoleCanvas(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _cells = new Cell[Height, Width];
        Clear();
    }

    public int Width { get; }
    public int Height { get; }

    public void Clear(ConsoleColor foreground = ConsoleColor.Gray, ConsoleColor background = ConsoleColor.Black)
    {
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            _cells[y, x] = new Cell(' ', foreground, background);
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
            _cells[row, col] = new Cell(ch, foreground, background);
    }

    public void Put(int x, int y, char ch,
        ConsoleColor foreground = ConsoleColor.Gray,
        ConsoleColor background = ConsoleColor.Black)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return;
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
            _cells[y, destX + i] = new Cell(text[sourceOffset + i], foreground, background);
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
                    var cell = _cells[y, x];
                    var start = x;
                    x++;
                    while (x < Width &&
                           _cells[y, x].Foreground == cell.Foreground &&
                           _cells[y, x].Background == cell.Background)
                        x++;

                    Console.ForegroundColor = cell.Foreground;
                    Console.BackgroundColor = cell.Background;
                    var chars = new char[x - start];
                    for (var i = 0; i < chars.Length; i++)
                        chars[i] = _cells[y, start + i].Ch;
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
            // Console host changed/disconnected during a frame.  A later frame can retry.
        }
    }
}
