using HikSync.Core.Abstractions;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace HikSync.Application;

/// <summary>
/// Writes device/service transactions to the operation_log table. Best-effort: a logging failure
/// is itself logged (to Serilog) but never propagates into the work flow.
/// </summary>
public sealed class OperationLogger
{
    private readonly IOperationLogRepository _repo;
    private readonly ILogger<OperationLogger> _logger;

    public OperationLogger(IOperationLogRepository repo, ILogger<OperationLogger> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task LogAsync(
        string? deviceIp, DeviceRole? role, string operation, string status, string message,
        CancellationToken ct, long? pairId = null, int? durationMs = null)
    {
        try
        {
            await _repo.WriteAsync(new OperationLog
            {
                LoggedAtUtc = DateTime.UtcNow,
                DeviceIp = deviceIp,
                Role = role,
                PairId = pairId,
                Operation = operation,
                Status = status,
                Message = message,
                DurationMs = durationMs,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write operation_log entry ({Operation} {DeviceIp}).", operation, deviceIp);
        }
    }
}
