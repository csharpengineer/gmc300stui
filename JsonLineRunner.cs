using System.Globalization;
using System.Text.Json;
using Gmc300sTui.Device;

namespace Gmc300sTui;

/// <summary>
/// Machine-readable streaming mode. Stdout contains only compact JSON objects,
/// one per successful CPM sample, so it can be redirected or appended directly
/// to a .jsonl file. Diagnostics go to stderr.
/// </summary>
internal sealed class JsonLineRunner
{
    private readonly Gmc300sDevice _device;
    private string _version = string.Empty;
    private string _serial = string.Empty;
    private byte[]? _config;
    private double? _voltage;
    private DateTime? _deviceTime;
    private double? _clockDriftSeconds;
    private DateTime _lastSlowPoll = DateTime.MinValue;
    private DateTime _lastConfigPoll = DateTime.MinValue;

    public JsonLineRunner(Gmc300sDevice device)
    {
        _device = device;
    }

    public void Run()
    {
        _version = SafeRead(_device.GetVersion) ?? "unknown";
        _serial = SafeRead(_device.GetSerialNumber) ?? "unknown";
        _config = SafeRead(_device.GetConfig);
        PollSlow(force: true);

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var started = DateTimeOffset.Now;
                try
                {
                    var cpm = _device.GetCpm();
                    PollSlow(force: false);
                    PollConfigIfDue();
                    WriteSample(started, cpm);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"{DateTimeOffset.Now:O} CPM poll failed: {ex.Message}");
                }

                var elapsed = DateTimeOffset.Now - started;
                var remaining = TimeSpan.FromSeconds(1) - elapsed;
                if (remaining > TimeSpan.Zero)
                    cts.Token.WaitHandle.WaitOne(remaining);
            }
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private void PollSlow(bool force)
    {
        var now = DateTime.Now;
        if (!force && (now - _lastSlowPoll).TotalSeconds < 5)
            return;
        _lastSlowPoll = now;

        var voltage = SafeRead(_device.GetVoltage);
        if (voltage is not null)
            _voltage = voltage;

        var deviceTime = SafeRead(_device.GetDateTime);
        if (deviceTime is not null)
        {
            _deviceTime = deviceTime;
            _clockDriftSeconds = (deviceTime.Value - DateTime.Now).TotalSeconds;
        }
    }

    private void PollConfigIfDue()
    {
        var now = DateTime.Now;
        if ((now - _lastConfigPoll).TotalSeconds < 30)
            return;
        _lastConfigPoll = now;
        var config = SafeRead(_device.GetConfig);
        if (config is not null)
            _config = config;
    }

    private void WriteSample(DateTimeOffset timestamp, int cpm)
    {
        double? dose = null;
        if (_config is not null && ConfigSettings.TryComputeDoseRate(_config, cpm, out var uSvPerHour))
            dose = uSvPerHour;

        bool? speaker = _config is { Length: > 2 } ? _config[2] != 0 : null;
        bool? alarm = _config is { Length: > 1 } ? _config[1] != 0 : null;
        string? loggingMode = null;
        if (_config is { Length: > 32 })
        {
            var saveSetting = ConfigSettings.All.FirstOrDefault(x => x.Offset == 32);
            if (saveSetting is not null)
                loggingMode = ConfigSettings.FormatValue(saveSetting, _config);
        }

        var sample = new Dictionary<string, object?>
        {
            ["timestamp_utc"] = timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["timestamp_local"] = timestamp.ToString("O", CultureInfo.InvariantCulture),
            ["cpm"] = cpm,
            ["dose_uSv_h"] = dose,
            ["dose_mR_h"] = dose is null ? null : dose.Value / 10.0,
            ["battery_v"] = _voltage,
            ["device_time"] = _deviceTime?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["device_clock_drift_s"] = _clockDriftSeconds is null ? null : Math.Round(_clockDriftSeconds.Value, 3),
            ["speaker"] = speaker,
            ["alarm"] = alarm,
            ["logging_mode"] = loggingMode,
            ["version"] = _version,
            ["serial"] = _serial,
            ["port"] = _device.PortName,
            ["baud"] = _device.BaudRate
        };

        Console.Out.WriteLine(JsonSerializer.Serialize(sample, JsonOptions));
        Console.Out.Flush();
    }

    private static T? SafeRead<T>(Func<T> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{DateTimeOffset.Now:O} device metadata read failed: {ex.Message}");
            return default;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };
}
