using HikSync.Application;
using HikSync.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Service.Workers;

/// <summary>Checks hourly; the service runs the delete once per day after the configured hour.</summary>
public sealed class LogRetentionWorker : PeriodicWorker
{
    private readonly LogRetentionService _retention;
    private readonly LogOptions _options;

    public LogRetentionWorker(LogRetentionService retention, IOptions<LogOptions> options, ILogger<LogRetentionWorker> logger)
        : base(logger)
    {
        _retention = retention;
        _options = options.Value;
    }

    protected override string Name => "Log retention (nightly)";
    protected override bool IsEnabled => _options.RetentionEnabled;
    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override Task RunOnceAsync(CancellationToken ct) => _retention.CleanupIfDueAsync(ct);
}
