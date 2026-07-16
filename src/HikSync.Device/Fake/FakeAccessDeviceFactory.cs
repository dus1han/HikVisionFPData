using System.Collections.Concurrent;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;

namespace HikSync.Device.Fake;

/// <summary>
/// Creates and caches <see cref="FakeAccessDevice"/> instances per IP, so state persists across
/// connects (an OUT device keeps what sync wrote to it). IN devices are seeded with sample
/// users/fingerprints so a sync run has something to copy.
/// </summary>
public sealed class FakeAccessDeviceFactory : IAccessDeviceFactory
{
    private readonly ConcurrentDictionary<string, FakeAccessDevice> _devices = new();

    public Task<IAccessDevice> ConnectAsync(DeviceEndpoint endpoint, CancellationToken ct)
    {
        var device = _devices.GetOrAdd(endpoint.Ip, _ => Create(endpoint));
        return Task.FromResult<IAccessDevice>(device);
    }

    /// <summary>Register a pre-seeded device (used by tests).</summary>
    public FakeAccessDevice Register(FakeAccessDevice device)
    {
        _devices[device.Endpoint.Ip] = device;
        return device;
    }

    private static FakeAccessDevice Create(DeviceEndpoint endpoint)
    {
        var info = new DeviceInfo
        {
            Model = "DS-K1A8503MF-B",
            SerialNumber = $"FAKE-{endpoint.Ip}",
            FirmwareVersion = "V0.0-fake",
        };
        var device = new FakeAccessDevice(endpoint, info);

        // Seed the "IN" side of a typical lab layout so sync has data to copy.
        // (Heuristic: seed every device; OUT starts identical-empty and receives via sync.)
        if (endpoint.Port % 2 == 0)
        {
            device.SeedUser(
                new DeviceUser { EmployeeNo = "1001", Name = "Sample One", UserType = "normal", Enabled = true },
                new FingerprintTemplate { EmployeeNo = "1001", FingerIndex = 1, Template = new byte[] { 1, 2, 3, 4 } });
        }

        return device;
    }
}
