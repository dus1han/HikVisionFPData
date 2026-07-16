using HikSync.Core.Models;

namespace HikSync.Core.Abstractions;

/// <summary>
/// A live connection (login session) to a single terminal. Implementations wrap HCNetSDK.
/// Dispose releases the login handle.
/// </summary>
public interface IAccessDevice : IAsyncDisposable
{
    DeviceEndpoint Endpoint { get; }

    Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct);

    /// <summary>Read access events within the query window (device search is time-range based).</summary>
    IAsyncEnumerable<AccessEvent> ReadEventsAsync(AcsEventQuery query, CancellationToken ct);

    IAsyncEnumerable<DeviceUser> ReadUsersAsync(CancellationToken ct);

    IAsyncEnumerable<FingerprintTemplate> ReadFingerprintsAsync(CancellationToken ct);

    Task UpsertUserAsync(DeviceUser user, CancellationToken ct);

    Task UpsertFingerprintAsync(FingerprintTemplate fingerprint, CancellationToken ct);

    Task DeleteUserAsync(string employeeNo, CancellationToken ct);
}

/// <summary>Opens connections to terminals. One implementation per backend (HCNetSDK, fake).</summary>
public interface IAccessDeviceFactory
{
    Task<IAccessDevice> ConnectAsync(DeviceEndpoint endpoint, CancellationToken ct);
}
