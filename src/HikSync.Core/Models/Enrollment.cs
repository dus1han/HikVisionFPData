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
