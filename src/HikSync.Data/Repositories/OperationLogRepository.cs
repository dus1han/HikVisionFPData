using Dapper;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;
using HikSync.Data.Internal;

namespace HikSync.Data.Repositories;

public sealed class OperationLogRepository : IOperationLogRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public OperationLogRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task WriteAsync(OperationLog entry, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO operation_log (logged_at, device_ip, role, pair_id, operation, status, message, duration_ms)
            VALUES (@LoggedAt, @DeviceIp, @Role, @PairId, @Operation, @Status, @Message, @DurationMs);
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            LoggedAt = DateTime.SpecifyKind(entry.LoggedAtUtc, DateTimeKind.Utc),
            entry.DeviceIp,
            Role = entry.Role is null ? null : DbMappings.RoleToDb(entry.Role.Value),
            entry.PairId,
            entry.Operation,
            entry.Status,
            entry.Message,
            entry.DurationMs,
        }, cancellationToken: ct));
    }

    public async Task<int> DeleteBeforeAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        const string sql = "DELETE FROM operation_log WHERE logged_at < @cutoff;";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            sql, new { cutoff = DateTime.SpecifyKind(cutoffUtc, DateTimeKind.Utc) }, cancellationToken: ct));
    }
}
