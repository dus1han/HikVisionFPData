using FluentAssertions;
using HikSync.Application;
using HikSync.Core.Configuration;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HikSync.UnitTests;

public class LogRetentionTests
{
    [Fact]
    public async Task DeletesEntriesOlderThanRetentionWindow_KeepsRecent()
    {
        var repo = new InMemoryOperationLogRepository();
        repo.Entries.Add(new OperationLog { LoggedAtUtc = DateTime.UtcNow.AddDays(-40), Operation = "connect", Status = "ok" });
        repo.Entries.Add(new OperationLog { LoggedAtUtc = DateTime.UtcNow.AddDays(-10), Operation = "connect", Status = "ok" });

        var service = new LogRetentionService(
            repo,
            new OperationLogger(repo, NullLogger<OperationLogger>.Instance),
            Options.Create(new LogOptions { CleanupHourLocal = 0, RetentionDays = 30 }),
            NullLogger<LogRetentionService>.Instance);

        await service.CleanupIfDueAsync(CancellationToken.None);

        repo.Entries.Should().NotContain(e => e.LoggedAtUtc < DateTime.UtcNow.AddDays(-30));
        repo.Entries.Should().ContainSingle(e => e.Operation == "connect"); // the -10d one survives
        repo.Entries.Should().Contain(e => e.Operation == "cleanup");       // cleanup itself is logged
    }

    [Fact]
    public async Task RunsAtMostOncePerDay()
    {
        var repo = new InMemoryOperationLogRepository();
        var service = new LogRetentionService(
            repo,
            new OperationLogger(repo, NullLogger<OperationLogger>.Instance),
            Options.Create(new LogOptions { CleanupHourLocal = 0, RetentionDays = 30 }),
            NullLogger<LogRetentionService>.Instance);

        await service.CleanupIfDueAsync(CancellationToken.None);
        await service.CleanupIfDueAsync(CancellationToken.None); // same day: should be a no-op

        repo.Entries.Count(e => e.Operation == "cleanup").Should().Be(1);
    }
}
