using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Application;

/// <summary>
/// Drains locally-buffered 'pending' rows to the pusher and marks results. While the central
/// push is set aside the injected pusher is a no-op, so this simply reports the backlog.
/// </summary>
public sealed class PushService
{
    private readonly IAttendanceRepository _attendance;
    private readonly IAttendancePusher _pusher;
    private readonly PushOptions _options;
    private readonly HealthState _health;
    private readonly ILogger<PushService> _logger;

    public PushService(
        IAttendanceRepository attendance,
        IAttendancePusher pusher,
        IOptions<PushOptions> options,
        HealthState health,
        ILogger<PushService> logger)
    {
        _attendance = attendance;
        _pusher = pusher;
        _options = options.Value;
        _health = health;
        _logger = logger;
    }

    public async Task PushPendingAsync(CancellationToken ct)
    {
        if (!_pusher.Enabled)
        {
            _health.PendingBacklog = await _attendance.CountPendingAsync(ct);
            return;
        }

        var batch = await _attendance.GetPendingAsync(_options.BatchSize, ct);
        if (batch.Count == 0) return;

        var result = await _pusher.PushAsync(batch, ct);

        if (result.AcceptedKeys.Count > 0)
            await _attendance.MarkUploadedAsync(result.AcceptedKeys, ct);

        if (result.RejectedKeys.Count > 0)
        {
            // Prefer the destination's own per-row reason so last_upload_error is actionable.
            if (result.Errors is { Count: > 0 })
                await _attendance.MarkAttemptFailedAsync(result.Errors, _options.MaxAttempts, ct);
            else
                await _attendance.MarkAttemptFailedAsync(result.RejectedKeys, "rejected by central push", _options.MaxAttempts, ct);
        }

        _health.LastPushSuccessUtc = DateTime.UtcNow;
        _health.PendingBacklog = await _attendance.CountPendingAsync(ct);
        _logger.LogInformation("Push cycle: {Accepted} accepted, {Rejected} rejected, {Backlog} pending.",
            result.AcceptedKeys.Count, result.RejectedKeys.Count, _health.PendingBacklog);
    }
}
