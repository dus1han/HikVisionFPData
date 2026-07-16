using System.Runtime.CompilerServices;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;

namespace HikSync.Device.Fake;

/// <summary>
/// In-memory terminal for development/testing without hardware. Holds its own users,
/// fingerprints and events so IN/OUT pairs behave independently and sync can be observed.
/// </summary>
public sealed class FakeAccessDevice : IAccessDevice
{
    private readonly object _gate = new();
    private readonly List<AccessEvent> _events = new();
    private readonly Dictionary<string, DeviceUser> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, int), FingerprintTemplate> _fingerprints = new();

    public FakeAccessDevice(DeviceEndpoint endpoint, DeviceInfo info)
    {
        Endpoint = endpoint;
        Info = info;
    }

    public DeviceEndpoint Endpoint { get; }
    public DeviceInfo Info { get; }

    public void SeedEvents(IEnumerable<AccessEvent> events)
    {
        lock (_gate) _events.AddRange(events);
    }

    public void SeedUser(DeviceUser user, params FingerprintTemplate[] fingerprints)
    {
        lock (_gate)
        {
            _users[user.EmployeeNo] = user;
            foreach (var fp in fingerprints) _fingerprints[fp.Key] = fp;
        }
    }

    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct) => Task.FromResult(Info);

    public async IAsyncEnumerable<AccessEvent> ReadEventsAsync(
        AcsEventQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        List<AccessEvent> snapshot;
        lock (_gate)
        {
            snapshot = _events
                .Where(e => e.EventTimeUtc >= query.StartUtc && e.EventTimeUtc <= query.EndUtc)
                .OrderBy(e => e.EventTimeUtc).ThenBy(e => e.SerialNo)
                .ToList();
        }

        foreach (var e in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<DeviceUser> ReadUsersAsync([EnumeratorCancellation] CancellationToken ct)
    {
        List<DeviceUser> snapshot;
        lock (_gate) snapshot = _users.Values.ToList();
        foreach (var u in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return u;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<FingerprintTemplate> ReadFingerprintsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        List<FingerprintTemplate> snapshot;
        lock (_gate) snapshot = _fingerprints.Values.ToList();
        foreach (var f in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return f;
            await Task.Yield();
        }
    }

    public Task UpsertUserAsync(DeviceUser user, CancellationToken ct)
    {
        lock (_gate) _users[user.EmployeeNo] = user;
        return Task.CompletedTask;
    }

    public Task UpsertFingerprintAsync(FingerprintTemplate fingerprint, CancellationToken ct)
    {
        lock (_gate) _fingerprints[fingerprint.Key] = fingerprint;
        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(string employeeNo, CancellationToken ct)
    {
        lock (_gate)
        {
            _users.Remove(employeeNo);
            foreach (var key in _fingerprints.Keys.Where(k => k.Item1 == employeeNo).ToList())
                _fingerprints.Remove(key);
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
