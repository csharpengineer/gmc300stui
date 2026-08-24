using System.Diagnostics;
using System.Globalization;
using Gmc300sTui.Device;

namespace Gmc300sTui.Ui;

/// <summary>
/// Responsive/color UI.  The original TuiApp remains available through --classic
/// while this renderer gets exercised against real hardware and terminal sizes.
/// </summary>
public sealed class ResponsiveTuiApp
{
    private enum Screen
    {
        Dashboard,
        Settings,
        Remote,
        History,
        Info,
        Advanced,
        Help
    }

    private enum GraphMetric
    {
        Cpm,
        Dose,
        Battery
    }

    private sealed class Snapshot
    {
        public int? Cpm { get; set; }
        public int? Cps { get; set; }
        public double? Voltage { get; set; }
        public double? TemperatureC { get; set; }
        public DateTime? DeviceTime { get; set; }
        public double? ClockDriftSeconds { get; set; }
        public (short X, short Y, short Z)? Gyro { get; set; }
        public byte[]? Config { get; set; }
        public string? Version { get; set; }
        public string? Serial { get; set; }
        public string Status { get; set; } = "Connected";
        public DateTime LastCpmAt { get; set; }
        public DateTime LastSlowPollAt { get; set; }
        public DateTime LastConfigAt { get; set; }
        public Queue<int> CpmHistory { get; } = new();
        public Queue<int> VoltageHistoryMv { get; } = new();
        public DeviceCapabilities Capabilities { get; set; } = DeviceCapabilities.Unknown;
    }

    private readonly Gmc300sDevice _device;
    private readonly Snapshot _snapshot = new();
    private readonly object _snapshotLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _backupDirectory;
    private readonly string _historyDirectory;
    private Screen _screen = Screen.Dashboard;
    private GraphMetric _graphMetric = GraphMetric.Cpm;
    private int _settingIndex;
    private int _rawConfigPage;
    private string _message = string.Empty;
    private DateTime _messageUntil;
    private Task? _pollTask;

    public ResponsiveTuiApp(Gmc300sDevice device)
    {
        _device = device;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gmc300sTui");
        _backupDirectory = Path.Combine(root, "config-backups");
        _historyDirectory = Path.Combine(root, "history");
    }

    public void Run()
    {
        var oldCursor = Console.CursorVisible;
        var oldTreat = Console.TreatControlCAsInput;
        Console.CursorVisible = false;
        Console.TreatControlCAsInput = true;
        Console.Clear();

        try
        {
            InitialRead();
            _pollTask = Task.Run(() => PollLoop(_cts.Token));

            var redraw = Stopwatch.StartNew();
            var running = true;
            while (running)
            {
                if (redraw.ElapsedMilliseconds >= 250)
                {
                    Draw();
                    redraw.Restart();
                }

                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
                    {
                        running = false;
                        continue;
                    }

                    running = HandleKey(key);
                    redraw.Restart();
                    Draw();
                }
                else
                {
                    Thread.Sleep(25);
                }
            }
        }
        finally
        {
            _cts.Cancel();
            try { _pollTask?.Wait(1000); } catch { }
            Console.ResetColor();
            Console.CursorVisible = oldCursor;
            Console.TreatControlCAsInput = oldTreat;
            Console.Clear();
        }
    }

    private void InitialRead()
    {
        SetStatus("Reading device identity and configuration...");
        try
        {
            var version = _device.GetVersion();
            string serial;
            try { serial = _device.GetSerialNumber(); } catch { serial = "Unavailable"; }
            var config = _device.GetConfig();
            var cpm = _device.GetCpm();

            lock (_snapshotLock)
            {
                _snapshot.Version = version;
                _snapshot.Serial = serial;
                _snapshot.Config = config;
                _snapshot.Capabilities = DeviceCapabilities.FromVersion(version);
                _snapshot.Cpm = cpm;
                _snapshot.LastCpmAt = DateTime.Now;
                _snapshot.LastConfigAt = DateTime.Now;
                _snapshot.Status = "Connected";
            }
        }
        catch (Exception ex)
        {
            SetStatus("Initial read failed: " + ex.Message);
        }
    }

    private void PollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var cpm = _device.GetCpm();
                lock (_snapshotLock)
                {
                    _snapshot.Cpm = cpm;
                    _snapshot.LastCpmAt = DateTime.Now;
                    _snapshot.CpmHistory.Enqueue(cpm);

                    // Keep ten minutes at roughly one regular sample per second.  The
                    // graph takes only as many points as fit in the current terminal.
                    while (_snapshot.CpmHistory.Count > 600)
                        _snapshot.CpmHistory.Dequeue();

                    _snapshot.Status = "Connected";
                }
            }
            catch (Exception ex)
            {
                SetStatus("CPM poll: " + ex.Message);
            }

            var now = DateTime.Now;
            bool doSlow;
            bool doConfig;
            lock (_snapshotLock)
            {
                doSlow = (now - _snapshot.LastSlowPollAt).TotalSeconds >= 5;
                doConfig = (now - _snapshot.LastConfigAt).TotalSeconds >= 30;
                if (doSlow) _snapshot.LastSlowPollAt = now;
                if (doConfig) _snapshot.LastConfigAt = now;
            }

            if (doSlow) PollSlow();
            if (doConfig) PollConfig();

            token.WaitHandle.WaitOne(900);
        }
    }

    private void PollSlow()
    {
        DeviceCapabilities capabilities;
        lock (_snapshotLock) capabilities = _snapshot.Capabilities;

        if (capabilities.HeartbeatCpsSampling)
        {
            try
            {
                var cps = _device.GetCpsViaHeartbeatSample();
                lock (_snapshotLock) _snapshot.Cps = cps;
            }
            catch { }
        }

        try
        {
            var voltage = _device.GetVoltage();
            lock (_snapshotLock)
            {
                _snapshot.Voltage = voltage;
                _snapshot.VoltageHistoryMv.Enqueue((int)Math.Round(voltage * 1000.0));
                // One hour at the five-second slow-poll cadence.
                while (_snapshot.VoltageHistoryMv.Count > 720)
                    _snapshot.VoltageHistoryMv.Dequeue();
            }
        }
        catch { }

        try
        {
            var deviceTime = _device.GetDateTime();
            var sampledAt = DateTime.Now;
            lock (_snapshotLock)
            {
                _snapshot.DeviceTime = deviceTime;
                _snapshot.ClockDriftSeconds = (deviceTime - sampledAt).TotalSeconds;
            }
        }
        catch { }

        if (capabilities.Temperature)
            TryUpdate(() => _device.GetTemperatureCelsius(), v => _snapshot.TemperatureC = v);

        if (capabilities.Gyroscope)
            TryUpdate(() => _device.GetGyro(), v => _snapshot.Gyro = v);
    }

    private void PollConfig()
    {
        try
        {
            var config = _device.GetConfig();
            lock (_snapshotLock) _snapshot.Config = config;
        }
        catch { }
    }

    private void TryUpdate<T>(Func<T> get, Action<T> update)
    {
        try
        {
            var value = get();
            lock (_snapshotLock) update(value);
        }
        catch { }
    }

    private bool HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Q)
            return false;

        switch (key.Key)
        {
            case ConsoleKey.D: _screen = Screen.Dashboard; return true;
            case ConsoleKey.S: _screen = Screen.Settings; return true;
            case ConsoleKey.R when _screen != Screen.Settings && _screen != Screen.Info: _screen = Screen.Remote; return true;
            case ConsoleKey.H: _screen = Screen.History; return true;
            case ConsoleKey.I: _screen = Screen.Info; return true;
            case ConsoleKey.X: _screen = Screen.Advanced; return true;
            case ConsoleKey.F1: _screen = Screen.Help; return true;
            case ConsoleKey.G when _screen == Screen.Dashboard: CycleGraphMetric(); return true;
            case ConsoleKey.M: ToggleSpeaker(); return true;
            case ConsoleKey.A: ToggleAlarm(); return true;
            case ConsoleKey.T: SyncClock(); return true;
        }

        return _screen switch
        {
            Screen.Settings => HandleSettingsKey(key),
            Screen.Remote => HandleRemoteKey(key),
            Screen.History => HandleHistoryKey(key),
            Screen.Info => HandleInfoKey(key),
            Screen.Advanced => HandleAdvancedKey(key),
            _ => true
        };
    }

    private bool HandleSettingsKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _settingIndex = Math.Max(0, _settingIndex - 1);
                break;
            case ConsoleKey.DownArrow:
                _settingIndex = Math.Min(ConfigSettings.All.Count - 1, _settingIndex + 1);
                break;
            case ConsoleKey.PageUp:
                _settingIndex = Math.Max(0, _settingIndex - 10);
                break;
            case ConsoleKey.PageDown:
                _settingIndex = Math.Min(ConfigSettings.All.Count - 1, _settingIndex + 10);
                break;
            case ConsoleKey.Home:
                _settingIndex = 0;
                break;
            case ConsoleKey.End:
                _settingIndex = ConfigSettings.All.Count - 1;
                break;
            case ConsoleKey.Enter:
                EditSelectedSetting();
                break;
            case ConsoleKey.B:
                BackupConfig();
                break;
            case ConsoleKey.R:
                RefreshConfigNow();
                break;
        }
        return true;
    }

    private bool HandleRemoteKey(ConsoleKeyInfo key)
    {
        try
        {
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                case ConsoleKey.Backspace:
                case ConsoleKey.LeftArrow:
                    _device.SendKey(0); Flash("Sent S1 / Back (KEY0)"); break;
                case ConsoleKey.UpArrow:
                    _device.SendKey(1); Flash("Sent S2 / Up (KEY1)"); break;
                case ConsoleKey.DownArrow:
                    _device.SendKey(2); Flash("Sent S3 / Down (KEY2)"); break;
                case ConsoleKey.Enter:
                case ConsoleKey.RightArrow:
                    _device.SendKey(3); Flash("Sent S4 / Enter/Menu (KEY3)"); break;
                case ConsoleKey.D1:
                case ConsoleKey.NumPad1:
                    _device.SendKey(0); break;
                case ConsoleKey.D2:
                case ConsoleKey.NumPad2:
                    _device.SendKey(1); break;
                case ConsoleKey.D3:
                case ConsoleKey.NumPad3:
                    _device.SendKey(2); break;
                case ConsoleKey.D4:
                case ConsoleKey.NumPad4:
                    _device.SendKey(3); break;
            }
        }
        catch (Exception ex)
        {
            Flash("Remote command failed: " + ex.Message, 5);
        }
        return true;
    }

    private bool HandleHistoryKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.G)
            DownloadHistory();
        return true;
    }

    private bool HandleInfoKey(ConsoleKeyInfo key)
    {
        if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.PageUp)
            _rawConfigPage = Math.Max(0, _rawConfigPage - 1);
        if (key.Key is ConsoleKey.RightArrow or ConsoleKey.PageDown)
            _rawConfigPage = Math.Min(1, _rawConfigPage + 1);
        if (key.Key == ConsoleKey.R)
            RefreshConfigNow();
        return true;
    }

    private bool HandleAdvancedKey(ConsoleKeyInfo key)
    {
        try
        {
            switch (key.Key)
            {
                case ConsoleKey.D1:
                    if (Confirm("Type REBOOT to reboot the counter", "REBOOT"))
                    {
                        _device.Reboot();
                        Flash("Reboot command sent.");
                    }
                    break;
                case ConsoleKey.D2:
                    if (Confirm("Type POWER OFF to power off the counter", "POWER OFF"))
                    {
                        _device.PowerOff();
                        Flash("Power-off command sent.");
                    }
                    break;
                case ConsoleKey.D3:
                    if (Confirm("Type POWER ON to send the power-on command", "POWER ON"))
                    {
                        _device.PowerOn();
                        Flash("Power-on command sent.");
                    }
                    break;
                case ConsoleKey.D4:
                    if (Confirm("FACTORY RESET ERASES USER SETTINGS. Type FACTORY RESET", "FACTORY RESET"))
                    {
                        BackupConfig();
                        _device.FactoryReset();
                        Flash("Factory-reset command accepted.", 5);
                    }
                    break;
                case ConsoleKey.D5:
                    RawConfigWrite();
                    break;
                case ConsoleKey.D6:
                    if (Confirm("DANGEROUS: type ERASE CONFIG", "ERASE CONFIG"))
                    {
                        BackupConfig();
                        _device.EraseConfig();
                        Flash("Configuration erase command accepted.", 5);
                    }
                    break;
                case ConsoleKey.D7:
                    _device.RefreshConfig();
                    RefreshConfigNow();
                    Flash("CFGUPDATE accepted.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Flash("Advanced command failed: " + ex.Message, 6);
        }
        return true;
    }

    private void CycleGraphMetric()
    {
        _graphMetric = _graphMetric switch
        {
            GraphMetric.Cpm => GraphMetric.Dose,
            GraphMetric.Dose => GraphMetric.Battery,
            _ => GraphMetric.Cpm
        };
        GraphGraphics.InvalidateSixelOverlay();
        Flash($"Graph: {_graphMetric switch { GraphMetric.Cpm => "CPM", GraphMetric.Dose => "dose (nSv/h)", _ => "battery (mV)" }}");
    }

    private void ToggleSpeaker()
    {
        var config = GetConfigSnapshot();
        var enabled = config is not null && config.Length > 2 && config[2] != 0;
        try
        {
            _device.SetSpeaker(!enabled);
            Flash(!enabled ? "Speaker/clicks ON" : "Speaker/clicks OFF");
            Thread.Sleep(50);
            RefreshConfigNow();
        }
        catch (Exception ex)
        {
            Flash("Speaker command failed: " + ex.Message, 5);
        }
    }

    private void ToggleAlarm()
    {
        var config = GetConfigSnapshot();
        var enabled = config is not null && config.Length > 1 && config[1] != 0;
        try
        {
            _device.SetAlarm(!enabled);
            Flash(!enabled ? "Alarm ON" : "Alarm OFF");
            Thread.Sleep(50);
            RefreshConfigNow();
        }
        catch (Exception ex)
        {
            Flash("Alarm command failed: " + ex.Message, 5);
        }
    }

    private void SyncClock()
    {
        try
        {
            var now = DateTime.Now;
            _device.SetDateTime(now);
            lock (_snapshotLock)
            {
                _snapshot.DeviceTime = now;
                _snapshot.ClockDriftSeconds = 0;
            }
            Flash($"Clock synchronized to {now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Flash("Clock sync failed: " + ex.Message, 5);
        }
    }

    private void EditSelectedSetting()
    {
        var setting = ConfigSettings.All[_settingIndex];
        if (setting.Safety == SettingSafety.ReadOnly)
        {
            Flash("That setting is read-only here. Raw bytes remain visible on the Info screen.");
            return;
        }

        var config = GetConfigSnapshot();
        if (config is null)
        {
            Flash("Configuration has not been read yet.");
            return;
        }

        if (setting.Safety == SettingSafety.Expert &&
            !Confirm($"{setting.Name} is firmware-dependent. Type EXPERT to continue", "EXPERT"))
            return;

        var current = ConfigSettings.FormatValue(setting, config);
        var hint = InputHint(setting);
        var input = Prompt($"{setting.Name}\nCurrent: {current}\n{hint}\nNew value (blank cancels): ");
        if (string.IsNullOrWhiteSpace(input))
            return;

        try
        {
            var bytes = ConfigSettings.ParseValue(setting, input);
            if (setting.Kind == ConfigValueKind.BaudRateCode)
            {
                var baud = int.Parse(input, CultureInfo.InvariantCulture);
                if (!Confirm($"Changing baud can break the connection. Type {baud} to continue", baud.ToString(CultureInfo.InvariantCulture)))
                    return;
                _device.ChangeBaudRate(setting, baud, _backupDirectory);
            }
            else
            {
                _device.UpdateConfigSetting(setting, bytes, _backupDirectory);
            }

            RefreshConfigNow();
            Flash($"Updated {setting.Name}.");
        }
        catch (Exception ex)
        {
            Flash("Setting update failed: " + ex.Message, 7);
        }
    }

    private static string InputHint(ConfigSetting setting) => setting.Kind switch
    {
        ConfigValueKind.Bool => "Enter on/off or 1/0.",
        ConfigValueKind.BaudRateCode => "Enter baud: 1200,2400,4800,9600,14400,19200,28800,38400,57600,115200.",
        ConfigValueKind.SaveDataMode => "Enter 0..5 (0 off, 1 second, 2 minute, 3 hour; 4/5 threshold modes may be firmware-dependent).",
        ConfigValueKind.ThresholdMode => "Enter 0=CPM, 1=µSv/h, 2=mR/h.",
        ConfigValueKind.BatteryType => "Enter 0=rechargeable or 1=non-rechargeable.",
        ConfigValueKind.Float32LittleEndian => "Enter a decimal number using '.' as the decimal separator.",
        _ => "Enter a decimal numeric value."
    };

    private void BackupConfig()
    {
        try
        {
            var path = _device.BackupCurrentConfig(_backupDirectory);
            Flash("Config backup saved: " + path, 6);
            RefreshConfigNow();
        }
        catch (Exception ex)
        {
            Flash("Backup failed: " + ex.Message, 6);
        }
    }

    private void RefreshConfigNow()
    {
        try
        {
            var config = _device.GetConfig();
            lock (_snapshotLock)
            {
                _snapshot.Config = config;
                _snapshot.LastConfigAt = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            Flash("Config refresh failed: " + ex.Message, 5);
        }
    }

    private void RawConfigWrite()
    {
        if (!Confirm("Raw EEPROM writes can corrupt configuration. Type RAW WRITE", "RAW WRITE"))
            return;
        var offsetText = Prompt("Offset 0-255 (decimal, blank cancels): ");
        if (string.IsNullOrWhiteSpace(offsetText)) return;
        var valueText = Prompt("Byte value 0-255 (decimal): ");
        if (string.IsNullOrWhiteSpace(valueText)) return;

        var offset = int.Parse(offsetText, CultureInfo.InvariantCulture);
        var value = byte.Parse(valueText, CultureInfo.InvariantCulture);
        _device.WriteRawConfigByte(offset, value, _backupDirectory);
        RefreshConfigNow();
        Flash($"Wrote config[{offset}] = 0x{value:X2}.");
    }

    private void DownloadHistory()
    {
        Directory.CreateDirectory(_historyDirectory);
        var stem = $"gmc300s-history-{DateTime.Now:yyyyMMdd-HHmmss}";
        var rawPath = Path.Combine(_historyDirectory, stem + ".bin");
        var csvPath = Path.Combine(_historyDirectory, stem + ".csv");

        Console.Clear();
        Console.CursorVisible = false;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Downloading GMC-300S history...");
        Console.ResetColor();
        Console.WriteLine();
        try
        {
            var raw = _device.ReadHistory(progress: (done, total) =>
            {
                Console.SetCursorPosition(0, 2);
                var pct = total == 0 ? 0 : done * 100.0 / total;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"Read {done,6:N0} / {total,6:N0} bytes  ({pct,5:0.0}%)   ");
                Console.ResetColor();
            });
            File.WriteAllBytes(rawPath, raw);
            var records = HistoryParser.Parse(raw);
            HistoryParser.SaveCsv(csvPath, records);
            Flash($"History: {records.Count:N0} records. Saved {rawPath} and {csvPath}", 8);
        }
        catch (Exception ex)
        {
            Flash("History download failed: " + ex.Message, 8);
        }
        Console.ResetColor();
        Console.Clear();
    }

    private bool Confirm(string prompt, string exact)
    {
        var value = Prompt(prompt + "\n> ");
        return string.Equals(value, exact, StringComparison.Ordinal);
    }

    private string Prompt(string prompt)
    {
        Console.Clear();
        Console.CursorVisible = true;
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("GMC-300S TUI — input mode");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(new string('─', Math.Min(80, Math.Max(10, Console.WindowWidth - 1))));
        Console.ResetColor();
        Console.Write(prompt);
        var text = Console.ReadLine() ?? string.Empty;
        Console.CursorVisible = false;
        Console.Clear();
        return text;
    }

    private void Draw()
    {
        int width;
        int height;
        try
        {
            // Keep one column/row unused to avoid host-specific wrapping/scrolling at
            // the bottom-right cell.
            width = Math.Max(40, Console.WindowWidth - 1);
            height = Math.Max(16, Console.WindowHeight - 1);
        }
        catch
        {
            return;
        }

        var canvas = new ConsoleCanvas(width, height);
        var snapshot = CopySnapshot();
        DrawHeader(canvas, snapshot);

        switch (_screen)
        {
            case Screen.Dashboard: DrawDashboard(canvas, snapshot); break;
            case Screen.Settings: DrawSettings(canvas); break;
            case Screen.Remote: DrawRemote(canvas); break;
            case Screen.History: DrawHistory(canvas); break;
            case Screen.Info: DrawInfo(canvas, snapshot); break;
            case Screen.Advanced: DrawAdvanced(canvas); break;
            case Screen.Help: DrawHelp(canvas); break;
        }

        DrawFooter(canvas, snapshot);
        canvas.Render();
    }

    private void DrawHeader(ConsoleCanvas c, SnapshotCopy s)
    {
        c.Write(1, 0, "GMC-300S TUI", ConsoleColor.Cyan);
        var x = 16;
        c.Write(x, 0, s.Version ?? "Detecting...", ConsoleColor.White);
        x += (s.Version ?? "Detecting...").Length + 3;
        c.Write(x, 0, $"{_device.PortName} @ {_device.BaudRate}", ConsoleColor.Yellow);
        c.WriteRight(c.Width - 2, 0, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), ConsoleColor.DarkGray);
        c.Horizontal(0, 1, c.Width, '═', ConsoleColor.DarkGray);
    }

    private void DrawFooter(ConsoleCanvas c, SnapshotCopy s)
    {
        var lineY = c.Height - 3;
        c.Horizontal(0, lineY, c.Width, '═', ConsoleColor.DarkGray);

        var msg = DateTime.Now <= _messageUntil && !string.IsNullOrWhiteSpace(_message)
            ? _message
            : s.Status;
        var statusColor = StatusColor(msg);
        c.Write(1, lineY + 1, Crop(msg, c.Width - 2), statusColor);

        var y = lineY + 2;
        var x = 1;
        x = WriteHotkey(c, x, y, "D", "Dashboard");
        if (_screen == Screen.Dashboard && x < c.Width - 12) x = WriteHotkey(c, x, y, "G", "Graph");
        x = WriteHotkey(c, x, y, "S", "Settings");
        x = WriteHotkey(c, x, y, "R", "Remote");
        x = WriteHotkey(c, x, y, "H", "History");
        x = WriteHotkey(c, x, y, "I", "Info");
        x = WriteHotkey(c, x, y, "X", "Advanced");
        if (x < c.Width - 18) x = WriteHotkey(c, x, y, "F1", "Help");
        if (x < c.Width - 9) WriteHotkey(c, x, y, "Q", "Quit");
    }

    private static int WriteHotkey(ConsoleCanvas c, int x, int y, string key, string label)
    {
        if (x >= c.Width - 2) return x;
        c.Write(x, y, key, ConsoleColor.Cyan);
        x += key.Length;
        c.Write(x, y, " " + label + "  ", ConsoleColor.Gray);
        return x + label.Length + 3;
    }

    private void DrawDashboard(ConsoleCanvas c, SnapshotCopy s)
    {
        var bodyTop = 3;
        var bodyBottom = c.Height - 4;
        var bodyHeight = bodyBottom - bodyTop + 1;
        var wide = c.Width >= 108 && bodyHeight >= 22;
        var large = c.Width >= 138 && bodyHeight >= 28;

        if (!wide)
        {
            DrawCompactDashboard(c, s, bodyTop, bodyBottom);
            return;
        }

        var gap = 2;
        var leftX = 1;
        var leftW = (c.Width - 3 - gap) / 2;
        var rightX = leftX + leftW + gap;
        var rightW = c.Width - rightX - 1;
        var panelH = large ? 11 : 9;

        DrawRadiationPanel(c, s, leftX, bodyTop, leftW, panelH, large);
        DrawDevicePanel(c, s, rightX, bodyTop, rightW, panelH);

        var graphY = bodyTop + panelH + 1;
        var graphH = bodyBottom - graphY + 1;
        DrawSelectedGraph(c, 1, graphY, c.Width - 2, graphH, s);
    }

    private void DrawCompactDashboard(ConsoleCanvas c, SnapshotCopy s, int top, int bottom)
    {
        var config = s.Config;
        var dose = GetDoseText(s, out var calibration);
        var speaker = config is { Length: > 2 } ? (config[2] == 0 ? "OFF" : "ON") : "?";
        var alarm = config is { Length: > 1 } ? (config[1] == 0 ? "OFF" : "ON") : "?";
        var saveMode = GetSaveMode(config);

        var row = top;
        c.Write(2, row++, "RADIATION", ConsoleColor.Cyan);
        WriteLabelValue(c, 2, row++, "CPM", FormatNullable(s.Cpm), ConsoleColor.Yellow);
        WriteLabelValue(c, 2, row++, "Dose rate", dose, ConsoleColor.Cyan);
        WriteLabelValue(c, 2, row++, "Calibration", calibration, ConsoleColor.DarkGray);
        if (row <= bottom)
            WriteLabelValue(c, 2, row++, "CPS", GetCpsText(s), ConsoleColor.DarkGray);

        if (row <= bottom) row++;
        if (row <= bottom) c.Write(2, row++, "DEVICE", ConsoleColor.Cyan);
        if (row <= bottom) WriteLabelValue(c, 2, row++, "Battery", s.Voltage is null ? "n/a" : $"{s.Voltage:0.0} V", ConsoleColor.Green);
        if (row <= bottom) WriteLabelValue(c, 2, row++, "Clock", GetClockText(s), GetClockColor(s.ClockDriftSeconds));
        if (row <= bottom) WriteLabelValue(c, 2, row++, "Speaker", speaker, speaker == "OFF" ? ConsoleColor.Green : ConsoleColor.Yellow);
        if (row <= bottom) WriteLabelValue(c, 2, row++, "Alarm", alarm, alarm == "ON" ? ConsoleColor.Green : ConsoleColor.DarkGray);
        if (row <= bottom) WriteLabelValue(c, 2, row++, "Logging", saveMode, ConsoleColor.White);

        row++;
        var graphH = bottom - row + 1;
        if (graphH >= 6)
            DrawSelectedGraph(c, 1, row, c.Width - 2, graphH, s);
        else if (graphH >= 2)
        {
            var series = GetSelectedGraphSeries(s);
            DrawMiniGraph(c, 2, row, c.Width - 4, series.Data, series.ShortLabel);
        }
    }

    private void DrawRadiationPanel(ConsoleCanvas c, SnapshotCopy s, int x, int y, int width, int height, bool large)
    {
        c.Box(x, y, width, height, ConsoleColor.DarkGray, "RADIATION", ConsoleColor.Cyan);
        var dose = GetDoseText(s, out var calibration);

        if (large && s.Cpm is int cpm && width >= 48)
        {
            DrawBigNumber(c, x + 3, y + 2, cpm.ToString(CultureInfo.InvariantCulture), ConsoleColor.Yellow);
            var numberWidth = BigTextWidth(cpm.ToString(CultureInfo.InvariantCulture));
            c.Write(x + 4 + numberWidth, y + 4, "CPM", ConsoleColor.Yellow);
            c.Write(x + 3, y + 8, "Dose", ConsoleColor.DarkGray);
            c.Write(x + 12, y + 8, dose, ConsoleColor.Cyan, maxWidth: width - 15);
            c.Write(x + 3, y + 9, "Cal", ConsoleColor.DarkGray);
            c.Write(x + 12, y + 9, calibration, ConsoleColor.Gray, maxWidth: width - 15);
        }
        else
        {
            WritePanelValue(c, x, y + 2, width, "CPM", FormatNullable(s.Cpm), ConsoleColor.Yellow);
            WritePanelValue(c, x, y + 3, width, "Dose rate", dose, ConsoleColor.Cyan);
            WritePanelValue(c, x, y + 4, width, "Calibration", calibration, ConsoleColor.Gray);
            WritePanelValue(c, x, y + 5, width, "CPS", GetCpsText(s), ConsoleColor.DarkGray);
        }
    }

    private void DrawDevicePanel(ConsoleCanvas c, SnapshotCopy s, int x, int y, int width, int height)
    {
        c.Box(x, y, width, height, ConsoleColor.DarkGray, "DEVICE", ConsoleColor.Cyan);
        var config = s.Config;
        var speaker = config is { Length: > 2 } ? (config[2] == 0 ? "OFF" : "ON") : "?";
        var alarm = config is { Length: > 1 } ? (config[1] == 0 ? "OFF" : "ON") : "?";
        var saveMode = GetSaveMode(config);

        var row = y + 2;
        WritePanelValue(c, x, row++, width, "Battery", s.Voltage is null ? "n/a" : $"{s.Voltage:0.0} V", ConsoleColor.Green);
        WritePanelValue(c, x, row++, width, "Device clock", GetClockText(s), GetClockColor(s.ClockDriftSeconds));
        WritePanelValue(c, x, row++, width, "Speaker", speaker, speaker == "OFF" ? ConsoleColor.Green : ConsoleColor.Yellow);
        WritePanelValue(c, x, row++, width, "Alarm", alarm, alarm == "ON" ? ConsoleColor.Green : ConsoleColor.DarkGray);
        WritePanelValue(c, x, row++, width, "Logging", saveMode, ConsoleColor.White);
        WritePanelValue(c, x, row++, width, "Serial", s.Serial ?? "n/a", ConsoleColor.White);

        if (row < y + height - 1)
        {
            var sensors = s.Capabilities.Temperature || s.Capabilities.Gyroscope
                ? "model-dependent sensors available"
                : "temperature/orientation unsupported";
            WritePanelValue(c, x, row, width, "Sensors", sensors, ConsoleColor.DarkGray);
        }
    }

    private static void WritePanelValue(ConsoleCanvas c, int boxX, int y, int boxWidth,
        string label, string value, ConsoleColor valueColor)
    {
        var labelX = boxX + 3;
        var valueX = boxX + Math.Min(18, Math.Max(12, boxWidth / 3));
        c.Write(labelX, y, label, ConsoleColor.DarkGray, maxWidth: Math.Max(1, valueX - labelX - 1));
        c.Write(valueX, y, value, valueColor, maxWidth: Math.Max(1, boxX + boxWidth - valueX - 2));
    }

    private static void WriteLabelValue(ConsoleCanvas c, int x, int y, string label, string value, ConsoleColor valueColor)
    {
        c.Write(x, y, label.PadRight(18), ConsoleColor.DarkGray);
        c.Write(x + 18, y, value, valueColor, maxWidth: Math.Max(1, c.Width - x - 19));
    }

    private void DrawSelectedGraph(ConsoleCanvas c, int x, int y, int width, int height, SnapshotCopy snapshot)
    {
        var series = GetSelectedGraphSeries(snapshot);
        DrawTrendGraph(c, x, y, width, height, series.Data, series.Title, series.ShortLabel, series.SecondsPerSample);
    }

    private (int[] Data, string Title, string ShortLabel, int SecondsPerSample) GetSelectedGraphSeries(SnapshotCopy snapshot)
    {
        switch (_graphMetric)
        {
            case GraphMetric.Dose:
            {
                if (snapshot.Config is null)
                    return ([], "DOSE TREND · nSv/h", "Dose", 1);

                var dose = new List<int>(snapshot.CpmHistory.Length);
                foreach (var cpm in snapshot.CpmHistory)
                {
                    if (ConfigSettings.TryComputeDoseRate(snapshot.Config, cpm, out var uSv))
                        dose.Add((int)Math.Round(uSv * 1000.0, MidpointRounding.AwayFromZero));
                }
                return (dose.ToArray(), "DOSE TREND · nSv/h", "Dose", 1);
            }
            case GraphMetric.Battery:
                return (snapshot.VoltageHistoryMv, "BATTERY TREND · mV", "Battery", 5);
            default:
                return (snapshot.CpmHistory, "CPM TREND", "CPM", 1);
        }
    }

    private static void DrawTrendGraph(ConsoleCanvas c, int x, int y, int width, int height,
        IReadOnlyList<int> history, string title, string shortLabel, int secondsPerSample)
    {
        if (width < 24 || height < 6)
        {
            DrawMiniGraph(c, x, y, width, history, shortLabel);
            return;
        }

        c.Box(x, y, width, height, ConsoleColor.DarkGray, title, ConsoleColor.Cyan);
        var axisW = 7;
        var plotX = x + axisW;
        var plotY = y + 2;
        var plotW = Math.Max(4, width - axisW - 2);
        var plotH = Math.Max(2, height - 4);
        var data = history.TakeLast(plotW).ToArray();

        if (data.Length == 0)
        {
            c.Write(x + 3, y + 2, $"Waiting for {shortLabel.ToLowerInvariant()} samples...", ConsoleColor.DarkGray);
            return;
        }

        var rawMin = data.Min();
        var rawMax = data.Max();
        var average = data.Average();
        var current = data[^1];
        var range = Math.Max(4, rawMax - rawMin);
        var padding = Math.Max(1, (int)Math.Ceiling(range * 0.15));
        var scaleMin = Math.Max(0, rawMin - padding);
        var scaleMax = Math.Max(scaleMin + 4, rawMax + padding);

        var stat = $"now {current}   min {rawMin}   avg {average:0.0}   max {rawMax}   {data.Length} samples";
        c.WriteRight(x + width - 3, y, stat, ConsoleColor.Gray);

        var topValue = scaleMax;
        var midValue = (scaleMax + scaleMin) / 2;
        var bottomValue = scaleMin;
        DrawGridLine(c, plotX, plotY, plotW, topValue, ConsoleColor.DarkGray);
        DrawGridLine(c, plotX, plotY + plotH / 2, plotW, midValue, ConsoleColor.DarkGray);
        DrawGridLine(c, plotX, plotY + plotH - 1, plotW, bottomValue, ConsoleColor.DarkGray);

        var avgY = ValueToRow(average, scaleMin, scaleMax, plotY, plotH);
        for (var px = plotX; px < plotX + plotW; px += 2)
            c.Put(px, avgY, '·', ConsoleColor.DarkYellow);

        var firstX = plotX + Math.Max(0, plotW - data.Length);
        int? previousX = null;
        int? previousY = null;
        for (var i = 0; i < data.Length; i++)
        {
            var px = firstX + i;
            var py = ValueToRow(data[i], scaleMin, scaleMax, plotY, plotH);

            if (previousX is int prevX && previousY is int prevY)
                ConnectGraphPoints(c, prevX, prevY, px, py, ConsoleColor.Cyan);

            c.Put(px, py, i == data.Length - 1 ? '●' : '•',
                i == data.Length - 1 ? ConsoleColor.Yellow : ConsoleColor.Cyan);
            previousX = px;
            previousY = py;
        }

        var bottomY = y + height - 2;
        var approxSeconds = Math.Max(secondsPerSample, data.Length * secondsPerSample);
        c.Write(plotX, bottomY, approxSeconds < 90 ? $"~{approxSeconds}s ago" : $"~{approxSeconds / 60.0:0.0}m ago", ConsoleColor.DarkGray);
        c.WriteRight(x + width - 3, bottomY, "now", ConsoleColor.DarkGray);
        c.Write(plotX + Math.Max(1, plotW / 2 - 4), bottomY, $"avg {average:0.0}", ConsoleColor.DarkYellow);
    }

    private static void DrawGridLine(ConsoleCanvas c, int plotX, int y, int plotW, int value, ConsoleColor color)
    {
        c.WriteRight(plotX - 2, y, value.ToString(CultureInfo.InvariantCulture), ConsoleColor.DarkGray);
        for (var px = plotX; px < plotX + plotW; px += 2)
            c.Put(px, y, '·', color);
    }

    private static int ValueToRow(double value, int min, int max, int plotY, int plotH)
    {
        var fraction = (value - min) / Math.Max(1.0, max - min);
        var offset = (int)Math.Round(fraction * (plotH - 1));
        return plotY + plotH - 1 - Math.Clamp(offset, 0, plotH - 1);
    }

    private static void ConnectGraphPoints(ConsoleCanvas c, int x1, int y1, int x2, int y2, ConsoleColor color)
    {
        if (x2 <= x1) return;
        var delta = y2 - y1;
        if (delta == 0)
        {
            c.Put(x2, y2, '─', color);
            return;
        }

        if (Math.Abs(delta) == 1)
        {
            c.Put(x2, y2, delta < 0 ? '╱' : '╲', color);
            return;
        }

        var start = Math.Min(y1, y2) + 1;
        var end = Math.Max(y1, y2);
        for (var graphY = start; graphY < end; graphY++)
            c.Put(x2, graphY, '│', color);
        c.Put(x2, y2, delta < 0 ? '╱' : '╲', color);
    }

    private static void DrawMiniGraph(ConsoleCanvas c, int x, int y, int width, IReadOnlyList<int> history, string label)
    {
        var data = history.TakeLast(Math.Max(1, width - label.Length - 2)).ToArray();
        if (data.Length == 0)
        {
            c.Write(x, y, $"{label} history: waiting for samples...", ConsoleColor.DarkGray);
            return;
        }

        const string bars = "▁▂▃▄▅▆▇█";
        var min = data.Min();
        var max = data.Max();
        c.Write(x, y, label + " ", ConsoleColor.Cyan);
        var graphX = x + label.Length + 1;
        for (var i = 0; i < data.Length && graphX + i < c.Width; i++)
        {
            var idx = max == min ? 3 : (int)Math.Round((data[i] - min) * (bars.Length - 1.0) / (max - min));
            c.Put(graphX + i, y, bars[Math.Clamp(idx, 0, bars.Length - 1)], i == data.Length - 1 ? ConsoleColor.Yellow : ConsoleColor.Cyan);
        }
    }

    private void DrawSettings(ConsoleCanvas c)
    {
        var top = 3;
        var bottom = c.Height - 4;
        c.Write(2, top, "SETTINGS", ConsoleColor.Cyan);
        c.Write(12, top, "↑/↓ select   Enter edit   PgUp/PgDn   R refresh   B backup", ConsoleColor.DarkGray);
        c.Write(2, top + 1, "Config layout is best-effort; Caution/Expert fields may vary by firmware.", ConsoleColor.Yellow);
        c.Horizontal(1, top + 2, c.Width - 2, '─', ConsoleColor.DarkGray);

        var config = GetConfigSnapshot();
        var listTop = top + 3;
        var descriptionRows = 4;
        var visible = Math.Max(5, bottom - listTop - descriptionRows + 1);
        var start = Math.Clamp(_settingIndex - visible / 2, 0, Math.Max(0, ConfigSettings.All.Count - visible));
        var end = Math.Min(ConfigSettings.All.Count, start + visible);

        var row = listTop;
        for (var i = start; i < end; i++, row++)
        {
            var setting = ConfigSettings.All[i];
            var selected = i == _settingIndex;
            var safety = setting.Safety switch
            {
                SettingSafety.Normal => " ",
                SettingSafety.Caution => "!",
                SettingSafety.Expert => "X",
                SettingSafety.ReadOnly => "R",
                _ => " "
            };
            var safetyColor = setting.Safety switch
            {
                SettingSafety.Caution => ConsoleColor.Yellow,
                SettingSafety.Expert => ConsoleColor.Red,
                SettingSafety.ReadOnly => ConsoleColor.DarkGray,
                _ => ConsoleColor.Gray
            };
            var fg = selected ? ConsoleColor.White : ConsoleColor.Gray;
            var bg = selected ? ConsoleColor.DarkBlue : ConsoleColor.Black;
            if (selected) c.Fill(1, row, c.Width - 2, 1, ' ', fg, bg);
            c.Write(2, row, selected ? "▶" : " ", selected ? ConsoleColor.Cyan : ConsoleColor.DarkGray, bg);
            c.Write(4, row, $"[{safety}]", safetyColor, bg);
            c.Write(8, row, $"0x{setting.Offset:X2}", ConsoleColor.DarkGray, bg);
            c.Write(14, row, setting.Name, fg, bg, Math.Min(31, c.Width - 15));
            var value = config is null ? "<not loaded>" : ConfigSettings.FormatValue(setting, config);
            if (c.Width >= 70) c.Write(47, row, value, selected ? ConsoleColor.White : ConsoleColor.Cyan, bg, c.Width - 49);
        }

        var descY = Math.Min(bottom - 2, listTop + visible + 1);
        c.Horizontal(1, descY - 1, c.Width - 2, '─', ConsoleColor.DarkGray);
        var selectedSetting = ConfigSettings.All[_settingIndex];
        c.Write(2, descY, selectedSetting.Description, ConsoleColor.Gray, maxWidth: c.Width - 4);
        c.Write(2, descY + 1, "Legend:", ConsoleColor.DarkGray);
        c.Write(10, descY + 1, "! caution", ConsoleColor.Yellow);
        c.Write(22, descY + 1, "X expert", ConsoleColor.Red);
        c.Write(33, descY + 1, "R read-only", ConsoleColor.DarkGray);
    }

    private void DrawRemote(ConsoleCanvas c)
    {
        var top = 3;
        c.Write(2, top, "REMOTE KEYPAD", ConsoleColor.Cyan);
        c.Write(2, top + 2, "These keys send the four physical GMC buttons over USB.", ConsoleColor.Gray);

        var center = c.Width / 2;
        var y = top + 5;
        DrawKeyBox(c, center - 8, y, 16, 3, "↑  S2 / KEY1", ConsoleColor.Cyan);
        DrawKeyBox(c, center - 26, y + 4, 16, 4, "← S1 / KEY0", ConsoleColor.Cyan, "Back");
        DrawKeyBox(c, center - 8, y + 4, 16, 4, "↓ S3 / KEY2", ConsoleColor.Cyan, "Down");
        DrawKeyBox(c, center + 10, y + 4, 16, 4, "→ S4 / KEY3", ConsoleColor.Cyan, "Enter/Menu");

        c.WriteCentered(1, c.Width - 2, y + 10,
            "Esc/Backspace/Left = KEY0   Up = KEY1   Down = KEY2   Enter/Right = KEY3",
            ConsoleColor.Gray);
        c.WriteCentered(1, c.Width - 2, y + 11, "Number keys 1..4 also send KEY0..KEY3.", ConsoleColor.DarkGray);
    }

    private static void DrawKeyBox(ConsoleCanvas c, int x, int y, int width, int height,
        string title, ConsoleColor color, string? subtitle = null)
    {
        c.Box(x, y, width, height, ConsoleColor.DarkGray);
        c.WriteCentered(x + 1, x + width - 2, y + 1, title, color);
        if (subtitle is not null && height >= 4)
            c.WriteCentered(x + 1, x + width - 2, y + 2, subtitle, ConsoleColor.Gray);
    }

    private void DrawHistory(ConsoleCanvas c)
    {
        var top = 3;
        c.Write(2, top, "HISTORY", ConsoleColor.Cyan);
        c.Write(2, top + 2, "The GMC-300S has 64 KiB of internal history memory.", ConsoleColor.Gray);
        c.Write(4, top + 4, "• reads flash through SPIR in 4096-byte chunks", ConsoleColor.White);
        c.Write(4, top + 5, "• preserves the exact raw binary image", ConsoleColor.White);
        c.Write(4, top + 6, "• parses timestamp/save-mode markers and exports CSV", ConsoleColor.White);
        c.Write(2, top + 8, "Output:", ConsoleColor.DarkGray);
        c.Write(10, top + 8, _historyDirectory, ConsoleColor.Cyan, maxWidth: c.Width - 12);
        c.Write(2, top + 10, "Press G or Enter to download/export history now.", ConsoleColor.Yellow);
        c.Write(2, top + 12, "Parsing is best-effort because GQ does not fully document the history record format.", ConsoleColor.DarkGray, maxWidth: c.Width - 4);
    }

    private void DrawInfo(ConsoleCanvas c, SnapshotCopy s)
    {
        var top = 3;
        c.Write(2, top, "INFO / RAW CONFIG", ConsoleColor.Cyan);
        c.Write(22, top, $"Version {s.Version ?? "?"}", ConsoleColor.White);
        c.Write(48, top, $"Serial {s.Serial ?? "?"}", ConsoleColor.White, maxWidth: Math.Max(1, c.Width - 50));
        c.Write(2, top + 1, $"{_device.PortName} @ {_device.BaudRate}, 8N1, no flow control", ConsoleColor.Yellow);
        c.Write(2, top + 2,
            $"Capabilities: heartbeat CPS={(s.Capabilities.HeartbeatCpsSampling ? "sampled" : "disabled")}, temperature={(s.Capabilities.Temperature ? "yes" : "no")}, gyro={(s.Capabilities.Gyroscope ? "yes" : "no")}",
            ConsoleColor.DarkGray, maxWidth: c.Width - 4);
        c.Write(2, top + 3, $"Raw configuration page {_rawConfigPage + 1}/2 — ←/→ change page, R refresh", ConsoleColor.Gray);
        c.Horizontal(1, top + 4, c.Width - 2, '─', ConsoleColor.DarkGray);

        var config = s.Config;
        if (config is null)
        {
            c.Write(2, top + 6, "Configuration not available.", ConsoleColor.Red);
            return;
        }

        var startRow = _rawConfigPage * 8;
        var y = top + 6;
        c.Write(2, y++, "     00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F     ASCII", ConsoleColor.DarkGray);
        for (var row = startRow; row < startRow + 8 && y < c.Height - 5; row++, y++)
        {
            var offset = row * 16;
            var bytes = config.Skip(offset).Take(16).ToArray();
            var hex = string.Join(' ', bytes.Select(b => b.ToString("X2")));
            var ascii = new string(bytes.Select(b => b is >= 32 and <= 126 ? (char)b : '.').ToArray());
            c.Write(2, y, $"{offset:X2}:", ConsoleColor.Cyan);
            c.Write(7, y, hex, ConsoleColor.White);
            if (c.Width > 70) c.Write(58, y, ascii, ConsoleColor.DarkGray, maxWidth: c.Width - 60);
        }
    }

    private void DrawAdvanced(ConsoleCanvas c)
    {
        var top = 3;
        c.Write(2, top, "ADVANCED / DESTRUCTIVE COMMANDS", ConsoleColor.Red);
        c.Write(2, top + 1, "Actions that can alter or erase device state require typed confirmation.", ConsoleColor.Yellow);
        var items = new[]
        {
            ("1", "Reboot counter", false),
            ("2", "Power off counter", false),
            ("3", "Power on command", false),
            ("4", "Factory reset  [backs up config first]", true),
            ("5", "Raw WCFG byte write  [backs up config first]", true),
            ("6", "Erase all configuration  [DANGEROUS; backs up first]", true),
            ("7", "Reload/refresh configuration (CFGUPDATE)", false)
        };
        var y = top + 4;
        foreach (var (key, label, dangerous) in items)
        {
            c.Write(4, y, key, dangerous ? ConsoleColor.Red : ConsoleColor.Cyan);
            c.Write(8, y++, label, dangerous ? ConsoleColor.Yellow : ConsoleColor.Gray, maxWidth: c.Width - 10);
        }
        c.Write(2, y + 2, "Automatic config backups:", ConsoleColor.DarkGray);
        c.Write(28, y + 2, _backupDirectory, ConsoleColor.Cyan, maxWidth: c.Width - 30);
    }

    private void DrawHelp(ConsoleCanvas c)
    {
        var top = 3;
        c.Write(2, top, "HELP / KEYBOARD", ConsoleColor.Cyan);
        var y = top + 2;
        DrawHelpRow(c, ref y, "D", "Dashboard");
        DrawHelpRow(c, ref y, "S", "Settings");
        DrawHelpRow(c, ref y, "R", "Remote keypad");
        DrawHelpRow(c, ref y, "H", "History/export");
        DrawHelpRow(c, ref y, "I", "Device info/raw configuration");
        DrawHelpRow(c, ref y, "X", "Advanced commands");
        DrawHelpRow(c, ref y, "G", "Cycle dashboard graph: CPM / dose / battery");
        DrawHelpRow(c, ref y, "M", "Mute/unmute speaker clicks");
        DrawHelpRow(c, ref y, "A", "Toggle alarm");
        DrawHelpRow(c, ref y, "T", "Synchronize counter clock to Windows");
        DrawHelpRow(c, ref y, "Q", "Quit");

        y += 1;
        c.Write(2, y++, "Settings: ↑/↓ move | PgUp/PgDn | Home/End | Enter edit | B backup | R refresh", ConsoleColor.Gray, maxWidth: c.Width - 4);
        c.Write(2, y++, "Info: ←/→ switch raw-config page | R refresh", ConsoleColor.Gray);
        y++;
        c.Write(2, y++, "Safety model", ConsoleColor.Yellow);
        c.Write(4, y++, "• direct firmware commands are preferred for speaker/alarm/time/key/power actions", ConsoleColor.DarkGray, maxWidth: c.Width - 6);
        c.Write(4, y++, "• EEPROM edits are backed up before WCFG and verified after CFGUPDATE", ConsoleColor.DarkGray, maxWidth: c.Width - 6);
        c.Write(4, y, "• uncertain offsets remain Caution/Expert/read-only", ConsoleColor.DarkGray, maxWidth: c.Width - 6);
    }

    private static void DrawHelpRow(ConsoleCanvas c, ref int y, string key, string description)
    {
        c.Write(4, y, key, ConsoleColor.Cyan);
        c.Write(10, y++, description, ConsoleColor.Gray);
    }

    private static ConsoleColor StatusColor(string status)
    {
        if (status.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("error", StringComparison.OrdinalIgnoreCase))
            return ConsoleColor.Red;
        if (status.Contains("Connected", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("saved", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Updated", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("synchronized", StringComparison.OrdinalIgnoreCase))
            return ConsoleColor.Green;
        return ConsoleColor.Yellow;
    }

    private static ConsoleColor GetClockColor(double? driftSeconds)
    {
        if (driftSeconds is null) return ConsoleColor.DarkGray;
        return Math.Abs(driftSeconds.Value) <= 5 ? ConsoleColor.Green : ConsoleColor.Yellow;
    }

    private static string GetClockText(SnapshotCopy s)
    {
        if (s.DeviceTime is null) return "n/a";
        var drift = s.ClockDriftSeconds is null
            ? string.Empty
            : $" ({Math.Round(s.ClockDriftSeconds.Value):+#;-#;0}s vs PC)";
        return $"{s.DeviceTime:yyyy-MM-dd HH:mm:ss}{drift}";
    }

    private static string GetCpsText(SnapshotCopy s) => s.Capabilities.HeartbeatCpsSampling
        ? (s.Cps?.ToString("N0", CultureInfo.InvariantCulture) ?? "waiting...")
        : "N/A — heartbeat kept off during command polling";

    private static string GetSaveMode(byte[]? config) => config is { Length: > 32 }
        ? ConfigSettings.FormatValue(ConfigSettings.All.First(x => x.Offset == 32), config)
        : "?";

    private static string GetDoseText(SnapshotCopy s, out string calibration)
    {
        calibration = "n/a";
        if (s.Cpm is not int cpm || s.Config is null || !ConfigSettings.TryComputeDoseRate(s.Config, cpm, out var uSv))
            return "n/a";

        if (uSv > 0)
            calibration = $"~{cpm / uSv:0.0} CPM per µSv/h";
        return $"{uSv:0.0000} µSv/h  (~{uSv / 10.0:0.0000} mR/h)";
    }

    private static readonly IReadOnlyDictionary<char, string[]> BigGlyphs = new Dictionary<char, string[]>
    {
        ['0'] = ["███", "█ █", "█ █", "█ █", "███"],
        ['1'] = [" █ ", "██ ", " █ ", " █ ", "███"],
        ['2'] = ["███", "  █", "███", "█  ", "███"],
        ['3'] = ["███", "  █", "███", "  █", "███"],
        ['4'] = ["█ █", "█ █", "███", "  █", "  █"],
        ['5'] = ["███", "█  ", "███", "  █", "███"],
        ['6'] = ["███", "█  ", "███", "█ █", "███"],
        ['7'] = ["███", "  █", "  █", "  █", "  █"],
        ['8'] = ["███", "█ █", "███", "█ █", "███"],
        ['9'] = ["███", "█ █", "███", "  █", "███"]
    };

    private static void DrawBigNumber(ConsoleCanvas c, int x, int y, string text, ConsoleColor color)
    {
        var cursor = x;
        foreach (var ch in text)
        {
            if (!BigGlyphs.TryGetValue(ch, out var glyph)) continue;
            for (var row = 0; row < glyph.Length; row++)
                c.Write(cursor, y + row, glyph[row], color);
            cursor += 4;
        }
    }

    private static int BigTextWidth(string text) => Math.Max(0, text.Length * 4 - 1);

    private void Flash(string message, int seconds = 3)
    {
        _message = message;
        _messageUntil = DateTime.Now.AddSeconds(seconds);
    }

    private void SetStatus(string message)
    {
        lock (_snapshotLock) _snapshot.Status = message;
    }

    private byte[]? GetConfigSnapshot()
    {
        lock (_snapshotLock) return _snapshot.Config?.ToArray();
    }

    private sealed record SnapshotCopy(
        int? Cpm,
        int? Cps,
        double? Voltage,
        double? TemperatureC,
        DateTime? DeviceTime,
        double? ClockDriftSeconds,
        (short X, short Y, short Z)? Gyro,
        byte[]? Config,
        string? Version,
        string? Serial,
        string Status,
        int[] CpmHistory,
        int[] VoltageHistoryMv,
        DeviceCapabilities Capabilities);

    private SnapshotCopy CopySnapshot()
    {
        lock (_snapshotLock)
        {
            return new SnapshotCopy(
                _snapshot.Cpm,
                _snapshot.Cps,
                _snapshot.Voltage,
                _snapshot.TemperatureC,
                _snapshot.DeviceTime,
                _snapshot.ClockDriftSeconds,
                _snapshot.Gyro,
                _snapshot.Config?.ToArray(),
                _snapshot.Version,
                _snapshot.Serial,
                _snapshot.Status,
                _snapshot.CpmHistory.ToArray(),
                _snapshot.VoltageHistoryMv.ToArray(),
                _snapshot.Capabilities);
        }
    }

    private static string FormatNullable(int? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "n/a";

    private static string Crop(string text, int width)
    {
        var max = Math.Max(1, width);
        if (text.Length <= max) return text;
        return max == 1 ? "…" : text[..(max - 1)] + "…";
    }
}
