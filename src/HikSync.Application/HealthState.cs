namespace HikSync.Application;

/// <summary>Last-success timestamps + backlog, surfaced for health/observability.</summary>
public sealed class HealthState
{
    public DateTime? LastAttendanceSuccessUtc { get; set; }
    public DateTime? LastSyncSuccessUtc { get; set; }
    public DateTime? LastPushSuccessUtc { get; set; }
    public int PendingBacklog { get; set; }
    public int DevicesOffline { get; set; }
}
