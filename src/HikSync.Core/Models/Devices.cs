namespace HikSync.Core.Models;

/// <summary>Connection details for a single terminal.</summary>
public sealed class DeviceEndpoint
{
    public required string Ip { get; init; }
    public int Port { get; init; } = 8000;
    public string Username { get; init; } = "admin";
    public string Password { get; init; } = string.Empty;

    public override string ToString() => $"{Ip}:{Port}";
}

/// <summary>A location with its IN (enrollment master) and OUT terminals.</summary>
public sealed class DevicePair
{
    public long Id { get; init; }
    public required string Location { get; init; }
    public required DeviceEndpoint In { get; init; }
    public required DeviceEndpoint Out { get; init; }
    public bool Enabled { get; init; } = true;

    public IEnumerable<(DeviceRole Role, DeviceEndpoint Endpoint)> Devices()
    {
        yield return (DeviceRole.In, In);
        yield return (DeviceRole.Out, Out);
    }
}

/// <summary>Identity read from a terminal (used to warn on model/firmware mismatch before sync).</summary>
public sealed class DeviceInfo
{
    public string Model { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string FirmwareVersion { get; init; } = string.Empty;
}
