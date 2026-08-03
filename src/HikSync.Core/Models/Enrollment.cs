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
    /// or coerceFP are alarm/duress fingers. Preserved so a synced print keeps its meaning; writing
    /// the wrong type is rejected as badParameters.
    /// </summary>
    public string FingerType { get; set; } = "normalFP";

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
