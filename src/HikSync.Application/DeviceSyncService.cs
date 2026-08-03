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
    private readonly ISyncFailureRepository _syncFailures;
    private readonly IDeviceEnrollmentRepository _enrollment;
    private readonly IAccessDeviceFactory _factory;
    private readonly ISdkFingerprintWriter _sdkFingerprints;
    private readonly OperationLogger _log;
    private readonly SyncOptions _options;
    private readonly HealthState _health;
    private readonly ILogger<DeviceSyncService> _logger;

    /// <summary>True when fingerprint writes should go over the HCNetSDK rather than the primary transport.</summary>
    private bool UseSdkForFingerprints =>
        string.Equals(_options.FingerprintTransport, "sdk", StringComparison.OrdinalIgnoreCase);

    public DeviceSyncService(
        IDevicePairRepository pairs,
        ISyncStateRepository syncState,
        ISyncFailureRepository syncFailures,
        IDeviceEnrollmentRepository enrollment,
        IAccessDeviceFactory factory,
        ISdkFingerprintWriter sdkFingerprints,
        OperationLogger log,
        IOptions<SyncOptions> options,
        HealthState health,
        ILogger<DeviceSyncService> logger)
    {
        _pairs = pairs;
        _syncState = syncState;
        _syncFailures = syncFailures;
        _enrollment = enrollment;
        _factory = factory;
        _sdkFingerprints = sdkFingerprints;
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

            // Snapshot the full roster of each device (before the fingerprint filter below, so users
            // without a fingerprint are still recorded). Best-effort — a snapshot failure must not
            // break the sync itself.
            await SnapshotEnrollmentAsync(pair, inUsers, inFps, outUsers, outFps, ct);

            // Optionally restrict to users that actually have a fingerprint enrolled (both sides).
            if (_options.OnlyUsersWithFingerprints)
            {
                var inWithFp = new HashSet<string>(inFps.Select(f => f.EmployeeNo), StringComparer.Ordinal);
                inUsers = inUsers.Where(u => inWithFp.Contains(u.EmployeeNo)).ToList();
                var outWithFp = new HashSet<string>(outFps.Select(f => f.EmployeeNo), StringComparer.Ordinal);
                outUsers = outUsers.Where(u => outWithFp.Contains(u.EmployeeNo)).ToList();
            }

            string summary;
            var fails = new List<SyncFailure>();
            if (_options.Bidirectional)
            {
                // Union: give each device whatever the other has that it's missing.
                var toOut = SyncPlanner.BuildMissingOnly(inUsers, inFps, outUsers, outFps);
                var toIn = SyncPlanner.BuildMissingOnly(outUsers, outFps, inUsers, inFps);

                fails.AddRange(await ApplyAsync(outDevice, pair.Id, pair.In.Ip, pair.Out.Ip, toOut, ct));
                fails.AddRange(await ApplyAsync(inDevice, pair.Id, pair.Out.Ip, pair.In.Ip, toIn, ct));
                fails.AddRange(await SdkWriteFingerprintsAsync(pair.Out, pair.Id, pair.In.Ip, toOut.FingerprintsToUpsert, ct));
                fails.AddRange(await SdkWriteFingerprintsAsync(pair.In, pair.Id, pair.Out.Ip, toIn.FingerprintsToUpsert, ct));

                summary = $"union: -> OUT users +{toOut.UsersToUpsert.Count}, fp +{toOut.FingerprintsToUpsert.Count}; " +
                          $"-> IN users +{toIn.UsersToUpsert.Count}, fp +{toIn.FingerprintsToUpsert.Count} " +
                          $"(IN {inUsers.Count} users/{inFps.Count} fp, OUT {outUsers.Count} users/{outFps.Count} fp)" +
                          (fails.Count > 0 ? $" [{fails.Count} item(s) failed]" : "");
            }
            else
            {
                // Legacy one-way: IN is master, OUT mirrors it.
                var plan = SyncPlanner.Build(inUsers, inFps, outUsers, outFps, _options.DeleteRemovedUsers);
                fails.AddRange(await ApplyAsync(outDevice, pair.Id, pair.In.Ip, pair.Out.Ip, plan, ct));
                fails.AddRange(await SdkWriteFingerprintsAsync(pair.Out, pair.Id, pair.In.Ip, plan.FingerprintsToUpsert, ct));
                summary = $"one-way IN->OUT: users +{plan.UsersToUpsert.Count}, fingerprints +{plan.FingerprintsToUpsert.Count}, " +
                          $"deletes {plan.EmployeesToDelete.Count} (IN {inUsers.Count} users / OUT {outUsers.Count} users)" +
                          (fails.Count > 0 ? $" [{fails.Count} item(s) failed]" : "");
            }

            if (fails.Count > 0)
                await _syncFailures.UpsertAsync(fails, ct);

            await _syncState.UpsertAsync(new SyncState
            {
                PairId = pair.Id,
                LastSyncAtUtc = DateTime.UtcNow,
                InUserCount = inUsers.Count,
                OutUserCount = outUsers.Count,
                LastStatus = fails.Count == 0 ? "ok" : "partial",
                LastError = fails.Count == 0 ? null : $"{fails.Count} item(s) failed",
            }, ct);

            await _log.LogAsync(pair.Out.Ip, DeviceRole.Out, LogOperation.Sync, LogStatus.Ok, summary, ct, pair.Id, (int)sw.ElapsedMilliseconds);
            _logger.LogInformation("Pair {Location} synced: {Summary}", pair.Location, summary);
        }
        finally
        {
            if (outDevice is not null) await DisconnectLoggedAsync(outDevice, pair.Out.Ip, DeviceRole.Out, pair, ct);
            await DisconnectLoggedAsync(inDevice, pair.In.Ip, DeviceRole.In, pair, ct);
        }
    }

    /// <summary>
    /// Writes fingerprints to a device over the SDK, out of process, when configured. Returns a failure
    /// per print the writer could not apply (crash/timeout ⇒ all, retried next cycle). No-op when the
    /// primary transport handles fingerprints (they were already written by ApplyAsync).
    /// </summary>
    private async Task<List<SyncFailure>> SdkWriteFingerprintsAsync(
        DeviceEndpoint target, long pairId, string sourceIp, IReadOnlyList<FingerprintTemplate> prints, CancellationToken ct)
    {
        var failures = new List<SyncFailure>();
        if (!UseSdkForFingerprints || !_options.SyncFingerprints || prints.Count == 0) return failures;

        var sdkEndpoint = new DeviceEndpoint
        {
            Ip = target.Ip,
            Port = _options.SdkPort,
            Username = target.Username,
            Password = target.Password,
        };

        var results = await _sdkFingerprints.WriteAsync(sdkEndpoint, prints, ct);
        var byKey = prints.ToDictionary(p => (p.EmployeeNo, p.FingerIndex));
        foreach (var r in results.Where(r => !r.Ok))
        {
            _logger.LogWarning("SDK: could not write fingerprint for {EmployeeNo} (finger {Finger}) on {Ip}: {Error}",
                r.EmployeeNo, r.FingerIndex, target.Ip, r.Error);
            failures.Add(new SyncFailure
            {
                PairId = pairId,
                SourceIp = sourceIp,
                TargetIp = target.Ip,
                EmployeeNo = r.EmployeeNo,
                FingerIndex = r.FingerIndex,
                Operation = "fingerprint",
                Error = r.Error,
            });
        }
        return failures;
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

    /// <summary>Applies a plan to one device: users first (a fingerprint references its user), then fingerprints, then deletes.</summary>
    /// <summary>
    /// Applies a plan to one device. Each item is attempted independently: a single device rejection
    /// (e.g. one malformed fingerprint the firmware refuses with a 400) is logged and skipped so it
    /// cannot abort the whole batch. Previously the first failure stopped every later upsert, which
    /// meant one bad fingerprint left every employee behind it unsynced — so they could punch on the
    /// device they were enrolled on but not on its partner. Returns the number of failed items.
    /// Cancellation is not swallowed: it propagates so shutdown still stops the loop promptly.
    /// </summary>
    private async Task<List<SyncFailure>> ApplyAsync(
        IAccessDevice target, long pairId, string sourceIp, string targetIp, SyncPlan plan, CancellationToken ct)
    {
        var failures = new List<SyncFailure>();

        foreach (var user in plan.UsersToUpsert)
        {
            try { await target.UpsertUserAsync(user, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Sync: could not upsert user {EmployeeNo} on {Ip}; skipping.", user.EmployeeNo, targetIp);
                failures.Add(Fail(pairId, sourceIp, targetIp, user.EmployeeNo, 0, "user", ex));
            }
        }

        // Fingerprints via the primary transport (ISAPI). When FingerprintTransport=sdk they are written
        // separately, out of process, by SdkWriteFingerprintsAsync — skip them here. Users are upserted
        // first either way: a fingerprint write references its user.
        if (_options.SyncFingerprints && !UseSdkForFingerprints)
            foreach (var fp in plan.FingerprintsToUpsert)
            {
                try { await target.UpsertFingerprintAsync(fp, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Sync: could not upsert fingerprint for {EmployeeNo} (finger {Finger}) on {Ip}; skipping.",
                        fp.EmployeeNo, fp.FingerIndex, targetIp);
                    failures.Add(Fail(pairId, sourceIp, targetIp, fp.EmployeeNo, fp.FingerIndex, "fingerprint", ex));
                }
            }

        foreach (var employeeNo in plan.EmployeesToDelete)
        {
            try { await target.DeleteUserAsync(employeeNo, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Sync: could not delete user {EmployeeNo} on {Ip}; skipping.", employeeNo, targetIp);
                failures.Add(Fail(pairId, sourceIp, targetIp, employeeNo, 0, "delete", ex));
            }
        }

        return failures;
    }

    private async Task SnapshotEnrollmentAsync(
        DevicePair pair, List<DeviceUser> inUsers, List<FingerprintTemplate> inFps,
        List<DeviceUser> outUsers, List<FingerprintTemplate> outFps, CancellationToken ct)
    {
        try
        {
            await _enrollment.ReplaceForDeviceAsync(pair.In.Ip,
                BuildEnrollment(pair.Id, pair.In.Ip, pair.Location, DeviceRole.In, inUsers, inFps), ct);
            await _enrollment.ReplaceForDeviceAsync(pair.Out.Ip,
                BuildEnrollment(pair.Id, pair.Out.Ip, pair.Location, DeviceRole.Out, outUsers, outFps), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to snapshot device enrollment for pair {Location}.", pair.Location);
        }
    }

    private static List<DeviceEnrollment> BuildEnrollment(
        long pairId, string ip, string location, DeviceRole role,
        List<DeviceUser> users, List<FingerprintTemplate> fps)
    {
        var fingersByEmp = fps
            .GroupBy(f => f.EmployeeNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.FingerIndex).Distinct().OrderBy(x => x).ToArray(), StringComparer.Ordinal);

        var rows = new List<DeviceEnrollment>(users.Count);
        foreach (var u in users)
        {
            var fingers = fingersByEmp.TryGetValue(u.EmployeeNo, out var f) ? f : Array.Empty<int>();
            rows.Add(new DeviceEnrollment
            {
                PairId = pairId,
                DeviceIp = ip,
                Location = location,
                Role = role,
                EmployeeNo = u.EmployeeNo,
                Name = u.Name,
                Enabled = u.Enabled,
                FingerprintCount = fingers.Length,
                FingerIds = fingers,
            });
        }
        return rows;
    }

    private static SyncFailure Fail(long pairId, string sourceIp, string targetIp, string employeeNo, int finger, string op, Exception ex) =>
        new()
        {
            PairId = pairId,
            SourceIp = sourceIp,
            TargetIp = targetIp,
            EmployeeNo = employeeNo,
            FingerIndex = finger,
            Operation = op,
            Error = ex.Message,
        };

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
