using System.Diagnostics;
using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Logic;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Application;

/// <summary>
/// Syncs users + fingerprints from the IN (master) terminal to the OUT terminal of each pair.
/// </summary>
public sealed class DeviceSyncService
{
    private readonly IDevicePairRepository _pairs;
    private readonly ISyncStateRepository _syncState;
    private readonly IAccessDeviceFactory _factory;
    private readonly OperationLogger _log;
    private readonly SyncOptions _options;
    private readonly HealthState _health;
    private readonly ILogger<DeviceSyncService> _logger;

    public DeviceSyncService(
        IDevicePairRepository pairs,
        ISyncStateRepository syncState,
        IAccessDeviceFactory factory,
        OperationLogger log,
        IOptions<SyncOptions> options,
        HealthState health,
        ILogger<DeviceSyncService> logger)
    {
        _pairs = pairs;
        _syncState = syncState;
        _factory = factory;
        _log = log;
        _options = options.Value;
        _health = health;
        _logger = logger;
    }

    public async Task SyncAllAsync(CancellationToken ct)
    {
        var pairs = await _pairs.GetEnabledPairsAsync(ct);
        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SyncPairAsync(pair, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Sync failed for pair {Location} ({In} -> {Out}).", pair.Location, pair.In, pair.Out);
                await _log.LogAsync(pair.In.Ip, DeviceRole.In, LogOperation.Error, LogStatus.Error, ex.Message, ct, pair.Id);
                await _syncState.UpsertAsync(new SyncState
                {
                    PairId = pair.Id,
                    LastSyncAtUtc = DateTime.UtcNow,
                    LastStatus = "error",
                    LastError = ex.Message,
                }, ct);
            }
        }

        _health.LastSyncSuccessUtc = DateTime.UtcNow;
    }

    private async Task SyncPairAsync(DevicePair pair, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var inDevice = await ConnectLoggedAsync(pair.In.Ip, DeviceRole.In, pair, ct);
        IAccessDevice? outDevice = null;
        try
        {
            outDevice = await ConnectLoggedAsync(pair.Out.Ip, DeviceRole.Out, pair, ct);

            var inInfo = await inDevice.GetDeviceInfoAsync(ct);
            var outInfo = await outDevice.GetDeviceInfoAsync(ct);
            if (!string.Equals(inInfo.Model, outInfo.Model, StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("Pair {Location}: model mismatch IN='{In}' OUT='{Out}'. Fingerprint templates may not transfer.",
                    pair.Location, inInfo.Model, outInfo.Model);

            var inUsers = await ReadAllAsync(inDevice.ReadUsersAsync(ct));
            var inFps = await ReadAllAsync(inDevice.ReadFingerprintsAsync(ct));
            var outUsers = await ReadAllAsync(outDevice.ReadUsersAsync(ct));
            var outFps = await ReadAllAsync(outDevice.ReadFingerprintsAsync(ct));

            // Optionally sync only users that have a fingerprint enrolled.
            if (_options.OnlyUsersWithFingerprints)
            {
                var withFp = new HashSet<string>(inFps.Select(f => f.EmployeeNo), StringComparer.Ordinal);
                inUsers = inUsers.Where(u => withFp.Contains(u.EmployeeNo)).ToList();
            }

            var plan = SyncPlanner.Build(inUsers, inFps, outUsers, outFps, _options.DeleteRemovedUsers);

            // Users first (fingerprints reference the user), then fingerprints, then deletes.
            foreach (var user in plan.UsersToUpsert)
                await outDevice.UpsertUserAsync(user, ct);
            foreach (var fp in plan.FingerprintsToUpsert)
                await outDevice.UpsertFingerprintAsync(fp, ct);
            foreach (var employeeNo in plan.EmployeesToDelete)
                await outDevice.DeleteUserAsync(employeeNo, ct);

            await _syncState.UpsertAsync(new SyncState
            {
                PairId = pair.Id,
                LastSyncAtUtc = DateTime.UtcNow,
                InUserCount = inUsers.Count,
                OutUserCount = outUsers.Count,
                LastStatus = "ok",
                LastError = null,
            }, ct);

            string summary = $"users +{plan.UsersToUpsert.Count}, fingerprints +{plan.FingerprintsToUpsert.Count}, " +
                             $"deletes {plan.EmployeesToDelete.Count} (IN {inUsers.Count} users / OUT {outUsers.Count} users)";
            await _log.LogAsync(pair.Out.Ip, DeviceRole.Out, LogOperation.Sync, LogStatus.Ok, summary, ct, pair.Id, (int)sw.ElapsedMilliseconds);
            _logger.LogInformation("Pair {Location} synced: {Summary}", pair.Location, summary);
        }
        finally
        {
            if (outDevice is not null) await DisconnectLoggedAsync(outDevice, pair.Out.Ip, DeviceRole.Out, pair, ct);
            await DisconnectLoggedAsync(inDevice, pair.In.Ip, DeviceRole.In, pair, ct);
        }
    }

    private async Task<IAccessDevice> ConnectLoggedAsync(string ip, DeviceRole role, DevicePair pair, CancellationToken ct)
    {
        var endpoint = role == DeviceRole.In ? pair.In : pair.Out;
        await _log.LogAsync(ip, role, LogOperation.Connect, LogStatus.Info, "connecting", ct, pair.Id);
        try
        {
            var device = await _factory.ConnectAsync(endpoint, ct);
            await _log.LogAsync(ip, role, LogOperation.Connect, LogStatus.Ok, "connected", ct, pair.Id);
            return device;
        }
        catch (Exception ex)
        {
            await _log.LogAsync(ip, role, LogOperation.Error, LogStatus.Error, $"connect failed: {ex.Message}", ct, pair.Id);
            throw;
        }
    }

    private async Task DisconnectLoggedAsync(IAccessDevice device, string ip, DeviceRole role, DevicePair pair, CancellationToken ct)
    {
        await device.DisposeAsync();
        await _log.LogAsync(ip, role, LogOperation.Disconnect, LogStatus.Ok, "disconnected", ct, pair.Id);
    }

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
