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

        var portName = GetArg(args, "--port") ?? "COM10";
        var baudText = GetArg(args, "--baud");
        var baudRate = 57600;
        if (baudText is not null && (!int.TryParse(baudText, out baudRate) || baudRate <= 0))
        {
            Console.Error.WriteLine($"Invalid --baud value: {baudText}");
            return 2;
        }

        try
        {
            using var device = new Gmc300sDevice(portName, baudRate);
            device.Open();

            var app = new TuiApp(device);
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.ResetColor();
            Console.CursorVisible = true;
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Unable to start: {ex.Message}");
            Console.Error.WriteLine($"Requested serial port: {portName} @ {baudRate} baud");

            try
            {
                var ports = SerialPort.GetPortNames().OrderBy(x => x).ToArray();
                Console.Error.WriteLine(ports.Length == 0
                    ? "Windows reported no serial ports through SerialPort.GetPortNames()."
                    : "Ports reported by Windows: " + string.Join(", ", ports));
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
        Console.WriteLine("GMC-300S Windows TUI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  gmc300s-tui.exe [--port COM10] [--baud 57600]");
        Console.WriteLine();
        Console.WriteLine("Defaults are COM10 and 57600 baud.");
    }
}
