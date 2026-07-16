using System.Diagnostics;
using System.Text.Json;
using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Logic;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Application;

/// <summary>
/// Pulls new access events from every terminal (IN and OUT of each pair), tags them by role,
/// and stores only previously-unseen rows in the local edge DB (dedup + serial cursor).
/// </summary>
public sealed class AttendanceCollector
{
    private readonly IDevicePairRepository _pairs;
    private readonly IWatermarkRepository _watermarks;
    private readonly IAttendanceRepository _attendance;
    private readonly IAccessDeviceFactory _factory;
    private readonly OperationLogger _log;
    private readonly AttendanceOptions _options;
    private readonly HealthState _health;
    private readonly ILogger<AttendanceCollector> _logger;
    private readonly HashSet<VerifyMode>? _countedModes;

    public AttendanceCollector(
        IDevicePairRepository pairs,
        IWatermarkRepository watermarks,
        IAttendanceRepository attendance,
        IAccessDeviceFactory factory,
        OperationLogger log,
        IOptions<AttendanceOptions> options,
        HealthState health,
        ILogger<AttendanceCollector> logger)
    {
        _pairs = pairs;
        _watermarks = watermarks;
        _attendance = attendance;
        _factory = factory;
        _log = log;
        _options = options.Value;
        _health = health;
        _logger = logger;
        _countedModes = BuildCountedModes(_options.CountedVerifyModes);
    }

    public async Task CollectAllAsync(CancellationToken ct)
    {
        var pairs = await _pairs.GetEnabledPairsAsync(ct);
        using var throttle = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentDevices));
        int offline = 0;
        int totalNew = 0;

        var tasks = pairs.SelectMany(p => p.Devices().Select(d => (Pair: p, d.Role, d.Endpoint)))
            .Select(async item =>
            {
                await throttle.WaitAsync(ct);
                try
                {
                    int inserted = await CollectDeviceAsync(item.Pair, item.Role, item.Endpoint, ct);
                    Interlocked.Add(ref totalNew, inserted);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref offline);
                    _logger.LogWarning(ex, "Attendance collection failed for {Role} device {Endpoint}.", item.Role, item.Endpoint);
                    await SafeMarkWatermarkAsync(item.Endpoint.Ip, "error", ex.Message, ct);
                }
                finally
                {
                    throttle.Release();
                }
            });

        await Task.WhenAll(tasks);

        _health.DevicesOffline = offline;
        _health.LastAttendanceSuccessUtc = DateTime.UtcNow;
        _health.PendingBacklog = await _attendance.CountPendingAsync(ct);
        _logger.LogInformation("Attendance cycle done: {New} new row(s), {Offline} device(s) unreachable, {Backlog} pending.",
            totalNew, offline, _health.PendingBacklog);
    }

    private async Task<int> CollectDeviceAsync(DevicePair pair, DeviceRole role, DeviceEndpoint endpoint, CancellationToken ct)
    {
        var watermark = await _watermarks.GetAsync(endpoint.Ip, ct);
        DateTime startUtc = watermark?.LastEventTimeUtc ?? _options.BackfillStartUtc ?? DateTime.UtcNow;
        DateTime endUtc = DateTime.UtcNow;
        var offset = TimeSpan.FromMinutes(_options.DeviceUtcOffsetMinutes ?? 0);

        var query = new AcsEventQuery { StartUtc = startUtc, EndUtc = endUtc, DeviceUtcOffset = offset };

        var batch = new List<AttendanceRecord>();
        DateTime maxTime = startUtc;
        long maxSerial = watermark?.LastSerialNo ?? 0;
        DateTime fetchedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        // Session logging: connect -> attendance -> disconnect, tagged with ip + IN/OUT role.
        await _log.LogAsync(endpoint.Ip, role, LogOperation.Connect, LogStatus.Info, "connecting", ct, pair.Id);

        IAccessDevice device;
        try
        {
            device = await _factory.ConnectAsync(endpoint, ct);
        }
        catch (Exception ex)
        {
            await _log.LogAsync(endpoint.Ip, role, LogOperation.Error, LogStatus.Error, $"connect failed: {ex.Message}", ct, pair.Id);
            throw;
        }
        await _log.LogAsync(endpoint.Ip, role, LogOperation.Connect, LogStatus.Ok, "connected", ct, pair.Id);

        try
        {
            await foreach (var e in device.ReadEventsAsync(query, ct))
            {
                if (_countedModes is not null && !_countedModes.Contains(e.VerifyMode))
                    continue;

                var eventUtc = DateTime.SpecifyKind(e.EventTimeUtc, DateTimeKind.Utc);
                batch.Add(new AttendanceRecord
                {
                    PairId = pair.Id,
                    DeviceIp = endpoint.Ip,
                    Role = role,
                    Location = pair.Location,
                    EmployeeNo = e.EmployeeNo,
                    CardNo = e.CardNo,
                    EventTimeUtc = eventUtc,
                    DeviceSerialNo = e.SerialNo,
                    Major = e.Major,
                    Minor = e.Minor,
                    VerifyMode = e.VerifyMode.ToString(),
                    RawJson = BuildRawJson(e),
                    FetchedAtUtc = fetchedAt,
                    IdempotencyKey = AttendanceIdentity.ComputeKey(endpoint.Ip, e.EmployeeNo, eventUtc, e.Major, e.Minor),
                });

                if (eventUtc > maxTime) maxTime = eventUtc;
                if (e.SerialNo > maxSerial) maxSerial = e.SerialNo;
            }

            int inserted = await _attendance.InsertIgnoreAsync(batch, ct);

            // Advance the cursor only after a clean window (we reached here without throwing).
            await _watermarks.UpsertAsync(new FetchWatermark
            {
                DeviceIp = endpoint.Ip,
                LastEventTimeUtc = maxTime,
                LastSerialNo = maxSerial,
                LastRunAtUtc = DateTime.UtcNow,
                LastStatus = "ok",
                LastError = null,
            }, ct);

            await _log.LogAsync(endpoint.Ip, role, LogOperation.Attendance, LogStatus.Ok,
                $"read {batch.Count}, new {inserted}", ct, pair.Id, (int)sw.ElapsedMilliseconds);

            if (inserted > 0)
                _logger.LogDebug("{Role} {Endpoint}: {Inserted}/{Read} new event(s).", role, endpoint, inserted, batch.Count);

            return inserted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _log.LogAsync(endpoint.Ip, role, LogOperation.Error, LogStatus.Error, ex.Message, ct, pair.Id);
            throw;
        }
        finally
        {
            await device.DisposeAsync();
            await _log.LogAsync(endpoint.Ip, role, LogOperation.Disconnect, LogStatus.Ok, "disconnected", ct, pair.Id);
        }
    }

    private async Task SafeMarkWatermarkAsync(string deviceIp, string status, string error, CancellationToken ct)
    {
        try
        {
            var existing = await _watermarks.GetAsync(deviceIp, ct) ?? new FetchWatermark { DeviceIp = deviceIp };
            existing.LastRunAtUtc = DateTime.UtcNow;
            existing.LastStatus = status;
            existing.LastError = error;
            await _watermarks.UpsertAsync(existing, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record watermark error for {DeviceIp}.", deviceIp);
        }
    }

    private static string BuildRawJson(AccessEvent e) => JsonSerializer.Serialize(new
    {
        serialNo = e.SerialNo,
        major = e.Major,
        minor = e.Minor,
        verifyMode = e.VerifyMode.ToString(),
        cardNo = e.CardNo,
        raw = e.Raw,
    });

    private static HashSet<VerifyMode>? BuildCountedModes(string[] names)
    {
        if (names.Length == 0) return null; // null => count any successful verification
        var set = new HashSet<VerifyMode>();
        foreach (var name in names)
            if (Enum.TryParse<VerifyMode>(name, ignoreCase: true, out var mode))
                set.Add(mode);
        return set.Count == 0 ? null : set;
    }
}
