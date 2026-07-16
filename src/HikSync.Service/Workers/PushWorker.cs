using HikSync.Core.Configuration;
using HikSync.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Service.Workers;

public sealed class PushWorker : PeriodicWorker
{
    private readonly PushService _push;
    private readonly PushOptions _options;

    public PushWorker(PushService push, IOptions<PushOptions> options, ILogger<PushWorker> logger)
        : base(logger)
    {
        _push = push;
        _options = options.Value;
    }

    protected override string Name => "Push to central Postgres";
    protected override bool IsEnabled => _options.Enabled;
    protected override TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(10, _options.IntervalSeconds));

    protected override Task RunOnceAsync(CancellationToken ct) => _push.PushPendingAsync(ct);
}
