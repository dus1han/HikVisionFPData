namespace HikSync.Core.Models;

/// <summary>A user record on a terminal.</summary>
public sealed class DeviceUser
{
    public string EmployeeNo { get; set; } = string.Empty;
    public string? Name { get; set; }
    public DateTime? ValidBeginUtc { get; set; }
    public DateTime? ValidEndUtc { get; set; }
    public string UserType { get; set; } = "normal";
    public bool Enabled { get; set; } = true;

    /// <summary>Value-equality used by the sync planner to detect changed users.</summary>
    public string SyncSignature() =>
        string.Join('|', EmployeeNo, Name ?? "", ValidBeginUtc?.Ticks ?? 0,
            ValidEndUtc?.Ticks ?? 0, UserType, Enabled);
}

/// <summary>One enrolled fingerprint (a user may have several).</summary>
public sealed class FingerprintTemplate
{
    public string EmployeeNo { get; set; } = string.Empty;

    /// <summary>Finger index 1..10.</summary>
    public int FingerIndex { get; set; }

    /// <summary>
    /// Device fingerprint type — normalFP for an attendance finger; special values like dismissingFP
    /// or coerceFP are alarm/duress fingers. The device fixes this when the record is CREATED and
    /// ignores it on a later update, so a record enrolled under the wrong type can only be corrected
    /// by removing it and writing it again.
    /// </summary>
    public string FingerType { get; set; } = "normalFP";

    /// <summary>
    /// True for an ordinary attendance finger. Only these are copied to a partner device: duress and
    /// alarm fingers are device-local security config and propagating them would arm the same alarm
    /// on a terminal the operator never configured for it. They still count as *enrolled* though —
    /// see <see cref="Logic.SyncPlanner.BuildMissingOnly"/>.
    /// </summary>
    public bool IsAttendanceFinger =>
        string.IsNullOrEmpty(FingerType) || FingerType.Equals("normalFP", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opaque template bytes, copied binary between compatible devices.</summary>
    public byte[] Template { get; set; } = Array.Empty<byte>();

    public (string, int) Key => (EmployeeNo, FingerIndex);
}

/// <summary>Sync bookkeeping for a pair.</summary>
public sealed class SyncState
{
    public long PairId { get; set; }
    public DateTime? LastSyncAtUtc { get; set; }
    public int InUserCount { get; set; }
    public int OutUserCount { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Outcome of one fingerprint write attempted by the out-of-process SDK writer.</summary>
public sealed class FingerprintWriteResult
{
    public string EmployeeNo { get; set; } = string.Empty;
    public int FingerIndex { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>A device's view of one enrolled user, snapshotted into device_enrollment each sync.</summary>
public sealed class DeviceEnrollment
{
    public long PairId { get; set; }
    public string DeviceIp { get; set; } = string.Empty;
    public string? Location { get; set; }
    public DeviceRole Role { get; set; }
    public string EmployeeNo { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Enabled { get; set; }
    public int FingerprintCount { get; set; }
    public int[] FingerIds { get; set; } = Array.Empty<int>();
}

/// <summary>
/// One item the sync could not apply to a device, recorded so it is queryable from the DB rather
/// than only the log file. <see cref="TargetIp"/> is the device the write failed on;
/// <see cref="SourceIp"/> is where the record came from.
/// </summary>
public sealed class SyncFailure
{
    public long PairId { get; set; }
    public string? SourceIp { get; set; }
    public string TargetIp { get; set; } = string.Empty;
    public string EmployeeNo { get; set; } = string.Empty;

    /// <summary>1..10 for a fingerprint; 0 for user/delete operations.</summary>
    public int FingerIndex { get; set; }

    /// <summary>user | fingerprint | delete</summary>
    public string Operation { get; set; } = string.Empty;

    public string? Error { get; set; }
}
