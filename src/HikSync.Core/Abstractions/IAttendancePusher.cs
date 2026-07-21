using HikSync.Core.Models;

namespace HikSync.Core.Abstractions;

/// <summary>
/// Pushes captured rows to the destination (a central Postgres table — see REQUIREMENTS §8).
/// The push implementation is deliberately deferred; a no-op stub is used until the central
/// contract is finalized. Returns the idempotency keys that were durably accepted.
/// </summary>
public interface IAttendancePusher
{
    bool Enabled { get; }

    Task<PushResult> PushAsync(IReadOnlyList<AttendanceRecord> batch, CancellationToken ct);
}

public sealed record PushResult(IReadOnlyList<string> AcceptedKeys, IReadOnlyList<string> RejectedKeys)
{
    /// <summary>
    /// Why each rejected key was rejected, keyed by idempotency key. Recorded per row in
    /// <c>attendance_events.last_upload_error</c> so an operator can see the actual reason
    /// ("no employee with em_number '999'") instead of a generic failure message.
    /// Null when the destination gave no per-record detail.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Errors { get; init; }

    public static PushResult None { get; } = new(Array.Empty<string>(), Array.Empty<string>());
}
