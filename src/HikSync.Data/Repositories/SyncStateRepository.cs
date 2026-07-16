using Dapper;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;

namespace HikSync.Data.Repositories;

public sealed class SyncStateRepository : ISyncStateRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public SyncStateRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<SyncState?> GetAsync(long pairId, CancellationToken ct)
    {
        const string sql = """
            SELECT pair_id, last_sync_at, in_user_count, out_user_count, last_status, last_error
            FROM sync_state WHERE pair_id = @pairId;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(sql, new { pairId }, cancellationToken: ct));

        return row is null ? null : new SyncState
        {
            PairId = row.PairId,
            LastSyncAtUtc = row.LastSyncAt,
            InUserCount = row.InUserCount,
            OutUserCount = row.OutUserCount,
            LastStatus = row.LastStatus,
            LastError = row.LastError,
        };
    }

    public async Task UpsertAsync(SyncState s, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO sync_state (pair_id, last_sync_at, in_user_count, out_user_count, last_status, last_error)
            VALUES (@PairId, @LastSyncAt, @InUserCount, @OutUserCount, @LastStatus, @LastError)
            ON CONFLICT (pair_id) DO UPDATE SET
                last_sync_at   = EXCLUDED.last_sync_at,
                in_user_count  = EXCLUDED.in_user_count,
                out_user_count = EXCLUDED.out_user_count,
                last_status    = EXCLUDED.last_status,
                last_error     = EXCLUDED.last_error;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            s.PairId,
            LastSyncAt = s.LastSyncAtUtc is null ? (DateTime?)null : DateTime.SpecifyKind(s.LastSyncAtUtc.Value, DateTimeKind.Utc),
            s.InUserCount,
            s.OutUserCount,
            s.LastStatus,
            s.LastError,
        }, cancellationToken: ct));
    }

    private sealed class Row
    {
        public long PairId { get; set; }
        public DateTime? LastSyncAt { get; set; }
        public int InUserCount { get; set; }
        public int OutUserCount { get; set; }
        public string? LastStatus { get; set; }
        public string? LastError { get; set; }
    }
}
