using HikSync.Core.Abstractions;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace HikSync.Push;

/// <summary>
/// No-op pusher used when push is disabled (no <c>Push:Endpoint</c> configured). It accepts nothing,
/// so captured rows remain 'pending' in the local edge DB until <see cref="HttpAttendancePusher"/>
/// is active (Push:Enabled + Push:Endpoint set).
/// </summary>
public sealed class StubAttendancePusher : IAttendancePusher
{
    private readonly ILogger<StubAttendancePusher> _logger;
    private bool _warned;

    public StubAttendancePusher(ILogger<StubAttendancePusher> logger) => _logger = logger;

    public bool Enabled => false;

    public Task<PushResult> PushAsync(IReadOnlyList<AttendanceRecord> batch, CancellationToken ct)
    {
        if (!_warned)
        {
            _logger.LogInformation(
                "Central push is disabled: {Count} row(s) buffered locally as 'pending'. " +
                "Enable it once the central Postgres contract is finalized.", batch.Count);
            _warned = true;
        }
        return Task.FromResult(PushResult.None);
    }
}
