namespace HikSync.Core.Models;

/// <summary>Well-known operation names for the audit log (free-form; these are the common ones).</summary>
public static class LogOperation
{
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string Attendance = "attendance";
    public const string Sync = "sync";
    public const string Push = "push";
    public const string Error = "error";
    public const string Cleanup = "cleanup";
}

public static class LogStatus
{
    public const string Ok = "ok";
    public const string Info = "info";
    public const string Error = "error";
}

/// <summary>
/// One audit row: a device transaction (connect ... operation ... disconnect) or a service event,
/// tagged with the device IP and IN/OUT role.
/// </summary>
public sealed class OperationLog
{
    public long Id { get; set; }
    public DateTime LoggedAtUtc { get; set; }
    public string? DeviceIp { get; set; }
    public DeviceRole? Role { get; set; }
    public long? PairId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? DurationMs { get; set; }
}
