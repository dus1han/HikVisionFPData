using Dapper;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;
using HikSync.Data.Internal;

namespace HikSync.Data.Repositories;

public sealed class AttendanceRepository : IAttendanceRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public AttendanceRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<int> InsertIgnoreAsync(IReadOnlyCollection<AttendanceRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return 0;

        const string sql = """
            INSERT INTO attendance_events
                (pair_id, device_ip, role, location, employee_no, card_no, event_time,
                 device_serial_no, major, minor, verify_mode, raw_json, fetched_at,
                 idempotency_key, upload_status, upload_attempts)
            VALUES
                (@PairId, @DeviceIp, @Role, @Location, @EmployeeNo, @CardNo, @EventTime,
                 @DeviceSerialNo, @Major, @Minor, @VerifyMode, CAST(@RawJson AS jsonb), @FetchedAt,
                 @IdempotencyKey, @UploadStatus, @UploadAttempts)
            ON CONFLICT (idempotency_key) DO NOTHING;
            """;

        var rows = records.Select(r => new
        {
            r.PairId,
            r.DeviceIp,
            Role = DbMappings.RoleToDb(r.Role),
            r.Location,
            r.EmployeeNo,
            r.CardNo,
            EventTime = DateTime.SpecifyKind(r.EventTimeUtc, DateTimeKind.Utc),
            r.DeviceSerialNo,
            r.Major,
            r.Minor,
            r.VerifyMode,
            RawJson = string.IsNullOrEmpty(r.RawJson) ? null : r.RawJson,
            FetchedAt = DateTime.SpecifyKind(r.FetchedAtUtc, DateTimeKind.Utc),
            r.IdempotencyKey,
            UploadStatus = DbMappings.StatusToDb(r.UploadStatus),
            r.UploadAttempts,
        });

        await using var conn = await _factory.OpenAsync(ct);
        // Dapper executes the command once per row and sums affected rows.
        // With ON CONFLICT DO NOTHING, that sum == the number of genuinely new rows.
        return await conn.ExecuteAsync(new CommandDefinition(sql, rows, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AttendanceRecord>> GetPendingAsync(int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT id, pair_id, device_ip, role, location, employee_no, card_no, event_time,
                   device_serial_no, major, minor, verify_mode, raw_json, fetched_at,
                   idempotency_key, upload_status, upload_attempts, last_upload_error, uploaded_at
            FROM attendance_events
            WHERE upload_status = 'pending'
            ORDER BY event_time
            LIMIT @limit;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, new { limit }, cancellationToken: ct));

        return rows.Select(r => new AttendanceRecord
        {
            Id = r.Id,
            PairId = r.PairId,
            DeviceIp = r.DeviceIp,
            Role = DbMappings.RoleFromDb(r.Role),
            Location = r.Location,
            EmployeeNo = r.EmployeeNo,
            CardNo = r.CardNo,
            EventTimeUtc = r.EventTime,
            DeviceSerialNo = r.DeviceSerialNo ?? 0,
            Major = r.Major,
            Minor = r.Minor,
            VerifyMode = r.VerifyMode ?? string.Empty,
            RawJson = r.RawJson ?? string.Empty,
            FetchedAtUtc = r.FetchedAt,
            IdempotencyKey = r.IdempotencyKey,
            UploadStatus = DbMappings.StatusFromDb(r.UploadStatus),
            UploadAttempts = r.UploadAttempts,
            LastUploadError = r.LastUploadError,
            UploadedAt = r.UploadedAt,
        }).ToList();
    }

    public async Task MarkUploadedAsync(IReadOnlyCollection<string> idempotencyKeys, CancellationToken ct)
    {
        if (idempotencyKeys.Count == 0) return;
        const string sql = """
            UPDATE attendance_events
            SET upload_status = 'uploaded', uploaded_at = now()
            WHERE idempotency_key = ANY(@keys);
            """;
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { keys = idempotencyKeys.ToArray() }, cancellationToken: ct));
    }

    public async Task MarkAttemptFailedAsync(
        IReadOnlyCollection<string> idempotencyKeys, string error, int maxAttempts, CancellationToken ct)
    {
        if (idempotencyKeys.Count == 0) return;
        const string sql = """
            UPDATE attendance_events
            SET upload_attempts = upload_attempts + 1,
                last_upload_error = @error,
                upload_status = CASE WHEN upload_attempts + 1 >= @maxAttempts THEN 'dead_letter' ELSE upload_status END
            WHERE idempotency_key = ANY(@keys);
            """;
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            sql, new { keys = idempotencyKeys.ToArray(), error, maxAttempts }, cancellationToken: ct));
    }

    public async Task<int> CountPendingAsync(CancellationToken ct)
    {
        const string sql = "SELECT count(*) FROM attendance_events WHERE upload_status = 'pending';";
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: ct));
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public long PairId { get; set; }
        public string DeviceIp { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string EmployeeNo { get; set; } = string.Empty;
        public string? CardNo { get; set; }
        public DateTime EventTime { get; set; }
        public long? DeviceSerialNo { get; set; }
        public int Major { get; set; }
        public int Minor { get; set; }
        public string? VerifyMode { get; set; }
        public string? RawJson { get; set; }
        public DateTime FetchedAt { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public string UploadStatus { get; set; } = "pending";
        public int UploadAttempts { get; set; }
        public string? LastUploadError { get; set; }
        public DateTime? UploadedAt { get; set; }
    }
}
