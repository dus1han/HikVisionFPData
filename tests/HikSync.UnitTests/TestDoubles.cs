using HikSync.Core.Abstractions;
using HikSync.Core.Models;

namespace HikSync.UnitTests;

internal sealed class InMemoryDevicePairRepository : IDevicePairRepository
{
    private readonly IReadOnlyList<DevicePair> _pairs;
    public InMemoryDevicePairRepository(params DevicePair[] pairs) => _pairs = pairs;
    public Task<IReadOnlyList<DevicePair>> GetEnabledPairsAsync(CancellationToken ct) =>
        Task.FromResult(_pairs);
}

internal sealed class InMemoryWatermarkRepository : IWatermarkRepository
{
    public Dictionary<string, FetchWatermark> Store { get; } = new(StringComparer.Ordinal);

    public Task<FetchWatermark?> GetAsync(string deviceIp, CancellationToken ct) =>
        Task.FromResult(Store.TryGetValue(deviceIp, out var w) ? w : null);

    public Task UpsertAsync(FetchWatermark watermark, CancellationToken ct)
    {
        Store[watermark.DeviceIp] = watermark;
        return Task.CompletedTask;
    }
}

/// <summary>Mimics the Postgres unique-key dedup: inserts ignore rows whose idempotency key exists.</summary>
internal sealed class InMemoryAttendanceRepository : IAttendanceRepository
{
    public List<AttendanceRecord> Rows { get; } = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public Task<int> InsertIgnoreAsync(IReadOnlyCollection<AttendanceRecord> records, CancellationToken ct)
    {
        int inserted = 0;
        foreach (var r in records)
        {
            if (_keys.Add(r.IdempotencyKey))
            {
                Rows.Add(r);
                inserted++;
            }
        }
        return Task.FromResult(inserted);
    }

    public Task<IReadOnlyList<AttendanceRecord>> GetPendingAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AttendanceRecord>>(
            Rows.Where(r => r.UploadStatus == UploadStatus.Pending).OrderBy(r => r.EventTimeUtc).Take(limit).ToList());

    public Task MarkUploadedAsync(IReadOnlyCollection<string> idempotencyKeys, CancellationToken ct)
    {
        var set = idempotencyKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var r in Rows.Where(r => set.Contains(r.IdempotencyKey)))
            r.UploadStatus = UploadStatus.Uploaded;
        return Task.CompletedTask;
    }

    public Task MarkAttemptFailedAsync(IReadOnlyCollection<string> keys, string error, int maxAttempts, CancellationToken ct)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var r in Rows.Where(r => set.Contains(r.IdempotencyKey)))
        {
            r.UploadAttempts++;
            r.LastUploadError = error;
            if (r.UploadAttempts >= maxAttempts) r.UploadStatus = UploadStatus.DeadLetter;
        }
        return Task.CompletedTask;
    }

    public Task MarkAttemptFailedAsync(IReadOnlyDictionary<string, string> errorsByKey, int maxAttempts, CancellationToken ct)
    {
        foreach (var r in Rows.Where(r => errorsByKey.ContainsKey(r.IdempotencyKey)))
        {
            r.UploadAttempts++;
            r.LastUploadError = errorsByKey[r.IdempotencyKey];
            if (r.UploadAttempts >= maxAttempts) r.UploadStatus = UploadStatus.DeadLetter;
        }
        return Task.CompletedTask;
    }

    public Task<int> CountPendingAsync(CancellationToken ct) =>
        Task.FromResult(Rows.Count(r => r.UploadStatus == UploadStatus.Pending));
}

internal sealed class InMemoryOperationLogRepository : IOperationLogRepository
{
    public List<OperationLog> Entries { get; } = new();

    public Task WriteAsync(OperationLog entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<int> DeleteBeforeAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        int removed = Entries.RemoveAll(e => e.LoggedAtUtc < cutoffUtc);
        return Task.FromResult(removed);
    }
}

internal sealed class InMemorySyncStateRepository : ISyncStateRepository
{
    public Dictionary<long, SyncState> Store { get; } = new();
    public Task<SyncState?> GetAsync(long pairId, CancellationToken ct) =>
        Task.FromResult(Store.TryGetValue(pairId, out var s) ? s : null);
    public Task UpsertAsync(SyncState state, CancellationToken ct)
    {
        Store[state.PairId] = state;
        return Task.CompletedTask;
    }
}
