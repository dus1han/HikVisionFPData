using HikSync.Core.Configuration;
using HikSync.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Service.Workers;

public sealed class SyncWorker : PeriodicWorker
{
    private readonly DeviceSyncService _sync;
    private readonly SyncOptions _options;

    public SyncWorker(DeviceSyncService sync, IOptions<SyncOptions> options, ILogger<SyncWorker> logger)
        : base(logger)
    {
        _sync = sync;
        _options = options.Value;
    }

    protected override string Name => "Device sync (IN -> OUT)";
    protected override bool IsEnabled => _options.Enabled;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(10, _options.IntervalSeconds));

    protected override Task RunOnceAsync(CancellationToken ct) => _sync.SyncAllAsync(ct);
}
