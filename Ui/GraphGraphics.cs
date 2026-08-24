using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Gmc300sTui.Ui;

internal enum GraphGraphicsMode
{
    Auto,
    Sixel,
    Braille
}

internal readonly record struct SixelGraphPoint(int SubX, int SubY, ConsoleColor Color);

/// <summary>
/// Chooses the highest-quality graph backend the current terminal can support.
/// For Sixel, exact terminal cell dimensions matter: a one-pixel-per-cell error
/// accumulates across a wide graph and makes the apparent right edge drift.
/// Modern terminals can report the cell size through XTWINOPS CSI 16 t; the
/// legacy Win32 console-font API remains a fallback for traditional hosts.
/// </summary>
internal static class GraphGraphics
{
    private static GraphGraphicsMode _requested = GraphGraphicsMode.Auto;
    private static GraphGraphicsMode? _resolved;
    private static (int Width, int Height)? _cellSize;
    private static bool _sixelFailed;
    private static ulong? _lastSixelSignature;

    public static GraphGraphicsMode Requested => _requested;

    public static GraphGraphicsMode Resolved
    {
        get
        {
            if (_sixelFailed)
                return GraphGraphicsMode.Braille;
            return _resolved ??= Resolve();
        }
    }

    public static string ResolvedName => Resolved switch
    {
        GraphGraphicsMode.Sixel => "sixel",
        GraphGraphicsMode.Braille => "braille",
        _ => "braille"
    };

    public static bool UseSixel => Resolved == GraphGraphicsMode.Sixel;

    public static bool TryConfigure(string? value, out string? error)
    {
        error = null;
        var normalized = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
        _requested = normalized switch
        {
            "auto" => GraphGraphicsMode.Auto,
            "sixel" => GraphGraphicsMode.Sixel,
            "braille" => GraphGraphicsMode.Braille,
            _ => GraphGraphicsMode.Auto
        };

        if (normalized is not ("auto" or "sixel" or "braille"))
        {
            error = $"Invalid --graphics value '{value}'. Use auto, sixel, or braille.";
            return false;
        }

        _resolved = null;
        _cellSize = null;
        _sixelFailed = false;
        _lastSixelSignature = null;
        return true;
    }

    private static GraphGraphicsMode Resolve()
    {
        if (_requested == GraphGraphicsMode.Braille)
            return GraphGraphicsMode.Braille;

        if (Console.IsOutputRedirected)
            return GraphGraphicsMode.Braille;

        if (_requested == GraphGraphicsMode.Sixel)
        {
            _cellSize = DetectCellSize();
            return GraphGraphicsMode.Sixel;
        }

        if (!IsSixelLikelySupported())
            return GraphGraphicsMode.Braille;

        _cellSize = DetectCellSize();
        return GraphGraphicsMode.Sixel;
    }

    private static bool IsSixelLikelySupported()
    {
        // Windows Terminal exports WT_SESSION. WT_PROFILE_ID is included as a
        // secondary marker for hosts/profiles where WT_SESSION is not propagated.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_SESSION")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_PROFILE_ID")))
            return true;

        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? string.Empty;
        if (termProgram.Equals("Windows_Terminal", StringComparison.OrdinalIgnoreCase) ||
            termProgram.Equals("WezTerm", StringComparison.OrdinalIgnoreCase))
            return true;

        // Unix terminals and SSH sessions sometimes advertise Sixel directly.
        var term = Environment.GetEnvironmentVariable("TERM") ?? string.Empty;
        return term.Contains("sixel", StringComparison.OrdinalIgnoreCase);
    }

    private static (int Width, int Height) DetectCellSize()
    {
        // XTWINOPS CSI 16 t asks the terminal to report character-cell size in
        // pixels. Windows Terminal replies CSI 6 ; height ; width t. This is the
        // authoritative value for Sixel because Sixel pixels are device pixels.
        if (TryQueryTerminalCellSize(out var terminalSize))
            return terminalSize;

        if (TryGetConsoleCellSize(out var consoleSize))
            return consoleSize;

        // Last-resort compatibility fallback. Alignment may be imperfect on a
        // terminal with a differently sized font, but Sixel remains usable.
        return (8, 16);
    }

    private static (int Width, int Height) EffectiveCellSize() =>
        _cellSize ??= DetectCellSize();

    /// <summary>
    /// Query XTWINOPS character-cell dimensions (CSI 16 t). The response is
    /// CSI 6 ; height ; width t. We do this once while the graphics backend is
    /// being resolved, before normal key processing begins.
    /// </summary>
    private static bool TryQueryTerminalCellSize(out (int Width, int Height) size)
    {
        size = default;
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return false;

        try
        {
            // Do not risk consuming a real keystroke the user already entered.
            if (Console.KeyAvailable)
                return false;

            Console.Write("\u001b[16t");
            Console.Out.Flush();

            var response = new StringBuilder(32);
            var stopwatch = Stopwatch.StartNew();
            var started = false;

            while (stopwatch.ElapsedMilliseconds < 180)
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(2);
                    continue;
                }

                var ch = Console.ReadKey(intercept: true).KeyChar;
                if (!started)
                {
                    if (ch != '\u001b')
                        continue;
                    started = true;
                }

                response.Append(ch);
                if (ch == 't' || response.Length >= 31)
                    break;
            }

            return TryParseCellSizeResponse(response.ToString(), out size);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseCellSizeResponse(string response, out (int Width, int Height) size)
    {
        size = default;
        const string prefix = "\u001b[6;";
        var start = response.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return false;

        start += prefix.Length;
        var end = response.IndexOf('t', start);
        if (end < 0)
            return false;

        var payload = response[start..end];
        var separator = payload.IndexOf(';');
        if (separator <= 0 || separator >= payload.Length - 1)
            return false;

        if (!int.TryParse(payload[..separator], out var height) ||
            !int.TryParse(payload[(separator + 1)..], out var width) ||
            width <= 0 || height <= 0 || width > 128 || height > 256)
            return false;

        size = (width, height);
        return true;
    }

    public static void InvalidateSixelOverlay() => _lastSixelSignature = null;

    public static bool IsSixelOverlayDirty(IReadOnlyList<SixelGraphPoint> points, int topRow, int bottomRow)
    {
        if (!UseSixel || points.Count < 2 || bottomRow < topRow)
            return false;

        return _lastSixelSignature != ComputeSixelSignature(points, topRow, bottomRow);
    }

    public static void BeginSynchronizedUpdate()
    {
        if (UseSixel)
            Console.Write("\u001b[?2026h");
    }

    public static void EndSynchronizedUpdate()
    {
        if (UseSixel)
            Console.Write("\u001b[?2026l");
    }

    public static void RenderSixelOverlay(IReadOnlyList<SixelGraphPoint> points, int topRow, int bottomRow)
    {
        if (!UseSixel || points.Count < 2 || bottomRow < topRow)
            return;

        try
        {
            var signature = ComputeSixelSignature(points, topRow, bottomRow);
            var cellSize = EffectiveCellSize();
            var cellW = Math.Clamp(cellSize.Width, 4, 32);
            var cellH = Math.Clamp(cellSize.Height, 8, 64);

            var minCellX = points.Min(p => p.SubX / 2);
            var maxCellX = points.Max(p => p.SubX / 2);
            var widthCells = Math.Max(1, maxCellX - minCellX + 1);
            var heightCells = Math.Max(1, bottomRow - topRow + 1);
            var pixelWidth = Math.Max(1, widthCells * cellW);
            var pixelHeight = Math.Max(1, heightCells * cellH);

            // 0 = transparent, 1 = cyan graph, 2 = yellow latest point.
            var pixels = new byte[pixelHeight, pixelWidth];

            (int X, int Y) ToPixel(SixelGraphPoint point)
            {
                var localSubX = point.SubX - minCellX * 2;
                var localSubY = point.SubY - topRow * 4;
                var px = (int)Math.Round((localSubX + 0.5) * cellW / 2.0);
                var py = (int)Math.Round((localSubY + 0.5) * cellH / 4.0);
                return (Math.Clamp(px, 0, pixelWidth - 1), Math.Clamp(py, 0, pixelHeight - 1));
            }

            var previous = ToPixel(points[0]);
            SetPixel(pixels, previous.X, previous.Y, 1);
            for (var i = 1; i < points.Count; i++)
            {
                var current = ToPixel(points[i]);
                DrawLine(pixels, previous.X, previous.Y, current.X, current.Y, 1);
                previous = current;
            }

            var latest = ToPixel(points[^1]);
            DrawDisc(pixels, latest.X, latest.Y, Math.Max(2, cellH / 8), 2);

            var sixel = EncodeSixel(pixels);
            if (sixel.Length == 0)
                return;

            // Save/restore cursor using unambiguous ESC sequences. In C#, \x
            // escapes consume following hexadecimal digits, so "\x1b7" was
            // accidentally U+01B7 (Ʒ), which is the stray glyph seen on screen.
            Console.Write("\u001b7");
            Console.Write($"\u001b[{topRow + 1};{minCellX + 1}H");
            Console.Write(sixel);
            Console.Write("\u001b8");
            _lastSixelSignature = signature;
        }
        catch
        {
            // Rendering must never take down device monitoring. One failure turns
            // Sixel off for the rest of this process; the next frame uses Braille.
            _sixelFailed = true;
            _lastSixelSignature = null;
        }
    }

    private static ulong ComputeSixelSignature(IReadOnlyList<SixelGraphPoint> points, int topRow, int bottomRow)
    {
        // FNV-1a over geometry plus the effective raster scale. The CPM polling
        // thread changes the point list roughly once per second, while the TUI can
        // redraw four times per second; this lets unchanged Sixel images persist.
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;

        void Mix(int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= prime;
                hash ^= (uint)(value >> 16);
                hash *= prime;
            }
        }

        var cell = EffectiveCellSize();
        Mix(topRow);
        Mix(bottomRow);
        Mix(cell.Width);
        Mix(cell.Height);
        Mix(points.Count);
        foreach (var point in points)
        {
            Mix(point.SubX);
            Mix(point.SubY);
            Mix((int)point.Color);
        }

        return hash;
    }

    private static void DrawLine(byte[,] pixels, int x0, int y0, int x1, int y1, byte color)
    {
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;

        while (true)
        {
            SetPixel(pixels, x0, y0, color);
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

    private static void DrawDisc(byte[,] pixels, int centerX, int centerY, int radius, byte color)
    {
        var rr = radius * radius;
        for (var y = centerY - radius; y <= centerY + radius; y++)
        for (var x = centerX - radius; x <= centerX + radius; x++)
            if ((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) <= rr)
                SetPixel(pixels, x, y, color);
    }

    private static void SetPixel(byte[,] pixels, int x, int y, byte color)
    {
        if (y < 0 || y >= pixels.GetLength(0) || x < 0 || x >= pixels.GetLength(1))
            return;
        pixels[y, x] = color;
    }

    private static string EncodeSixel(byte[,] pixels)
    {
        var height = pixels.GetLength(0);
        var width = pixels.GetLength(1);
        if (height == 0 || width == 0)
            return string.Empty;

        var sb = new StringBuilder(Math.Min(width * Math.Max(1, height / 3), 1_000_000));
        sb.Append("\u001bP0;1;0q");             // P2=1: transparent background
        sb.Append('"').Append("1;1;").Append(width).Append(';').Append(height);
        sb.Append("#1;2;35;85;85");            // cyan
        sb.Append("#2;2;100;96;62");           // yellow

        for (var bandY = 0; bandY < height; bandY += 6)
        {
            var wroteColor = false;
            for (byte color = 1; color <= 2; color++)
            {
                var masks = new byte[width];
                var lastNonZero = -1;
                for (var x = 0; x < width; x++)
                {
                    byte mask = 0;
                    for (var bit = 0; bit < 6; bit++)
                    {
                        var y = bandY + bit;
                        if (y < height && pixels[y, x] == color)
                            mask |= (byte)(1 << bit);
                    }
                    masks[x] = mask;
                    if (mask != 0)
                        lastNonZero = x;
                }

                if (lastNonZero < 0)
                    continue;

                if (wroteColor)
                    sb.Append('$');
                sb.Append('#').Append(color);
                AppendRleSixels(sb, masks, lastNonZero + 1);
                wroteColor = true;
            }

            if (bandY + 6 < height)
                sb.Append('-');
        }

        sb.Append("\u001b\\");
        return sb.ToString();
    }

    private static void AppendRleSixels(StringBuilder sb, byte[] masks, int count)
    {
        var i = 0;
        while (i < count)
        {
            var ch = (char)(63 + masks[i]);
            var run = 1;
            while (i + run < count && masks[i + run] == masks[i])
                run++;

            if (run >= 4)
                sb.Append('!').Append(run).Append(ch);
            else
                sb.Append(ch, run);

            i += run;
        }
    }

    private static bool TryGetConsoleCellSize(out (int Width, int Height) size)
    {
        size = default;
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return false;

            var info = new ConsoleFontInfoEx
            {
                cbSize = (uint)Marshal.SizeOf<ConsoleFontInfoEx>(),
                FaceName = string.Empty
            };
            if (!GetCurrentConsoleFontEx(handle, false, ref info))
                return false;
            if (info.dwFontSize.X <= 0 || info.dwFontSize.Y <= 0)
                return false;

            size = (info.dwFontSize.X, info.dwFontSize.Y);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const int StdOutputHandle = -11;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConsoleFontInfoEx
    {
        public uint cbSize;
        public uint nFont;
        public Coord dwFontSize;
        public int FontFamily;
        public int FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref ConsoleFontInfoEx lpConsoleCurrentFontEx);
}
