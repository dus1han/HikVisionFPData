using HikSync.Core.Models;

namespace HikSync.Core.Abstractions;

public interface IDevicePairRepository
{
    Task<IReadOnlyList<DevicePair>> GetEnabledPairsAsync(CancellationToken ct);
}

public interface IWatermarkRepository
{
    Task<FetchWatermark?> GetAsync(string deviceIp, CancellationToken ct);
    Task UpsertAsync(FetchWatermark watermark, CancellationToken ct);
}

public interface IAttendanceRepository
{
    /// <summary>Insert rows, ignoring duplicates on the idempotency key. Returns the count actually inserted.</summary>
    Task<int> InsertIgnoreAsync(IReadOnlyCollection<AttendanceRecord> records, CancellationToken ct);

    Task<IReadOnlyList<AttendanceRecord>> GetPendingAsync(int limit, CancellationToken ct);

    Task MarkUploadedAsync(IReadOnlyCollection<string> idempotencyKeys, CancellationToken ct);

    Task MarkAttemptFailedAsync(IReadOnlyCollection<string> idempotencyKeys, string error, int maxAttempts, CancellationToken ct);

    Task<int> CountPendingAsync(CancellationToken ct);
}

public interface ISyncStateRepository
{
    Task<SyncState?> GetAsync(long pairId, CancellationToken ct);
    Task UpsertAsync(SyncState state, CancellationToken ct);
}

public interface IOperationLogRepository
{
    Task WriteAsync(OperationLog entry, CancellationToken ct);

    /// <summary>Delete log rows older than the cutoff. Returns the number removed.</summary>
    Task<int> DeleteBeforeAsync(DateTime cutoffUtc, CancellationToken ct);
}
