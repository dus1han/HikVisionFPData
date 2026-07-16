using HikSync.Core.Configuration;
using HikSync.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Service.Workers;

public sealed class AttendanceWorker : PeriodicWorker
{
    private readonly AttendanceCollector _collector;
    private readonly AttendanceOptions _options;

    public AttendanceWorker(AttendanceCollector collector, IOptions<AttendanceOptions> options, ILogger<AttendanceWorker> logger)
        : base(logger)
    {
        _collector = collector;
        _options = options.Value;
    }

    protected override string Name => "Attendance collection";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(5, _options.IntervalSeconds));

    protected override Task RunOnceAsync(CancellationToken ct) => _collector.CollectAllAsync(ct);
}
