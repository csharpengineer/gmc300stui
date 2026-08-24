using System.Buffers.Binary;
using System.Globalization;

namespace Gmc300sTui.Device;

public enum ConfigValueKind
{
    Bool,
    Byte,
    SignedByte,
    UInt16BigEndian,
    UInt24BigEndian,
    UInt32BigEndian,
    Float32LittleEndian,
    BaudRateCode,
    SaveDataMode,
    ThresholdMode,
    BatteryType,
    HexBytes
}

public enum SettingSafety
{
    Normal,
    Caution,
    Expert,
    ReadOnly
}

public sealed record ConfigSetting(
    string Name,
    int Offset,
    int Length,
    ConfigValueKind Kind,
    string Description,
    SettingSafety Safety = SettingSafety.Normal,
    string? DirectCommand = null);

public static class ConfigSettings
{
    // The GMC config layout is not formally specified by GQ. These offsets are a
    // best-effort map assembled from GQ support postings and the PyGMC project.
    // The raw-config screen always remains available so firmware differences are visible.
    public static readonly IReadOnlyList<ConfigSetting> All = new List<ConfigSetting>
    {
        new("Power state",                 0, 1, ConfigValueKind.Byte, "Reported config power flag (0=on, 1=off). Use Advanced screen for power commands.", SettingSafety.ReadOnly),
        new("Alarm enabled",               1, 1, ConfigValueKind.Bool, "Audible alarm enable.", SettingSafety.Normal, "ALARM"),
        new("Speaker / clicks",            2, 1, ConfigValueKind.Bool, "Geiger click speaker. Direct SPEAKER0/SPEAKER1 is preferred.", SettingSafety.Normal, "SPEAKER"),
        new("Graphic mode",                3, 1, ConfigValueKind.Bool, "Text/graphic display selector (firmware interpretation may vary).", SettingSafety.Caution),
        new("Backlight timeout (seconds)", 4, 1, ConfigValueKind.Byte, "LCD backlight timeout in seconds."),
        new("Idle title display mode",      5, 1, ConfigValueKind.Byte, "Idle title/display behavior (firmware-specific).", SettingSafety.Caution),
        new("Alarm CPM",                   6, 2, ConfigValueKind.UInt16BigEndian, "CPM alarm threshold."),
        new("Calibration #1 CPM",          8, 2, ConfigValueKind.UInt16BigEndian, "First CPM calibration point.", SettingSafety.Caution),
        new("Calibration #1 µSv/h",       10, 4, ConfigValueKind.Float32LittleEndian, "Dose rate for first calibration point.", SettingSafety.Caution),
        new("Calibration #2 CPM",         14, 2, ConfigValueKind.UInt16BigEndian, "Second CPM calibration point.", SettingSafety.Caution),
        new("Calibration #2 µSv/h",       16, 4, ConfigValueKind.Float32LittleEndian, "Dose rate for second calibration point.", SettingSafety.Caution),
        new("Calibration #3 CPM",         20, 2, ConfigValueKind.UInt16BigEndian, "Third CPM calibration point.", SettingSafety.Caution),
        new("Calibration #3 µSv/h",       22, 4, ConfigValueKind.Float32LittleEndian, "Dose rate for third calibration point.", SettingSafety.Caution),
        new("Idle display mode",          26, 1, ConfigValueKind.Byte, "Idle display mode selector (firmware-specific).", SettingSafety.Caution),
        new("Alarm µSv/h",                27, 4, ConfigValueKind.Float32LittleEndian, "Dose-rate alarm threshold.", SettingSafety.Caution),
        new("Alarm type",                 31, 1, ConfigValueKind.Byte, "Alarm type selector; numeric meaning is firmware-dependent.", SettingSafety.Caution),
        new("Data save mode",             32, 1, ConfigValueKind.SaveDataMode, "0=off, 1=every second/CPS, 2=every minute/CPM, 3=every hour/CPM; 4/5 are threshold modes on some firmware."),
        new("Swivel display",             33, 1, ConfigValueKind.Bool, "Automatic display swivel/orientation."),
        new("Zoom raw value",             34, 4, ConfigValueKind.UInt32BigEndian, "Four-byte zoom field. Exact encoding is firmware-dependent.", SettingSafety.Expert),
        new("History save address",       38, 3, ConfigValueKind.UInt24BigEndian, "Internal flash write pointer.", SettingSafety.ReadOnly),
        new("History read address",       41, 3, ConfigValueKind.UInt24BigEndian, "Internal flash read pointer.", SettingSafety.ReadOnly),
        new("Power saving mode",          44, 1, ConfigValueKind.Byte, "Power-saving mode selector."),
        new("Sensitivity mode",           45, 1, ConfigValueKind.Byte, "Sensitivity mode selector (firmware-specific).", SettingSafety.Caution),
        new("Counter delay",              46, 2, ConfigValueKind.UInt16BigEndian, "Counter/display delay field (firmware-specific).", SettingSafety.Caution),
        new("Display contrast",           48, 1, ConfigValueKind.Byte, "LCD contrast value."),
        new("Maximum CPM",                49, 2, ConfigValueKind.UInt16BigEndian, "Device maximum CPM field.", SettingSafety.ReadOnly),
        new("Sensitivity auto threshold", 51, 1, ConfigValueKind.Byte, "Auto-sensitivity threshold (firmware-specific).", SettingSafety.Caution),
        new("Large font mode",            52, 1, ConfigValueKind.Bool, "Large-font display mode."),
        new("LCD backlight level",        53, 1, ConfigValueKind.Byte, "Backlight brightness/level."),
        new("Reverse display",            54, 1, ConfigValueKind.Bool, "Reverse LCD display mode."),
        new("Motion detect",              55, 1, ConfigValueKind.Bool, "Motion/orientation detection flag.", SettingSafety.Caution),
        new("Battery type",               56, 1, ConfigValueKind.BatteryType, "0=rechargeable, 1=non-rechargeable."),
        new("Serial baud rate",           57, 1, ConfigValueKind.BaudRateCode, "USB serial baud rate. The app reconnects after changing it.", SettingSafety.Caution),
        new("CPM speaker calibration",    58, 1, ConfigValueKind.Byte, "Speaker calibration/behavior byte (firmware-specific).", SettingSafety.Expert),
        new("Graphic drawing mode",       59, 1, ConfigValueKind.Byte, "Graphic rendering selector (firmware-specific).", SettingSafety.Caution),
        new("LED enabled",                60, 1, ConfigValueKind.Bool, "Pulse/activity LED enable."),
        new("High-CPM calibration",       61, 1, ConfigValueKind.Byte, "High-CPM calibration field (firmware-specific).", SettingSafety.Expert),
        new("Save threshold CPM",         62, 2, ConfigValueKind.UInt16BigEndian, "CPM threshold used by threshold-based data saving."),
        new("Threshold mode",             64, 1, ConfigValueKind.ThresholdMode, "0=CPM, 1=µSv/h, 2=mR/h."),
        new("Save threshold µSv/h",       65, 4, ConfigValueKind.Float32LittleEndian, "Dose threshold used by threshold-based data saving.", SettingSafety.Caution),
        new("Fast estimate time",         69, 1, ConfigValueKind.Byte, "Fast-estimate time field; common values are 3,5,10,15,20,30,60 seconds.", SettingSafety.Caution),
        new("RTC offset",                 70, 1, ConfigValueKind.SignedByte, "Real-time-clock correction offset.", SettingSafety.Caution),
        new("Alarm volume",               71, 1, ConfigValueKind.Byte, "Alarm volume field."),
        new("Saved date/time raw",        72, 6, ConfigValueKind.HexBytes, "Six firmware-maintained date/time bytes seen on S-series layouts.", SettingSafety.ReadOnly)
    };

    public static string FormatValue(ConfigSetting setting, byte[] config)
    {
        if (setting.Offset < 0 || setting.Offset + setting.Length > config.Length)
            return "<out of range>";

        var span = config.AsSpan(setting.Offset, setting.Length);
        return setting.Kind switch
        {
            ConfigValueKind.Bool => span[0] == 0 ? "Off (0)" : span[0] == 1 ? "On (1)" : $"Raw {span[0]}",
            ConfigValueKind.Byte => span[0].ToString(CultureInfo.InvariantCulture),
            ConfigValueKind.SignedByte => unchecked((sbyte)span[0]).ToString(CultureInfo.InvariantCulture),
            ConfigValueKind.UInt16BigEndian => BinaryPrimitives.ReadUInt16BigEndian(span).ToString(CultureInfo.InvariantCulture),
            ConfigValueKind.UInt24BigEndian => (((uint)span[0] << 16) | ((uint)span[1] << 8) | span[2]).ToString(CultureInfo.InvariantCulture),
            ConfigValueKind.UInt32BigEndian => BinaryPrimitives.ReadUInt32BigEndian(span).ToString(CultureInfo.InvariantCulture),
            ConfigValueKind.Float32LittleEndian => ReadFloat32LittleEndian(span).ToString("0.######", CultureInfo.InvariantCulture),
            ConfigValueKind.BaudRateCode => FormatBaud(span[0]),
            ConfigValueKind.SaveDataMode => FormatSaveMode(span[0]),
            ConfigValueKind.ThresholdMode => span[0] switch { 0 => "CPM (0)", 1 => "µSv/h (1)", 2 => "mR/h (2)", _ => $"Unknown ({span[0]})" },
            ConfigValueKind.BatteryType => span[0] switch { 0 => "Rechargeable (0)", 1 => "Non-rechargeable (1)", _ => $"Unknown ({span[0]})" },
            ConfigValueKind.HexBytes => Convert.ToHexString(span),
            _ => Convert.ToHexString(span)
        };
    }

    public static byte[] ParseValue(ConfigSetting setting, string input)
    {
        input = input.Trim();
        return setting.Kind switch
        {
            ConfigValueKind.Bool => new[] { ParseBool(input) },
            ConfigValueKind.Byte => new[] { byte.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            ConfigValueKind.SignedByte => new[] { unchecked((byte)sbyte.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture)) },
            ConfigValueKind.UInt16BigEndian => EncodeUInt16(ushort.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            ConfigValueKind.UInt24BigEndian => EncodeUInt24(uint.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            ConfigValueKind.UInt32BigEndian => EncodeUInt32(uint.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            ConfigValueKind.Float32LittleEndian => EncodeFloat32(float.Parse(input, NumberStyles.Float, CultureInfo.InvariantCulture)),
            ConfigValueKind.BaudRateCode => new[] { EncodeBaud(int.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture)) },
            ConfigValueKind.SaveDataMode => new[] { ParseByteRange(input, 0, 5) },
            ConfigValueKind.ThresholdMode => new[] { ParseByteRange(input, 0, 2) },
            ConfigValueKind.BatteryType => new[] { ParseByteRange(input, 0, 1) },
            ConfigValueKind.HexBytes => ParseHex(input, setting.Length),
            _ => throw new InvalidOperationException("Unsupported setting type.")
        };
    }

    public static int? DecodeBaud(byte code) => code switch
    {
        64 => 1200,
        160 => 2400,
        208 => 4800,
        232 => 9600,
        240 => 14400,
        244 => 19200,
        248 => 28800,
        250 => 38400,
        252 => 57600,
        254 => 115200,
        _ => null
    };

    public static byte EncodeBaud(int baud) => baud switch
    {
        1200 => 64,
        2400 => 160,
        4800 => 208,
        9600 => 232,
        14400 => 240,
        19200 => 244,
        28800 => 248,
        38400 => 250,
        57600 => 252,
        115200 => 254,
        _ => throw new ArgumentOutOfRangeException(nameof(baud), "Supported values: 1200, 2400, 4800, 9600, 14400, 19200, 28800, 38400, 57600, 115200.")
    };

    public static bool TryComputeDoseRate(byte[] config, int cpm, out double uSvPerHour)
    {
        uSvPerHour = 0;
        if (config.Length < 26)
            return false;

        try
        {
            var points = new List<(double Cpm, double Dose)>
            {
                (0, 0),
                (BinaryPrimitives.ReadUInt16BigEndian(config.AsSpan(8, 2)), ReadFloat32LittleEndian(config.AsSpan(10, 4))),
                (BinaryPrimitives.ReadUInt16BigEndian(config.AsSpan(14, 2)), ReadFloat32LittleEndian(config.AsSpan(16, 4))),
                (BinaryPrimitives.ReadUInt16BigEndian(config.AsSpan(20, 2)), ReadFloat32LittleEndian(config.AsSpan(22, 4)))
            };

            var segments = new List<(double MaxCpm, double Slope, double Intercept)>();
            for (var i = 0; i < points.Count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                if (b.Cpm <= a.Cpm || !double.IsFinite(a.Dose) || !double.IsFinite(b.Dose))
                    continue;

                var slope = (b.Dose - a.Dose) / (b.Cpm - a.Cpm);
                var intercept = b.Dose - slope * b.Cpm;
                segments.Add((b.Cpm, slope, intercept));
            }

            if (segments.Count == 0)
                return false;

            var selected = segments.FirstOrDefault(s => cpm <= s.MaxCpm);
            if (selected == default)
                selected = segments[^1];

            uSvPerHour = selected.Slope * cpm + selected.Intercept;
            return double.IsFinite(uSvPerHour);
        }
        catch
        {
            return false;
        }
    }

    private static float ReadFloat32LittleEndian(ReadOnlySpan<byte> span)
    {
        var bits = BinaryPrimitives.ReadInt32LittleEndian(span);
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static byte[] EncodeFloat32(float value)
    {
        if (!float.IsFinite(value))
            throw new FormatException("Value must be a finite floating-point number.");
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
        return bytes;
    }

    private static byte[] EncodeUInt16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] EncodeUInt24(uint value)
    {
        if (value > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be <= 16777215.");
        return new[] { (byte)(value >> 16), (byte)(value >> 8), (byte)value };
    }

    private static byte[] EncodeUInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte ParseBool(string input)
    {
        return input.ToLowerInvariant() switch
        {
            "0" or "off" or "false" or "no" => 0,
            "1" or "on" or "true" or "yes" => 1,
            _ => throw new FormatException("Enter on/off, true/false, or 1/0.")
        };
    }

    private static byte ParseByteRange(string input, byte min, byte max)
    {
        var value = byte.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(nameof(input), $"Value must be between {min} and {max}.");
        return value;
    }

    private static byte[] ParseHex(string input, int expectedLength)
    {
        input = input.Replace(" ", "").Replace("-", "");
        var bytes = Convert.FromHexString(input);
        if (bytes.Length != expectedLength)
            throw new FormatException($"Expected exactly {expectedLength} bytes ({expectedLength * 2} hex characters)." );
        return bytes;
    }

    private static string FormatBaud(byte code)
    {
        var baud = DecodeBaud(code);
        return baud is null ? $"Unknown code {code}" : $"{baud} baud (code {code})";
    }

    private static string FormatSaveMode(byte value) => value switch
    {
        0 => "Off (0)",
        1 => "Every second / CPS (1)",
        2 => "Every minute / CPM (2)",
        3 => "Every hour / CPM (3)",
        4 => "Every second / CPS threshold (4, firmware-dependent)",
        5 => "Every minute / CPM threshold (5, firmware-dependent)",
        _ => $"Unknown ({value})"
    };
}
