namespace Gmc300sTui.Ui;

/// <summary>
/// Small terminal cell buffer that keeps layout independent from ANSI escape
/// sequence widths. It uses the standard Console color API, so it works in
/// Windows Terminal as well as traditional console hosts.
///
/// The detailed CPM graph is captured as a 2x4 sub-cell path. In Braille mode
/// that path becomes Unicode Braille cells; in Sixel mode the same points feed an
/// opaque raster framebuffer that is prepared offscreen and swapped into place.
/// </summary>
internal sealed class ConsoleCanvas
{
    private readonly Cell[,] _cells;
    private readonly byte[,] _brailleMasks;
    private readonly ConsoleColor[,] _brailleForegrounds;
    private readonly bool[,] _brailleActive;
    private readonly List<SixelGraphPoint> _sixelGraphPoints = new();
    private (int CellX, int SubX, int SubY)? _lastBrailleGraphPoint;

    // DrawCpmGraph writes its statistics, scale labels, average annotation and
    // right-side "now" marker in a predictable sequence. Capture those values so
    // Sixel can own the entire plot rectangle rather than acting as a transparent
    // overlay on text-drawn grid lines.
    private bool _capturingGraphScale;
    private readonly List<(int Row, int Value)> _graphScaleLabels = new(3);
    private GraphScale? _brailleGraphScale;
    private int? _sixelPlotX;
    private int? _sixelPlotRight;
    private double? _sixelAverage;

    private readonly record struct Cell(char Ch, ConsoleColor Foreground, ConsoleColor Background);
    private readonly record struct GraphScale(int TopRow, int BottomRow, int MinValue, int MaxValue);

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
        _sixelGraphPoints.Clear();
        _lastBrailleGraphPoint = null;
        _capturingGraphScale = false;
        _graphScaleLabels.Clear();
        _brailleGraphScale = null;
        _sixelPlotX = null;
        _sixelPlotRight = null;
        _sixelAverage = null;
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

        ObserveGraphAverage(text, foreground);

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

        ObserveGraphScaleLabel(rightX, y, text);
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
            Write(x + 2, y, $" {title} ", titleColor, ConsoleColor.Black, width - 4);
    }

    private void ObserveGraphScaleLabel(int rightX, int y, string text)
    {
        if (text.StartsWith("now ", StringComparison.Ordinal) &&
            text.Contains(" samples", StringComparison.Ordinal))
        {
            _capturingGraphScale = true;
            _graphScaleLabels.Clear();
            _sixelGraphPoints.Clear();
            _brailleGraphScale = null;
            _lastBrailleGraphPoint = null;
            _sixelPlotX = null;
            _sixelPlotRight = null;
            _sixelAverage = null;
            return;
        }

        // The bottom-right time marker is written at the actual right edge of the
        // plot. Recording it avoids guessing plot width from the amount of data.
        if (text.Equals("now", StringComparison.Ordinal) && _brailleGraphScale is not null)
        {
            _sixelPlotRight = rightX;
            return;
        }

        if (!_capturingGraphScale ||
            !int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return;

        if (_graphScaleLabels.Count == 0)
            _sixelPlotX = rightX + 2;

        _graphScaleLabels.Add((y, value));
        if (_graphScaleLabels.Count < 3)
            return;

        var top = _graphScaleLabels[0];
        var bottom = _graphScaleLabels[2];
        if (bottom.Row > top.Row && top.Value > bottom.Value)
            _brailleGraphScale = new GraphScale(top.Row, bottom.Row, bottom.Value, top.Value);

        _capturingGraphScale = false;
    }

    private void ObserveGraphAverage(string text, ConsoleColor foreground)
    {
        if (foreground != ConsoleColor.DarkYellow ||
            !text.StartsWith("avg ", StringComparison.Ordinal))
            return;

        if (double.TryParse(
                text.AsSpan(4),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var average))
            _sixelAverage = average;
    }

    private SixelGraphFrame? GetSixelGraphFrame()
    {
        if (_brailleGraphScale is not { } scale ||
            _sixelPlotX is not int plotX ||
            _sixelPlotRight is not int plotRight ||
            _sixelAverage is not double average ||
            plotRight < plotX)
            return null;

        return new SixelGraphFrame(
            plotX,
            scale.TopRow,
            plotRight - plotX + 1,
            scale.BottomRow - scale.TopRow + 1,
            scale.MinValue,
            scale.MaxValue,
            average);
    }

    private static bool IsBrailleGraphPoint(char ch, ConsoleColor foreground) =>
        ch is '•' or '●' && foreground is ConsoleColor.Cyan or ConsoleColor.Yellow;

    private static bool IsBrailleGraphConnector(char ch, ConsoleColor foreground) =>
        foreground == ConsoleColor.Cyan && ch is '─' or '│' or '╱' or '╲';

    private void PlotBrailleGraphPoint(int cellX, int cellY, ConsoleColor pointColor)
    {
        var subX = cellX * 2 + 1;
        var subY = GetFullResolutionGraphSubY(cellY);

        if (_lastBrailleGraphPoint is { } previous)
        {
            if (cellX > previous.CellX)
            {
                PlotBrailleLine(previous.SubX, previous.SubY, subX, subY, ConsoleColor.Cyan);
            }
            else
            {
                _lastBrailleGraphPoint = null;
                _sixelGraphPoints.Clear();
            }
        }

        SetBrailleDot(subX, subY, pointColor);
        _sixelGraphPoints.Add(new SixelGraphPoint(subX, subY, pointColor));
        _lastBrailleGraphPoint = (cellX, subX, subY);
    }

    private int GetFullResolutionGraphSubY(int cellY)
    {
        if (_brailleGraphScale is not { } scale ||
            cellY < scale.TopRow || cellY > scale.BottomRow ||
            scale.MaxValue <= scale.MinValue)
        {
            return cellY * 4 + 2;
        }

        var rowSpan = scale.BottomRow - scale.TopRow;
        var rowFromBottom = scale.BottomRow - cellY;
        var approximateValue = scale.MinValue +
            rowFromBottom / (double)rowSpan * (scale.MaxValue - scale.MinValue);
        var cpm = Math.Clamp(
            (int)Math.Round(approximateValue, MidpointRounding.AwayFromZero),
            scale.MinValue,
            scale.MaxValue);

        var topSubY = scale.TopRow * 4 + 1;
        var bottomSubY = scale.BottomRow * 4 + 2;
        var fraction = (cpm - scale.MinValue) / (double)(scale.MaxValue - scale.MinValue);
        return bottomSubY - (int)Math.Round(
            fraction * (bottomSubY - topSubY),
            MidpointRounding.AwayFromZero);
    }

    private void PlotBrailleLine(int x0, int y0, int x1, int y1, ConsoleColor color)
    {
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
        (0, 0) => 1 << 0,
        (0, 1) => 1 << 1,
        (0, 2) => 1 << 2,
        (0, 3) => 1 << 6,
        (1, 0) => 1 << 3,
        (1, 1) => 1 << 4,
        (1, 2) => 1 << 5,
        (1, 3) => 1 << 7,
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
        if (GraphGraphics.UseSixel)
            return baseCell;
        if (!_brailleActive[y, x] || _brailleMasks[y, x] == 0)
            return baseCell;

        return new Cell(
            (char)(0x2800 + _brailleMasks[y, x]),
            _brailleForegrounds[y, x],
            baseCell.Background);
    }

    private void RenderRange(int y, int firstX, int lastX)
    {
        if (y < 0 || y >= Height)
            return;

        firstX = Math.Clamp(firstX, 0, Width - 1);
        lastX = Math.Clamp(lastX, 0, Width - 1);
        if (lastX < firstX)
            return;

        Console.SetCursorPosition(firstX, y);
        var x = firstX;
        while (x <= lastX)
        {
            var cell = EffectiveCell(x, y);
            var start = x;
            x++;
            while (x <= lastX)
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

    private void RenderRows(int firstRow, int lastRow)
    {
        firstRow = Math.Clamp(firstRow, 0, Height - 1);
        lastRow = Math.Clamp(lastRow, 0, Height - 1);
        if (lastRow < firstRow)
            return;

        for (var y = firstRow; y <= lastRow; y++)
            RenderRange(y, 0, Width - 1);
    }

    /// <summary>
    /// Render all text except the rectangle owned by the persistent Sixel image.
    /// This is the crucial difference from the earlier overlay implementation:
    /// we never erase the visible graph before presenting its prepared successor.
    /// </summary>
    private void RenderOutsideGraphFrame(SixelGraphFrame frame)
    {
        var top = Math.Clamp(frame.PlotY, 0, Height - 1);
        var bottom = Math.Clamp(frame.PlotY + frame.PlotHeight - 1, 0, Height - 1);
        var left = Math.Clamp(frame.PlotX, 0, Width - 1);
        var right = Math.Clamp(frame.PlotX + frame.PlotWidth - 1, 0, Width - 1);

        if (top > 0)
            RenderRows(0, top - 1);

        for (var y = top; y <= bottom; y++)
        {
            if (left > 0)
                RenderRange(y, 0, left - 1);
            if (right < Width - 1)
                RenderRange(y, right + 1, Width - 1);
        }

        if (bottom < Height - 1)
            RenderRows(bottom + 1, Height - 1);
    }

    public void Render()
    {
        try
        {
            Console.CursorVisible = false;

            try
            {
                if (Console.CursorLeft == 0 && Console.CursorTop == 0)
                    GraphGraphics.InvalidateSixelOverlay();
            }
            catch
            {
                // Cursor queries are only a best-effort invalidation hint.
            }

            var sixelFrame = GetSixelGraphFrame();
            if (GraphGraphics.UseSixel &&
                sixelFrame is { } frame &&
                _sixelGraphPoints.Count >= 2)
            {
                var graphDirty = GraphGraphics.IsSixelFrameDirty(_sixelGraphPoints, frame);

                if (graphDirty)
                {
                    // The expensive rasterization and Sixel encoding happens before
                    // synchronized output begins: this is our actual back buffer.
                    if (GraphGraphics.TryPrepareSixelFrame(_sixelGraphPoints, frame, out var prepared))
                    {
                        GraphGraphics.BeginSynchronizedUpdate();
                        try
                        {
                            RenderOutsideGraphFrame(frame);
                            Console.ResetColor();
                            GraphGraphics.PresentSixelFrame(prepared);
                        }
                        finally
                        {
                            GraphGraphics.EndSynchronizedUpdate();
                        }
                    }
                    else
                    {
                        // Preparation failure switches the backend to Braille.
                        RenderRows(0, Height - 1);
                        Console.ResetColor();
                    }
                }
                else
                {
                    // Ordinary 250 ms dashboard updates never touch the raster
                    // rectangle. The current Sixel image simply remains displayed.
                    RenderOutsideGraphFrame(frame);
                    Console.ResetColor();
                }
            }
            else
            {
                GraphGraphics.InvalidateSixelOverlay();
                RenderRows(0, Height - 1);
                Console.ResetColor();
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            GraphGraphics.InvalidateSixelOverlay();
        }
        catch (IOException)
        {
            GraphGraphics.InvalidateSixelOverlay();
        }
    }
}
