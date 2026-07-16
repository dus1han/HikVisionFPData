using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Application;

/// <summary>
/// Nightly retention: once per day (at/after the configured local hour) delete operation_log rows
/// older than the retention window — i.e. `DELETE FROM operation_log WHERE logged_at &lt; now() - N days`.
/// </summary>
public sealed class LogRetentionService
{
    private readonly IOperationLogRepository _repo;
    private readonly OperationLogger _log;
    private readonly LogOptions _options;
    private readonly ILogger<LogRetentionService> _logger;
    private DateOnly? _lastRunDate;

    public LogRetentionService(
        IOperationLogRepository repo,
        OperationLogger log,
        IOptions<LogOptions> options,
        ILogger<LogRetentionService> logger)
    {
        _repo = repo;
        _log = log;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs the cleanup at most once per calendar day, after the configured hour.</summary>
    public async Task CleanupIfDueAsync(CancellationToken ct)
    {
        var nowLocal = DateTime.Now;
        if (nowLocal.Hour < _options.CleanupHourLocal) return;

        var today = DateOnly.FromDateTime(nowLocal);
        if (_lastRunDate == today) return;

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays));
        int deleted = await _repo.DeleteBeforeAsync(cutoff, ct);
        _lastRunDate = today;

        _logger.LogInformation("Log retention: deleted {Count} operation_log row(s) older than {Days} day(s).",
            deleted, _options.RetentionDays);
        await _log.LogAsync(null, null, LogOperation.Cleanup, LogStatus.Ok,
            $"deleted {deleted} log rows older than {_options.RetentionDays} days", ct);
    }
}
