namespace Gmc300sTui.Device;

/// <summary>
/// Model-specific protocol capabilities. GQ reuses command names across multiple
/// GMC generations, but not every command returns meaningful data on every model.
/// Keep optional polling opt-in so an unsupported command cannot be mistaken for
/// a real sensor reading.
/// </summary>
public sealed record DeviceCapabilities(
    string Model,
    bool HeartbeatCpsSampling,
    bool Temperature,
    bool Gyroscope)
{
    public static readonly DeviceCapabilities Unknown = new(
        "unknown model",
        HeartbeatCpsSampling: false,
        Temperature: false,
        Gyroscope: false);

    public static readonly DeviceCapabilities Gmc300S = new(
        "GMC-300S",
        HeartbeatCpsSampling: false,
        Temperature: false,
        Gyroscope: false);

    public static DeviceCapabilities FromVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Unknown;

        if (version.StartsWith("GMC-300S", StringComparison.OrdinalIgnoreCase))
            return Gmc300S;

        // Unknown models deliberately get the conservative capability set. Add a
        // model here only after its optional commands have been verified on hardware.
        return Unknown with { Model = version.Trim() };
    }
}
