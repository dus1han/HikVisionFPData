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

    /// <summary>
    /// Same as <see cref="MarkAttemptFailedAsync(IReadOnlyCollection{string}, string, int, CancellationToken)"/>
    /// but records a distinct reason per row, so <c>last_upload_error</c> carries the destination's
    /// actual message for that punch rather than one shared summary.
    /// </summary>
    Task MarkAttemptFailedAsync(IReadOnlyDictionary<string, string> errorsByKey, int maxAttempts, CancellationToken ct);

    Task<int> CountPendingAsync(CancellationToken ct);
}

public interface ISyncStateRepository
{
    Task<SyncState?> GetAsync(long pairId, CancellationToken ct);
    Task UpsertAsync(SyncState state, CancellationToken ct);
}

public interface ISyncFailureRepository
{
    /// <summary>Records each failure, incrementing the attempt count for one already seen.</summary>
    Task UpsertAsync(IReadOnlyCollection<SyncFailure> failures, CancellationToken ct);
}

public interface IDeviceEnrollmentRepository
{
    /// <summary>Replaces a device's roster wholesale so the table mirrors the device, deletions included.</summary>
    Task ReplaceForDeviceAsync(string deviceIp, IReadOnlyCollection<DeviceEnrollment> rows, CancellationToken ct);
}

/// <summary>
/// Writes fingerprint templates to a device over the HCNetSDK. The SDK is native code that can
/// hard-crash its host process, so the implementation runs it OUT OF PROCESS — a crash kills only the
/// child, never the service. Returns one result per print (crash/timeout ⇒ all failed).
/// </summary>
public interface ISdkFingerprintWriter
{
    Task<IReadOnlyList<FingerprintWriteResult>> WriteAsync(
        DeviceEndpoint target, IReadOnlyList<FingerprintTemplate> prints, CancellationToken ct);
}

public interface IOperationLogRepository
{
    Task WriteAsync(OperationLog entry, CancellationToken ct);

    /// <summary>Delete log rows older than the cutoff. Returns the number removed.</summary>
    Task<int> DeleteBeforeAsync(DateTime cutoffUtc, CancellationToken ct);
}
