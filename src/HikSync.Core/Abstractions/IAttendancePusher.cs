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
    public static PushResult None { get; } = new(Array.Empty<string>(), Array.Empty<string>());
}
