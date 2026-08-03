using Dapper;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;
using HikSync.Data.Internal;

namespace HikSync.Data.Repositories;

public sealed class DeviceEnrollmentRepository : IDeviceEnrollmentRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public DeviceEnrollmentRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task ReplaceForDeviceAsync(string deviceIp, IReadOnlyCollection<DeviceEnrollment> rows, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Delete-then-insert in one transaction so a reader never sees a half-refreshed roster, and a
        // user removed from the device disappears here too.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM device_enrollment WHERE device_ip = @deviceIp;",
            new { deviceIp }, tx, cancellationToken: ct));

        if (rows.Count > 0)
        {
            const string sql = """
                INSERT INTO device_enrollment
                    (device_ip, employee_no, pair_id, location, role, name, enabled, fingerprint_count, finger_ids, last_synced_at)
                VALUES
                    (@DeviceIp, @EmployeeNo, @PairId, @Location, @Role, @Name, @Enabled, @FingerprintCount, @FingerIds, now());
                """;

            foreach (var r in rows)
                await conn.ExecuteAsync(new CommandDefinition(sql, new
                {
                    r.DeviceIp,
                    r.EmployeeNo,
                    r.PairId,
                    r.Location,
                    Role = DbMappings.RoleToDb(r.Role),
                    r.Name,
                    r.Enabled,
                    r.FingerprintCount,
                    r.FingerIds,
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }
}
