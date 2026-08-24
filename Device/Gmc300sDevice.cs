using System.Buffers.Binary;
using System.IO.Ports;
using System.Text;

namespace Gmc300sTui.Device;

public sealed class Gmc300sDevice : IDisposable
{
    private readonly object _ioLock = new();
    private SerialPort _port;
    private bool _disposed;

    public Gmc300sDevice(string portName, int baudRate)
    {
        _port = CreatePort(portName, baudRate);
    }

    public string PortName => _port.PortName;
    public int BaudRate => _port.BaudRate;
    public bool IsOpen => _port.IsOpen;

    public void Open()
    {
        lock (_ioLock)
        {
            ThrowIfDisposed();
            if (_port.IsOpen)
                return;

            _port.Open();
            Thread.Sleep(150);
            DisableHeartbeatUnsafe();
        }
    }

    public void Close()
    {
        lock (_ioLock)
        {
            if (_port.IsOpen)
                _port.Close();
        }
    }

    public string GetVersion()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<GETVER>>");
            // RFC1201 says 14 bytes; some 300S revisions are picky, so accept at
            // least seven and drain anything else that arrives shortly after.
            var bytes = ReadAtLeastUnsafe(7, 32, 120);
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0', '\r', '\n', ' ');
        }
    }

    public string GetSerialNumber()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<GETSERIAL>>");
            var bytes = ReadExactUnsafe(7);
            return Convert.ToHexString(bytes);
        }
    }

    public int GetCpm()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<GETCPM>>");
            var data = ReadExactUnsafe(2);
            return BinaryPrimitives.ReadUInt16BigEndian(data);
        }
    }

    public int GetCpsViaHeartbeatSample()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<HEARTBEAT1>>");
            try
            {
                var data = ReadExactUnsafe(2);
                return BinaryPrimitives.ReadUInt16BigEndian(data) & 0x3FFF;
            }
            finally
            {
                WriteAsciiUnsafe("<HEARTBEAT0>>");
                Thread.Sleep(30);
                _port.DiscardInBuffer();
            }
        }
    }

    public double GetVoltage()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<GETVOLT>>");
            return ReadExactUnsafe(1)[0] / 10.0;
        }
    }

    public DateTime GetDateTime()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<GETDATETIME>>");
            var data = ReadExactUnsafe(7);
            if (data[6] != 0xAA)
                throw new IOException($"GETDATETIME returned unexpected terminator 0x{data[6]:X2}.");

            return new DateTime(2000 + data[0], data[1], data[2], data[3], data[4], data[5], DateTimeKind.Unspecified);
        }
    }

    public double GetTemperatureCelsius()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<GETTEMP>>");
            var data = ReadExactUnsafe(4);
            if (data[3] != 0xAA)
                throw new IOException($"GETTEMP returned unexpected terminator 0x{data[3]:X2}.");

            var value = double.Parse($"{data[0]}.{data[1]}", System.Globalization.CultureInfo.InvariantCulture);
            return data[2] == 0 ? value : -value;
        }
    }

    public (short X, short Y, short Z) GetGyro()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<GETGYRO>>");
            var data = ReadExactUnsafe(7);
            if (data[6] != 0xAA)
                throw new IOException($"GETGYRO returned unexpected terminator 0x{data[6]:X2}.");

            return (
                BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(0, 2)),
                BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(2, 2)),
                BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(4, 2)));
        }
    }

    public byte[] GetConfig()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<GETCFG>>");
            return ReadExactUnsafe(256);
        }
    }

    public void SetSpeaker(bool enabled) => SendAckAscii(enabled ? "<SPEAKER1>>" : "<SPEAKER0>>");

    public void SetAlarm(bool enabled) => SendAckAscii(enabled ? "<ALARM1>>" : "<ALARM0>>");

    public void SendKey(int key)
    {
        if (key is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(key));
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe($"<KEY{key}>>");
        }
    }

    public void SetDateTime(DateTime value)
    {
        if (value.Year is < 2000 or > 2255)
            throw new ArgumentOutOfRangeException(nameof(value), "Device year must be between 2000 and 2255.");

        var payload = new[]
        {
            (byte)(value.Year - 2000), (byte)value.Month, (byte)value.Day,
            (byte)value.Hour, (byte)value.Minute, (byte)value.Second
        };

        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteBinaryCommandUnsafe("SETDATETIME", payload);
            ExpectAckUnsafe("SETDATETIME");
        }
    }

    public void UpdateConfigSetting(ConfigSetting setting, byte[] value, string backupDirectory)
    {
        if (setting.Safety == SettingSafety.ReadOnly)
            throw new InvalidOperationException($"{setting.Name} is read-only in this application.");
        if (value.Length != setting.Length)
            throw new ArgumentException("Value length does not match setting length.", nameof(value));

        // Prefer direct commands for settings where the firmware exposes them.
        if (setting.DirectCommand == "SPEAKER")
        {
            SetSpeaker(value[0] != 0);
            return;
        }
        if (setting.DirectCommand == "ALARM")
        {
            SetAlarm(value[0] != 0);
            return;
        }

        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            var current = GetConfigUnsafe();
            BackupConfig(current, backupDirectory, "pre-write");

            for (var i = 0; i < value.Length; i++)
            {
                WriteConfigByteUnsafe(setting.Offset + i, value[i]);
            }

            WriteAsciiUnsafe("<CFGUPDATE>>");
            ExpectAckUnsafe("CFGUPDATE");
            Thread.Sleep(100);

            var verify = GetConfigUnsafe();
            for (var i = 0; i < value.Length; i++)
            {
                if (verify[setting.Offset + i] != value[i])
                    throw new IOException($"Config verification failed at offset {setting.Offset + i}: wrote 0x{value[i]:X2}, read 0x{verify[setting.Offset + i]:X2}.");
            }
        }
    }

    public void ChangeBaudRate(ConfigSetting setting, int newBaud, string backupDirectory)
    {
        if (setting.Kind != ConfigValueKind.BaudRateCode)
            throw new ArgumentException("Setting is not a baud-rate setting.", nameof(setting));

        var code = ConfigSettings.EncodeBaud(newBaud);
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            var current = GetConfigUnsafe();
            BackupConfig(current, backupDirectory, "pre-baud-change");
            WriteConfigByteUnsafe(setting.Offset, code);
            WriteAsciiUnsafe("<CFGUPDATE>>");
            ExpectAckUnsafe("CFGUPDATE");

            var name = _port.PortName;
            _port.Close();
            _port.Dispose();
            Thread.Sleep(150);
            _port = CreatePort(name, newBaud);
            _port.Open();
            Thread.Sleep(200);
            DisableHeartbeatUnsafe();

            // A simple live query verifies that both ends agree on the new baud.
            _ = GetCpmUnsafe();
        }
    }

    public string BackupCurrentConfig(string backupDirectory, string tag = "manual")
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            return BackupConfig(GetConfigUnsafe(), backupDirectory, tag);
        }
    }

    public byte[] ReadHistory(int flashSizeBytes = 64 * 1024, int chunkSize = 4096, Action<int, int>? progress = null)
    {
        if (flashSizeBytes <= 0 || chunkSize <= 0 || chunkSize > 4096)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));

        lock (_ioLock)
        {
            var result = new List<byte>(flashSizeBytes);
            for (var address = 0; address < flashSizeBytes; address += chunkSize)
            {
                var len = Math.Min(chunkSize, flashSizeBytes - address);
                PrepareCommandUnsafe();
                var payload = new[]
                {
                    (byte)(address >> 16), (byte)(address >> 8), (byte)address,
                    (byte)(len >> 8), (byte)len
                };
                WriteBinaryCommandUnsafe("SPIR", payload);
                var block = ReadExactUnsafe(len);
                result.AddRange(block);

                // Some GMC firmware revisions emit one extra byte after SPIR. Drain it
                // so the next command remains aligned.
                Thread.Sleep(10);
                if (_port.BytesToRead > 0)
                    _port.DiscardInBuffer();

                progress?.Invoke(Math.Min(address + len, flashSizeBytes), flashSizeBytes);
                if (block.All(b => b == 0xFF))
                    break;
            }
            return result.ToArray();
        }
    }

    public void PowerOff()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<POWEROFF>>");
        }
    }

    public void PowerOn()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<POWERON>>");
        }
    }

    public void Reboot()
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe("<REBOOT>>");
        }
    }

    public void FactoryReset() => SendAckAscii("<FACTORYRESET>>");

    public void EraseConfig() => SendAckAscii("<ECFG>>");

    public void RefreshConfig() => SendAckAscii("<CFGUPDATE>>");

    public void WriteRawConfigByte(int offset, byte value, string backupDirectory)
    {
        if (offset is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(offset));

        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            var current = GetConfigUnsafe();
            BackupConfig(current, backupDirectory, "pre-raw-write");
            WriteConfigByteUnsafe(offset, value);
            WriteAsciiUnsafe("<CFGUPDATE>>");
            ExpectAckUnsafe("CFGUPDATE");
        }
    }

    private static SerialPort CreatePort(string portName, int baudRate) => new(portName, baudRate, Parity.None, 8, StopBits.One)
    {
        Handshake = Handshake.None,
        DtrEnable = false,
        RtsEnable = false,
        ReadTimeout = 1800,
        WriteTimeout = 1800,
        Encoding = Encoding.ASCII
    };

    private void SendAckAscii(string command)
    {
        lock (_ioLock)
        {
            PrepareCommandUnsafe();
            WriteAsciiUnsafe(command);
            ExpectAckUnsafe(command);
        }
    }

    private int GetCpmUnsafe()
    {
        PrepareCommandUnsafe();
        WriteAsciiUnsafe("<GETCPM>>");
        return BinaryPrimitives.ReadUInt16BigEndian(ReadExactUnsafe(2));
    }

    private byte[] GetConfigUnsafe()
    {
        PrepareCommandUnsafe();
        WriteAsciiUnsafe("<GETCFG>>");
        return ReadExactUnsafe(256);
    }

    private void WriteConfigByteUnsafe(int offset, byte value)
    {
        if (offset is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(offset));
        WriteBinaryCommandUnsafe("WCFG", new[] { (byte)offset, value });
        ExpectAckUnsafe($"WCFG[{offset}]");
    }

    private void PrepareCommandUnsafe()
    {
        if (!_port.IsOpen)
            throw new InvalidOperationException("Serial port is not open.");
        DisableHeartbeatUnsafe();
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
    }

    private void DisableHeartbeatUnsafe()
    {
        if (!_port.IsOpen)
            return;
        WriteAsciiUnsafe("<HEARTBEAT0>>");
        Thread.Sleep(20);
        _port.DiscardInBuffer();
    }

    private void WriteAsciiUnsafe(string command) => _port.Write(command);

    private void WriteBinaryCommandUnsafe(string command, ReadOnlySpan<byte> payload)
    {
        var prefix = Encoding.ASCII.GetBytes("<" + command);
        var suffix = Encoding.ASCII.GetBytes(">>");
        var bytes = new byte[prefix.Length + payload.Length + suffix.Length];
        prefix.CopyTo(bytes, 0);
        payload.CopyTo(bytes.AsSpan(prefix.Length));
        suffix.CopyTo(bytes, prefix.Length + payload.Length);
        _port.Write(bytes, 0, bytes.Length);
    }

    private void ExpectAckUnsafe(string operation)
    {
        var ack = ReadExactUnsafe(1)[0];
        if (ack != 0xAA)
            throw new IOException($"{operation} returned 0x{ack:X2}; expected 0xAA.");
    }

    private byte[] ReadExactUnsafe(int count)
    {
        var data = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var n = _port.Read(data, offset, count - offset);
            if (n <= 0)
                throw new TimeoutException($"Timed out after reading {offset} of {count} expected bytes.");
            offset += n;
        }
        return data;
    }

    private byte[] ReadAtLeastUnsafe(int minimum, int maximum, int drainWaitMs)
    {
        var result = new List<byte>(maximum);
        while (result.Count < minimum)
            result.Add((byte)_port.ReadByte());

        var deadline = DateTime.UtcNow.AddMilliseconds(drainWaitMs);
        while (result.Count < maximum && DateTime.UtcNow < deadline)
        {
            while (_port.BytesToRead > 0 && result.Count < maximum)
                result.Add((byte)_port.ReadByte());
            Thread.Sleep(5);
        }
        return result.ToArray();
    }

    private static string BackupConfig(byte[] config, string directory, string tag)
    {
        Directory.CreateDirectory(directory);
        var safeTag = string.Concat(tag.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        var stem = $"gmc300s-config-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{safeTag}";
        var bin = Path.Combine(directory, stem + ".bin");
        File.WriteAllBytes(bin, config);

        var txt = Path.Combine(directory, stem + ".txt");
        using var writer = new StreamWriter(txt, false, Encoding.UTF8);
        writer.WriteLine($"GMC configuration backup: {DateTime.Now:O}");
        writer.WriteLine();
        foreach (var setting in ConfigSettings.All)
            writer.WriteLine($"0x{setting.Offset:X2}  {setting.Name,-30} {ConfigSettings.FormatValue(setting, config)}");
        writer.WriteLine();
        writer.WriteLine("Raw 256-byte configuration:");
        for (var row = 0; row < 16; row++)
        {
            var offset = row * 16;
            writer.Write($"{offset:X2}: ");
            writer.WriteLine(string.Join(' ', config.Skip(offset).Take(16).Select(b => b.ToString("X2"))));
        }
        return bin;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Gmc300sDevice));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_ioLock)
        {
            try
            {
                if (_port.IsOpen)
                {
                    DisableHeartbeatUnsafe();
                    _port.Close();
                }
            }
            catch
            {
                // Dispose should be best effort.
            }
            _port.Dispose();
            _disposed = true;
        }
    }
}
