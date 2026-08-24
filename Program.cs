using System.IO.Ports;
using System.Text;
using Gmc300sTui.Device;
using Gmc300sTui.Ui;

namespace Gmc300sTui;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintUsage();
            return 0;
        }

        var defaultPort = OperatingSystem.IsWindows() ? "COM10" : "/dev/ttyUSB0";
        var portName = GetArg(args, "--port") ?? defaultPort;
        var baudText = GetArg(args, "--baud");
        var baudRate = 57600;
        if (baudText is not null && (!int.TryParse(baudText, out baudRate) || baudRate <= 0))
        {
            Console.Error.WriteLine($"Invalid --baud value: {baudText}");
            return 2;
        }

        var classic = args.Any(a => string.Equals(a, "--classic", StringComparison.OrdinalIgnoreCase));
        var json = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));
        var graphicsText = GetArg(args, "--graphics") ?? "auto";
        if (!GraphGraphics.TryConfigure(graphicsText, out var graphicsError))
        {
            Console.Error.WriteLine(graphicsError);
            return 2;
        }

        if (json && classic)
        {
            Console.Error.WriteLine("--json and --classic are mutually exclusive.");
            return 2;
        }

        try
        {
            using var device = new Gmc300sDevice(portName, baudRate);
            device.Open();

            if (json)
                new JsonLineRunner(device).Run();
            else if (classic)
                new TuiApp(device).Run();
            else
                new ResponsiveTuiApp(device).Run();

            return 0;
        }
        catch (Exception ex)
        {
            Console.ResetColor();
            try { Console.CursorVisible = true; } catch { }
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Unable to start: {ex.Message}");
            Console.Error.WriteLine($"Requested serial port: {portName} @ {baudRate} baud");

            try
            {
                var ports = SerialPort.GetPortNames().OrderBy(x => x).ToArray();
                Console.Error.WriteLine(ports.Length == 0
                    ? "SerialPort.GetPortNames() reported no serial ports."
                    : "Serial ports reported by the OS: " + string.Join(", ", ports));
            }
            catch
            {
                // Discovery is informational only; never mask the original failure.
            }

            return 1;
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("GMC-300S TUI / collector");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  gmc300s-tui [--port PORT] [--baud 57600] [--graphics auto|sixel|braille]");
        Console.WriteLine("  gmc300s-tui [--port PORT] [--baud 57600] --json");
        Console.WriteLine("  gmc300s-tui [--port PORT] [--baud 57600] --classic");
        Console.WriteLine();
        Console.WriteLine("Defaults: 57600 baud; COM10 on Windows, /dev/ttyUSB0 on Linux.");
        Console.WriteLine("--graphics auto prefers Sixel on a supported terminal and falls back to Braille.");
        Console.WriteLine("--json writes one compact JSON object per sample to stdout (JSON Lines/JSONL).");
        Console.WriteLine("Diagnostics in --json mode go to stderr so stdout remains machine-readable.");
        Console.WriteLine("--classic runs the original compact UI.");
    }
}
