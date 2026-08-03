using Dapper;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;
using HikSync.Data.Internal;

namespace HikSync.Data.Repositories;

public sealed class SyncFailureRepository : ISyncFailureRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public SyncFailureRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(IReadOnlyCollection<SyncFailure> failures, CancellationToken ct)
    {
        if (failures.Count == 0) return;

        // Upsert on the natural key: a failure seen again bumps attempts and last_seen_at and refreshes
        // the error, so the table stays one row per outstanding problem rather than growing every cycle.
        const string sql = """
            INSERT INTO sync_failure
                (pair_id, source_ip, target_ip, employee_no, finger_index, operation, error, first_seen_at, last_seen_at, attempts)
            VALUES
                (@PairId, @SourceIp, @TargetIp, @EmployeeNo, @FingerIndex, @Operation, @Error, now(), now(), 1)
            ON CONFLICT (pair_id, target_ip, employee_no, finger_index, operation)
            DO UPDATE SET last_seen_at = now(),
                          attempts     = sync_failure.attempts + 1,
                          error        = EXCLUDED.error,
                          source_ip    = EXCLUDED.source_ip;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        foreach (var f in failures)
            await conn.ExecuteAsync(new CommandDefinition(sql, f, cancellationToken: ct));
    }
}
