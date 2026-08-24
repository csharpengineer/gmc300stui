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

internal readonly record struct SixelGraphFrame(
    int PlotX,
    int PlotY,
    int PlotWidth,
    int PlotHeight,
    int ScaleMin,
    int ScaleMax,
    double Average);

internal readonly record struct PreparedSixelFrame(
    string Payload,
    int Row,
    int Column,
    ulong Signature);

/// <summary>
/// Chooses the highest-quality graph backend the current terminal can support.
/// For Sixel, exact terminal cell dimensions matter: a one-pixel-per-cell error
/// accumulates across a wide graph and makes the apparent right edge drift.
/// Modern terminals can report the cell size through XTWINOPS CSI 16 t; the
/// legacy Win32 console-font API remains a fallback for traditional hosts.
///
/// Sixel frames are built completely in memory before synchronized output begins.
/// The prepared image is opaque and includes the graph background, grid, average
/// line, trace, and current-point marker. Presenting it therefore behaves much
/// like swapping a graphics back buffer: no blank/cleared graph frame is needed.
/// </summary>
internal static class GraphGraphics
{
    private static GraphGraphicsMode _requested = GraphGraphicsMode.Auto;
    private static GraphGraphicsMode? _resolved;
    private static (int Width, int Height)? _cellSize;
    private static bool _sixelFailed;
    private static ulong? _lastSixelSignature;
    private static (byte R, byte G, byte B) _terminalBackground = (0, 0, 0);
    private static DateTime _lastBackgroundProbeUtc = DateTime.MinValue;
    private static readonly TimeSpan BackgroundProbeInterval = TimeSpan.FromSeconds(30);

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
        _terminalBackground = (0, 0, 0);
        _lastBackgroundProbeUtc = DateTime.MinValue;
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
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_SESSION")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_PROFILE_ID")))
            return true;

        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? string.Empty;
        if (termProgram.Equals("Windows_Terminal", StringComparison.OrdinalIgnoreCase) ||
            termProgram.Equals("WezTerm", StringComparison.OrdinalIgnoreCase))
            return true;

        var term = Environment.GetEnvironmentVariable("TERM") ?? string.Empty;
        return term.Contains("sixel", StringComparison.OrdinalIgnoreCase);
    }

    private static (int Width, int Height) DetectCellSize()
    {
        if (TryQueryTerminalCellSize(out var terminalSize))
            return terminalSize;

        if (TryGetConsoleCellSize(out var consoleSize))
            return consoleSize;

        return (8, 16);
    }

    private static (int Width, int Height) EffectiveCellSize() =>
        _cellSize ??= DetectCellSize();

    private static bool TryQueryTerminalCellSize(out (int Width, int Height) size)
    {
        size = default;
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return false;

        try
        {
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

    public static bool IsSixelFrameDirty(IReadOnlyList<SixelGraphPoint> points, SixelGraphFrame frame)
    {
        if (!UseSixel || points.Count < 2 || frame.PlotWidth < 1 || frame.PlotHeight < 1)
            return false;

        if (RefreshTerminalBackgroundIfDue())
            _lastSixelSignature = null;

        return _lastSixelSignature != ComputeSixelSignature(points, frame);
    }

    private static bool RefreshTerminalBackgroundIfDue()
    {
        if (!UseSixel || Console.IsInputRedirected || Console.IsOutputRedirected)
            return false;

        var now = DateTime.UtcNow;
        if (now - _lastBackgroundProbeUtc < BackgroundProbeInterval)
            return false;

        // Set the attempt time before querying so an unsupported terminal costs at
        // most one short timeout every 30 seconds rather than every render frame.
        _lastBackgroundProbeUtc = now;
        if (!TryQueryTerminalBackground(out var background))
            return false;

        if (background == _terminalBackground)
            return false;

        _terminalBackground = background;
        return true;
    }

    private static bool TryQueryTerminalBackground(out (byte R, byte G, byte B) color)
    {
        color = default;
        try
        {
            // OSC 11 asks for the terminal's default background color. A typical
            // reply is ESC ] 11 ; rgb:0c0c/0c0c/0c0c ESC \\ .
            if (Console.KeyAvailable)
                return false;

            Console.Write("\u001b]11;?\u0007");
            Console.Out.Flush();

            var response = new StringBuilder(96);
            var stopwatch = Stopwatch.StartNew();
            var started = false;
            var previous = '\0';

            while (stopwatch.ElapsedMilliseconds < 140)
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
                if (ch == '\u0007' || (previous == '\u001b' && ch == '\\') || response.Length >= 95)
                    break;
                previous = ch;
            }

            return TryParseOsc11Background(response.ToString(), out color);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseOsc11Background(string response, out (byte R, byte G, byte B) color)
    {
        color = default;
        const string marker = "]11;rgb:";
        var markerAt = response.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerAt < 0)
            return false;

        var start = markerAt + marker.Length;
        var end = response.IndexOf('\u0007', start);
        var st = response.IndexOf("\u001b\\", start, StringComparison.Ordinal);
        if (end < 0 || (st >= 0 && st < end)) end = st;
        if (end < 0) end = response.Length;

        var parts = response[start..end].Split('/');
        if (parts.Length != 3 ||
            !TryParseOscColorComponent(parts[0], out var r) ||
            !TryParseOscColorComponent(parts[1], out var g) ||
            !TryParseOscColorComponent(parts[2], out var b))
            return false;

        color = (r, g, b);
        return true;
    }

    private static bool TryParseOscColorComponent(string hex, out byte value)
    {
        value = 0;
        hex = hex.Trim();
        if (hex.Length is < 1 or > 4 ||
            !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;

        var max = (1u << (hex.Length * 4)) - 1u;
        value = (byte)Math.Round(parsed * 255.0 / max, MidpointRounding.AwayFromZero);
        return true;
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

    /// <summary>
    /// Build and encode the complete next graph frame in memory. Nothing is sent
    /// to the terminal until this method has successfully produced the payload.
    /// </summary>
    public static bool TryPrepareSixelFrame(
        IReadOnlyList<SixelGraphPoint> points,
        SixelGraphFrame frame,
        out PreparedSixelFrame prepared)
    {
        prepared = default;
        if (!UseSixel || points.Count < 2 || frame.PlotWidth < 1 || frame.PlotHeight < 1)
            return false;

        try
        {
            RefreshTerminalBackgroundIfDue();
            var signature = ComputeSixelSignature(points, frame);
            var cellSize = EffectiveCellSize();
            var cellW = Math.Clamp(cellSize.Width, 4, 32);
            var cellH = Math.Clamp(cellSize.Height, 8, 64);
            var pixelWidth = Math.Max(1, frame.PlotWidth * cellW);
            var pixelHeight = Math.Max(1, frame.PlotHeight * cellH);

            // Palette indexes:
            // 0 terminal default background, 1 cyan trace, 2 yellow current point,
            // 3 dark-gray scale grid, 4 dark-yellow average line.
            var pixels = new byte[pixelHeight, pixelWidth];

            var topGridY = CellRowToPixel(0, cellH, pixelHeight);
            var midGridY = CellRowToPixel(frame.PlotHeight / 2, cellH, pixelHeight);
            var bottomGridY = CellRowToPixel(frame.PlotHeight - 1, cellH, pixelHeight);
            DrawDottedHorizontal(pixels, topGridY, Math.Max(2, cellW * 2), 3);
            DrawDottedHorizontal(pixels, midGridY, Math.Max(2, cellW * 2), 3);
            DrawDottedHorizontal(pixels, bottomGridY, Math.Max(2, cellW * 2), 3);

            var averageFraction = frame.ScaleMax <= frame.ScaleMin
                ? 0.5
                : (frame.Average - frame.ScaleMin) / (frame.ScaleMax - frame.ScaleMin);
            averageFraction = Math.Clamp(averageFraction, 0.0, 1.0);
            var averageY = pixelHeight - 1 - (int)Math.Round(averageFraction * (pixelHeight - 1));
            DrawDottedHorizontal(pixels, averageY, Math.Max(2, cellW * 2), 4);

            (int X, int Y) ToPixel(SixelGraphPoint point)
            {
                var localSubX = point.SubX - frame.PlotX * 2;
                var localSubY = point.SubY - frame.PlotY * 4;
                var px = (int)Math.Round((localSubX + 0.5) * cellW / 2.0);
                var py = (int)Math.Round((localSubY + 0.5) * cellH / 4.0);
                return (
                    Math.Clamp(px, 0, pixelWidth - 1),
                    Math.Clamp(py, 0, pixelHeight - 1));
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

            var payload = EncodeOpaqueSixel(pixels, _terminalBackground);
            if (payload.Length == 0)
                return false;

            prepared = new PreparedSixelFrame(
                payload,
                frame.PlotY + 1,
                frame.PlotX + 1,
                signature);
            return true;
        }
        catch
        {
            _sixelFailed = true;
            _lastSixelSignature = null;
            return false;
        }
    }

    /// <summary>
    /// Present a frame that has already been rasterized and Sixel-encoded.
    /// Call this inside a synchronized-output block for swap-buffer-like behavior.
    /// </summary>
    public static void PresentSixelFrame(PreparedSixelFrame prepared)
    {
        if (!UseSixel || string.IsNullOrEmpty(prepared.Payload))
            return;

        try
        {
            Console.Write("\u001b7");
            Console.Write($"\u001b[{prepared.Row};{prepared.Column}H");
            Console.Write(prepared.Payload);
            Console.Write("\u001b8");
            EraseReservedTerminalMargins();
            _lastSixelSignature = prepared.Signature;
        }
        catch
        {
            _sixelFailed = true;
            _lastSixelSignature = null;
        }
    }

    private static int CellRowToPixel(int row, int cellH, int pixelHeight) =>
        Math.Clamp(row * cellH + Math.Max(0, cellH / 2), 0, pixelHeight - 1);

    private static void DrawDottedHorizontal(byte[,] pixels, int y, int step, byte color)
    {
        var width = pixels.GetLength(1);
        for (var x = 0; x < width; x += Math.Max(1, step))
            SetPixel(pixels, x, y, color);
    }

    private static void EraseReservedTerminalMargins()
    {
        if (Console.IsOutputRedirected)
            return;

        try
        {
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            if (width < 1 || height < 1)
                return;

            Console.Write("\u001b7");
            for (var row = 1; row < height; row++)
                Console.Write($"\u001b[{row};{width}H\u001b[1X");
            Console.Write($"\u001b[{height};1H\u001b[2K");
            Console.Write("\u001b8");
        }
        catch
        {
            // Resize races are harmless; the next successful graph update retries.
        }
    }

    private static ulong ComputeSixelSignature(IReadOnlyList<SixelGraphPoint> points, SixelGraphFrame frame)
    {
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
        Mix(frame.PlotX);
        Mix(frame.PlotY);
        Mix(frame.PlotWidth);
        Mix(frame.PlotHeight);
        Mix(frame.ScaleMin);
        Mix(frame.ScaleMax);
        Mix(BitConverter.DoubleToInt64Bits(frame.Average).GetHashCode());
        Mix(cell.Width);
        Mix(cell.Height);
        Mix(_terminalBackground.R);
        Mix(_terminalBackground.G);
        Mix(_terminalBackground.B);
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

    private static string EncodeOpaqueSixel(byte[,] pixels, (byte R, byte G, byte B) background)
    {
        var height = pixels.GetLength(0);
        var width = pixels.GetLength(1);
        if (height == 0 || width == 0)
            return string.Empty;

        var sb = new StringBuilder(Math.Min(width * Math.Max(1, height / 2), 2_000_000));
        sb.Append("\u001bP0;0;0q");             // P2=0: opaque replacement
        sb.Append('"').Append("1;1;").Append(width).Append(';').Append(height);
        sb.Append("#0;2;")
            .Append(ToSixelPercent(background.R)).Append(';')
            .Append(ToSixelPercent(background.G)).Append(';')
            .Append(ToSixelPercent(background.B)); // terminal background (OSC 11)
        sb.Append("#1;2;35;85;85");            // cyan trace
        sb.Append("#2;2;100;96;62");           // yellow current point
        sb.Append("#3;2;30;32;34");            // dark gray grid
        sb.Append("#4;2;70;60;0");              // dark yellow average

        for (var bandY = 0; bandY < height; bandY += 6)
        {
            // Explicitly paint the complete background for this sixel band. This
            // makes replacement independent of transparency behavior in the host.
            byte backgroundMask = 0;
            for (var bit = 0; bit < 6 && bandY + bit < height; bit++)
                backgroundMask |= (byte)(1 << bit);
            sb.Append("#0");
            AppendRleSixels(sb, Enumerable.Repeat(backgroundMask, width).ToArray(), width);

            foreach (byte color in new byte[] { 3, 4, 1, 2 })
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

                sb.Append('$').Append('#').Append(color);
                AppendRleSixels(sb, masks, lastNonZero + 1);
            }

            if (bandY + 6 < height)
                sb.Append('-');
        }

        sb.Append("\u001b\\");
        return sb.ToString();
    }

    private static int ToSixelPercent(byte value) =>
        (int)Math.Round(value * 100.0 / 255.0, MidpointRounding.AwayFromZero);

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
