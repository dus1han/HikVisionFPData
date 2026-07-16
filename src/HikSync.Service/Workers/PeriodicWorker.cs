using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HikSync.Service.Workers;

/// <summary>Base for interval jobs: runs once immediately, then every <see cref="Interval"/>.</summary>
public abstract class PeriodicWorker : BackgroundService
{
    private readonly ILogger _logger;

    protected PeriodicWorker(ILogger logger) => _logger = logger;

    protected abstract string Name { get; }
    protected abstract TimeSpan Interval { get; }
    protected virtual bool IsEnabled => true;

    protected abstract Task RunOnceAsync(CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            _logger.LogInformation("{Name} is disabled by configuration.", Name);
            return;
        }

        _logger.LogInformation("{Name} started (interval {Interval}).", Name, Interval);
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Name} cycle failed.", Name);
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        _logger.LogInformation("{Name} stopping.", Name);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
