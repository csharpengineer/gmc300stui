using System.Diagnostics;
using System.Globalization;
using System.Text;
using Gmc300sTui.Device;

namespace Gmc300sTui.Ui;

public sealed class TuiApp
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

    private sealed class Snapshot
    {
        public int? Cpm { get; set; }
        public int? Cps { get; set; }
        public double? Voltage { get; set; }
        public double? TemperatureC { get; set; }
        public DateTime? DeviceTime { get; set; }
        public (short X, short Y, short Z)? Gyro { get; set; }
        public byte[]? Config { get; set; }
        public string? Version { get; set; }
        public string? Serial { get; set; }
        public string Status { get; set; } = "Connected";
        public DateTime LastCpmAt { get; set; }
        public DateTime LastSlowPollAt { get; set; }
        public DateTime LastConfigAt { get; set; }
        public Queue<int> CpmHistory { get; } = new();
        public bool CpsSupported { get; set; } = true;
        public bool TempSupported { get; set; } = true;
        public bool GyroSupported { get; set; } = true;
    }

    private readonly Gmc300sDevice _device;
    private readonly Snapshot _snapshot = new();
    private readonly object _snapshotLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _backupDirectory;
    private readonly string _historyDirectory;
    private Screen _screen = Screen.Dashboard;
    private int _settingIndex;
    private int _rawConfigPage;
    private string _message = "";
    private DateTime _messageUntil;
    private Task? _pollTask;

    public TuiApp(Gmc300sDevice device)
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
                _snapshot.Cpm = cpm;
                _snapshot.LastCpmAt = DateTime.Now;
                _snapshot.LastConfigAt = DateTime.Now;
                _snapshot.CpmHistory.Enqueue(cpm);
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
                    while (_snapshot.CpmHistory.Count > 120)
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

            if (doSlow)
                PollSlow();
            if (doConfig)
                PollConfig();

            token.WaitHandle.WaitOne(900);
        }
    }

    private void PollSlow()
    {
        bool tryCps;
        lock (_snapshotLock) tryCps = _snapshot.CpsSupported;
        if (tryCps)
        {
            try
            {
                var cps = _device.GetCpsViaHeartbeatSample();
                lock (_snapshotLock) _snapshot.Cps = cps;
            }
            catch (TimeoutException)
            {
                lock (_snapshotLock) _snapshot.CpsSupported = false;
            }
            catch { }
        }

        TryUpdate(() => _device.GetVoltage(), v => _snapshot.Voltage = v);
        TryUpdate(() => _device.GetDateTime(), v => _snapshot.DeviceTime = v);

        bool tryTemp;
        bool tryGyro;
        lock (_snapshotLock)
        {
            tryTemp = _snapshot.TempSupported;
            tryGyro = _snapshot.GyroSupported;
        }

        if (tryTemp)
        {
            try
            {
                var temp = _device.GetTemperatureCelsius();
                lock (_snapshotLock) _snapshot.TemperatureC = temp;
            }
            catch (TimeoutException)
            {
                lock (_snapshotLock) _snapshot.TempSupported = false;
            }
            catch { }
        }

        if (tryGyro)
        {
            try
            {
                var gyro = _device.GetGyro();
                lock (_snapshotLock) _snapshot.Gyro = gyro;
            }
            catch (TimeoutException)
            {
                lock (_snapshotLock) _snapshot.GyroSupported = false;
            }
            catch { }
        }
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
            case ConsoleKey.M:
                ToggleSpeaker();
                return true;
            case ConsoleKey.A:
                ToggleAlarm();
                return true;
            case ConsoleKey.T:
                SyncClock();
                return true;
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
            lock (_snapshotLock) _snapshot.DeviceTime = now;
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
        Console.WriteLine("Downloading GMC-300S history...");
        Console.WriteLine();
        try
        {
            var raw = _device.ReadHistory(progress: (done, total) =>
            {
                Console.SetCursorPosition(0, 2);
                var pct = total == 0 ? 0 : done * 100.0 / total;
                Console.Write($"Read {done,6:N0} / {total,6:N0} bytes  ({pct,5:0.0}%)   ");
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
        Console.WriteLine("GMC-300S TUI — input mode");
        Console.WriteLine(new string('─', Math.Min(80, Math.Max(10, Console.WindowWidth - 1))));
        Console.Write(prompt);
        var text = Console.ReadLine() ?? string.Empty;
        Console.CursorVisible = false;
        Console.Clear();
        return text;
    }

    private void Draw()
    {
        var width = Math.Max(20, Console.WindowWidth);
        var height = Math.Max(10, Console.WindowHeight);
        var sb = new StringBuilder();

        AppendHeader(sb, width);
        switch (_screen)
        {
            case Screen.Dashboard: DrawDashboard(sb, width, height); break;
            case Screen.Settings: DrawSettings(sb, width, height); break;
            case Screen.Remote: DrawRemote(sb, width); break;
            case Screen.History: DrawHistory(sb, width); break;
            case Screen.Info: DrawInfo(sb, width, height); break;
            case Screen.Advanced: DrawAdvanced(sb, width); break;
            case Screen.Help: DrawHelp(sb, width); break;
        }
        AppendFooter(sb, width);

        var text = sb.ToString();
        try
        {
            Console.SetCursorPosition(0, 0);
            Console.Write(FitToConsole(text, width, height));
        }
        catch (ArgumentOutOfRangeException)
        {
            Console.Clear();
        }
    }

    private void AppendHeader(StringBuilder sb, int width)
    {
        SnapshotCopy s = CopySnapshot();
        sb.AppendLine(Crop($" GMC-300S TUI   {s.Version ?? "Detecting..."}   {_device.PortName} @ {_device.BaudRate}   {DateTime.Now:yyyy-MM-dd HH:mm:ss}", width));
        sb.AppendLine(new string('═', Math.Min(width - 1, 140)));
    }

    private void DrawDashboard(StringBuilder sb, int width, int height)
    {
        var s = CopySnapshot();
        var config = s.Config;
        var dose = "n/a";
        if (s.Cpm is int cpm && config is not null && ConfigSettings.TryComputeDoseRate(config, cpm, out var uSv))
            dose = $"{uSv:0.0000} µSv/h   (~{uSv / 10.0:0.0000} mR/h)";

        var speaker = config is { Length: > 2 } ? (config[2] == 0 ? "OFF" : "ON") : "?";
        var alarm = config is { Length: > 1 } ? (config[1] == 0 ? "OFF" : "ON") : "?";
        var saveMode = config is { Length: > 32 }
            ? ConfigSettings.FormatValue(ConfigSettings.All.First(x => x.Offset == 32), config)
            : "?";

        sb.AppendLine();
        sb.AppendLine($"  CPM                 {FormatNullable(s.Cpm),12}");
        sb.AppendLine($"  CPS (heartbeat)     {(s.Cps is null ? (s.CpsSupported ? "probing..." : "unsupported") : s.Cps.Value.ToString("N0", CultureInfo.InvariantCulture))}");
        sb.AppendLine($"  Dose rate           {dose}");
        sb.AppendLine($"  Battery voltage     {(s.Voltage is null ? "n/a" : $"{s.Voltage:0.0} V")}");
        sb.AppendLine($"  Temperature         {(s.TemperatureC is null ? (s.TempSupported ? "probing..." : "unsupported") : $"{s.TemperatureC:0.0} °C")}");
        sb.AppendLine($"  Device clock        {(s.DeviceTime is null ? "n/a" : s.DeviceTime.Value.ToString("yyyy-MM-dd HH:mm:ss"))}");
        sb.AppendLine($"  Gyro/orientation    {(s.Gyro is null ? (s.GyroSupported ? "probing..." : "unsupported") : $"X={s.Gyro.Value.X}  Y={s.Gyro.Value.Y}  Z={s.Gyro.Value.Z}")}");
        sb.AppendLine($"  Speaker/clicks      {speaker}");
        sb.AppendLine($"  Alarm               {alarm}");
        sb.AppendLine($"  Data logging        {saveMode}");
        sb.AppendLine($"  Serial number       {s.Serial ?? "n/a"}");
        sb.AppendLine();
        sb.AppendLine("  CPM history (newest at right)");
        sb.AppendLine("  " + Sparkline(s.CpmHistory, Math.Min(90, Math.Max(20, width - 6))));
        sb.AppendLine();
        sb.AppendLine("  M mute/unmute   A alarm toggle   T sync device clock   R remote keypad");
        sb.AppendLine("  S settings      H history        I raw/info            X advanced");
    }

    private void DrawSettings(StringBuilder sb, int width, int height)
    {
        var config = GetConfigSnapshot();
        sb.AppendLine(" SETTINGS — ↑/↓ select, Enter edit, PgUp/PgDn, R refresh, B backup");
        sb.AppendLine(" The config layout is best-effort. Caution/Expert items are firmware-dependent.");
        sb.AppendLine(new string('─', Math.Min(width - 1, 140)));

        var visible = Math.Max(8, height - 9);
        var start = Math.Clamp(_settingIndex - visible / 2, 0, Math.Max(0, ConfigSettings.All.Count - visible));
        var end = Math.Min(ConfigSettings.All.Count, start + visible);

        for (var i = start; i < end; i++)
        {
            var setting = ConfigSettings.All[i];
            var marker = i == _settingIndex ? '▶' : ' ';
            var safety = setting.Safety switch
            {
                SettingSafety.Normal => " ",
                SettingSafety.Caution => "!",
                SettingSafety.Expert => "X",
                SettingSafety.ReadOnly => "R",
                _ => " "
            };
            var value = config is null ? "<not loaded>" : ConfigSettings.FormatValue(setting, config);
            sb.AppendLine(Crop($" {marker} [{safety}] 0x{setting.Offset:X2} {setting.Name,-29} {value}", width));
        }

        sb.AppendLine(new string('─', Math.Min(width - 1, 140)));
        var selected = ConfigSettings.All[_settingIndex];
        sb.AppendLine(Crop(" " + selected.Description, width));
        sb.AppendLine(" Legend: ! caution   X expert confirmation   R read-only");
    }

    private void DrawRemote(StringBuilder sb, int width)
    {
        sb.AppendLine(" REMOTE KEYPAD");
        sb.AppendLine();
        sb.AppendLine(" The keyboard sends the four physical GMC keys over USB:");
        sb.AppendLine();
        sb.AppendLine("                    ┌─────────────┐");
        sb.AppendLine("                    │ ↑  S2 / KEY1│");
        sb.AppendLine("        ┌───────────┼─────────────┼──────────────┐");
        sb.AppendLine("        │ ← S1/KEY0 │ ↓  S3 / KEY2│ → S4 / KEY3 │");
        sb.AppendLine("        │   Back    │    Down     │ Enter / Menu │");
        sb.AppendLine("        └───────────┴─────────────┴──────────────┘");
        sb.AppendLine();
        sb.AppendLine(" Esc/Backspace/Left = KEY0   Up = KEY1   Down = KEY2   Enter/Right = KEY3");
        sb.AppendLine(" Number keys 1..4 also send KEY0..KEY3.");
    }

    private void DrawHistory(StringBuilder sb, int width)
    {
        sb.AppendLine(" HISTORY");
        sb.AppendLine();
        sb.AppendLine(" The GMC-300S has 64 KiB of internal history memory. This screen can:");
        sb.AppendLine("   • read flash via SPIR in 4096-byte chunks");
        sb.AppendLine("   • save the exact raw binary image");
        sb.AppendLine("   • parse timestamp/save-mode markers and export CSV");
        sb.AppendLine();
        sb.AppendLine($" Files are written to: {_historyDirectory}");
        sb.AppendLine();
        sb.AppendLine(" Press G or Enter to download/export history now.");
        sb.AppendLine();
        sb.AppendLine(" Parsing is best-effort because GQ does not fully document the history record format.");
    }

    private void DrawInfo(StringBuilder sb, int width, int height)
    {
        var s = CopySnapshot();
        sb.AppendLine($" INFO / RAW CONFIG     Version: {s.Version ?? "?"}     Serial: {s.Serial ?? "?"}");
        sb.AppendLine($" Connection: {_device.PortName} @ {_device.BaudRate}, 8 data bits, no parity, 1 stop bit, no flow control");
        sb.AppendLine($" Raw configuration page {_rawConfigPage + 1}/2 — ←/→ change page, R refresh");
        sb.AppendLine(new string('─', Math.Min(width - 1, 140)));

        var config = s.Config;
        if (config is null)
        {
            sb.AppendLine(" Configuration not available.");
            return;
        }

        var startRow = _rawConfigPage * 8;
        sb.AppendLine("      00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F     ASCII");
        for (var row = startRow; row < startRow + 8; row++)
        {
            var offset = row * 16;
            var bytes = config.Skip(offset).Take(16).ToArray();
            var hex = string.Join(' ', bytes.Select(b => b.ToString("X2")));
            var ascii = new string(bytes.Select(b => b is >= 32 and <= 126 ? (char)b : '.').ToArray());
            sb.AppendLine($" {offset:X2}:  {hex}    {ascii}");
        }
        sb.AppendLine();
        sb.AppendLine(" All 256 bytes are shown across these two pages, including undocumented firmware fields.");
    }

    private void DrawAdvanced(StringBuilder sb, int width)
    {
        sb.AppendLine(" ADVANCED / DESTRUCTIVE COMMANDS");
        sb.AppendLine();
        sb.AppendLine("  1  Reboot counter");
        sb.AppendLine("  2  Power off counter");
        sb.AppendLine("  3  Power on command");
        sb.AppendLine("  4  Factory reset                 [backs up config first]");
        sb.AppendLine("  5  Raw WCFG byte write           [backs up config first]");
        sb.AppendLine("  6  Erase all configuration       [backs up config first — DANGEROUS]");
        sb.AppendLine("  7  Reload/refresh configuration (CFGUPDATE)");
        sb.AppendLine();
        sb.AppendLine(" Destructive operations require an exact typed confirmation phrase.");
        sb.AppendLine($" Automatic config backups: {_backupDirectory}");
    }

    private void DrawHelp(StringBuilder sb, int width)
    {
        sb.AppendLine(" HELP / KEYBOARD");
        sb.AppendLine();
        sb.AppendLine(" Global: D dashboard | S settings | R remote | H history | I info | X advanced | F1 help | Q quit");
        sb.AppendLine("         M speaker  | A alarm    | T sync clock | Ctrl+C quit");
        sb.AppendLine();
        sb.AppendLine(" Settings: ↑/↓ move | PgUp/PgDn | Home/End | Enter edit | B backup | R refresh");
        sb.AppendLine(" Info:     ←/→ switch raw-config page | R refresh");
        sb.AppendLine();
        sb.AppendLine(" Safety model:");
        sb.AppendLine("   • direct firmware commands are preferred for speaker/alarm/time/key/power actions");
        sb.AppendLine("   • known EEPROM edits are backed up before WCFG and verified after CFGUPDATE");
        sb.AppendLine("   • uncertain offsets are labeled Caution/Expert or left read-only");
        sb.AppendLine("   • the Info screen exposes all 256 raw config bytes regardless of interpretation");
    }

    private void AppendFooter(StringBuilder sb, int width)
    {
        sb.AppendLine(new string('═', Math.Min(width - 1, 140)));
        var status = CopySnapshot().Status;
        var msg = DateTime.Now <= _messageUntil && !string.IsNullOrWhiteSpace(_message) ? _message : status;
        sb.AppendLine(Crop($" {msg}", width));
        sb.AppendLine(Crop(" D Dashboard  S Settings  R Remote  H History  I Info  X Advanced  F1 Help  Q Quit", width));
    }

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
        (short X, short Y, short Z)? Gyro,
        byte[]? Config,
        string? Version,
        string? Serial,
        string Status,
        int[] CpmHistory,
        bool CpsSupported,
        bool TempSupported,
        bool GyroSupported);

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
                _snapshot.Gyro,
                _snapshot.Config?.ToArray(),
                _snapshot.Version,
                _snapshot.Serial,
                _snapshot.Status,
                _snapshot.CpmHistory.ToArray(),
                _snapshot.CpsSupported,
                _snapshot.TempSupported,
                _snapshot.GyroSupported);
        }
    }

    private static string Sparkline(IEnumerable<int> values, int width)
    {
        var data = values.TakeLast(width).ToArray();
        if (data.Length == 0) return "(waiting for data)";
        const string bars = "▁▂▃▄▅▆▇█";
        var min = data.Min();
        var max = data.Max();
        if (max == min) return new string(bars[3], data.Length) + $"  {min} CPM";

        var chars = data.Select(v =>
        {
            var idx = (int)Math.Round((v - min) * (bars.Length - 1.0) / (max - min));
            return bars[Math.Clamp(idx, 0, bars.Length - 1)];
        }).ToArray();
        return new string(chars) + $"  min {min} / max {max}";
    }

    private static string FormatNullable(int? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "n/a";

    private static string Crop(string text, int width)
    {
        var max = Math.Max(1, width - 1);
        if (text.Length <= max) return text;
        return text[..Math.Max(1, max - 1)] + "…";
    }

    private static string FitToConsole(string text, int width, int height)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var sb = new StringBuilder();
        var usableHeight = Math.Max(1, height - 1);
        for (var i = 0; i < usableHeight; i++)
        {
            var line = i < lines.Length ? Crop(lines[i], width) : string.Empty;
            sb.Append(line.PadRight(Math.Max(1, width - 1)));
            if (i < usableHeight - 1) sb.AppendLine();
        }
        return sb.ToString();
    }
}
