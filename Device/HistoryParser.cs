using System.Globalization;
using System.Text;

namespace Gmc300sTui.Device;

public sealed record HistoryRecord(
    DateTime Timestamp,
    uint Count,
    string Unit,
    string Mode,
    DateTime ReferenceTimestamp,
    string? Note);

public static class HistoryParser
{
    public static IReadOnlyList<HistoryRecord> Parse(byte[] raw)
    {
        var records = new List<HistoryRecord>();
        DateTime? current = null;
        DateTime? reference = null;
        var unit = "Unknown";
        var mode = "Unknown";
        string? pendingNote = null;
        var ffRun = 0;

        // Empty flash is 0xFF. Trim the erased tail before interpreting bytes as
        // legitimate one-byte counts; the original raw image is still preserved on disk.
        var end = raw.Length;
        while (end > 0 && raw[end - 1] == 0xFF)
            end--;

        var i = 0;
        while (i < end)
        {
            var b = raw[i];
            ffRun = b == 0xFF ? ffRun + 1 : 0;
            if (ffRun > 100)
                break;

            if (b == 0x55 && i + 1 < end)
            {
                var b2 = raw[i + 1];
                if (b2 == 0xAA && i + 2 < end)
                {
                    var marker = raw[i + 2];
                    switch (marker)
                    {
                        case 0 when i + 11 < end:
                        {
                            var data = raw.AsSpan(i + 3, 9);
                            if (TryContext(data, out var dt, out unit, out mode))
                            {
                                current = dt;
                                reference = dt;
                            }
                            i += 12;
                            continue;
                        }
                        case 1 when i + 4 < end:
                        {
                            var count = ((uint)raw[i + 3] << 8) | raw[i + 4];
                            AddCount(records, count, ref current, reference, unit, mode, ref pendingNote);
                            i += 5;
                            continue;
                        }
                        case 2 when i + 3 < end:
                        {
                            var size = raw[i + 3];
                            if (i + 4 + size <= end)
                            {
                                pendingNote = Encoding.UTF8.GetString(raw, i + 4, size);
                                i += 4 + size;
                                continue;
                            }
                            break;
                        }
                        case 3 when i + 5 < end:
                        {
                            var count = ((uint)raw[i + 3] << 16) | ((uint)raw[i + 4] << 8) | raw[i + 5];
                            AddCount(records, count, ref current, reference, unit, mode, ref pendingNote);
                            i += 6;
                            continue;
                        }
                        case 4 when i + 6 < end:
                        {
                            var count = ((uint)raw[i + 3] << 24) | ((uint)raw[i + 4] << 16) | ((uint)raw[i + 5] << 8) | raw[i + 6];
                            AddCount(records, count, ref current, reference, unit, mode, ref pendingNote);
                            i += 7;
                            continue;
                        }
                        case 5 when i + 3 < end:
                            // Tube selection marker on devices that have it; GMC-300S has one tube.
                            i += 4;
                            continue;
                    }

                    // Unknown 0x55 0xAA xx sequence: treat the bytes as normal counts.
                    AddCount(records, b, ref current, reference, unit, mode, ref pendingNote);
                    AddCount(records, b2, ref current, reference, unit, mode, ref pendingNote);
                    AddCount(records, marker, ref current, reference, unit, mode, ref pendingNote);
                    i += 3;
                    continue;
                }

                // 0x55 was a legitimate count; the following byte is too.
                AddCount(records, b, ref current, reference, unit, mode, ref pendingNote);
                AddCount(records, b2, ref current, reference, unit, mode, ref pendingNote);
                i += 2;
                continue;
            }

            AddCount(records, b, ref current, reference, unit, mode, ref pendingNote);
            i++;
        }

        return records;
    }

    public static void SaveCsv(string path, IEnumerable<HistoryRecord> records)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("timestamp,count,unit,mode,reference_timestamp,note");
        foreach (var r in records)
        {
            writer.Write(Escape(r.Timestamp.ToString("O", CultureInfo.InvariantCulture)));
            writer.Write(',');
            writer.Write(r.Count.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(Escape(r.Unit));
            writer.Write(',');
            writer.Write(Escape(r.Mode));
            writer.Write(',');
            writer.Write(Escape(r.ReferenceTimestamp.ToString("O", CultureInfo.InvariantCulture)));
            writer.Write(',');
            writer.WriteLine(Escape(r.Note ?? string.Empty));
        }
    }

    private static bool TryContext(ReadOnlySpan<byte> data, out DateTime dt, out string unit, out string mode)
    {
        dt = default;
        unit = "Unknown";
        mode = "Unknown";
        try
        {
            dt = new DateTime(2000 + data[0], data[1], data[2], data[3], data[4], data[5]);
            (unit, mode) = data[8] switch
            {
                0 => ("OFF", "off"),
                1 => ("CPS", "every second"),
                2 => ("CPM", "every minute"),
                3 => ("CPM", "every hour"),
                4 => ("CPS", "every second - threshold"),
                5 => ("CPM", "every minute - threshold"),
                _ => ("Unknown", $"unknown mode {data[8]}")
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddCount(
        ICollection<HistoryRecord> records,
        uint count,
        ref DateTime? current,
        DateTime? reference,
        string unit,
        string mode,
        ref string? pendingNote)
    {
        if (current is null || reference is null)
            return;

        if (mode.Contains("second", StringComparison.OrdinalIgnoreCase))
            current = current.Value.AddSeconds(1);
        else if (mode.Contains("minute", StringComparison.OrdinalIgnoreCase))
            current = current.Value.AddMinutes(1);
        else if (mode.Contains("hour", StringComparison.OrdinalIgnoreCase))
            current = current.Value.AddHours(1);
        else
            return;

        records.Add(new HistoryRecord(current.Value, count, unit, mode, reference.Value, pendingNote));
        pendingNote = null;
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
