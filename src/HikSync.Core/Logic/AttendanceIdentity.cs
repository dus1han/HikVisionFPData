using HikSync.Core.Models;

namespace HikSync.Core.Logic;

/// <summary>
/// Builds the reset-immune identity for an access event. Anchoring on
/// (device, employee, time, major, minor) — NOT the device serial — keeps identity
/// stable across a device event-log/factory reset (which restarts the serial low).
/// </summary>
public static class AttendanceIdentity
{
    public static string ComputeKey(string deviceIp, string employeeNo, DateTime eventTimeUtc, int major, int minor)
    {
        long unix = new DateTimeOffset(DateTime.SpecifyKind(eventTimeUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return $"{deviceIp}:{employeeNo}:{unix}:{major}:{minor}";
    }

    public static string ComputeKey(string deviceIp, AccessEvent e) =>
        ComputeKey(deviceIp, e.EmployeeNo, e.EventTimeUtc, e.Major, e.Minor);
}
