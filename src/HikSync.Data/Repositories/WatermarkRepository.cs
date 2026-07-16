using Dapper;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;

namespace HikSync.Data.Repositories;

public sealed class WatermarkRepository : IWatermarkRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public WatermarkRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<FetchWatermark?> GetAsync(string deviceIp, CancellationToken ct)
    {
        const string sql = """
            SELECT device_ip, last_event_time, last_serial_no, last_run_at, last_status, last_error
            FROM fetch_watermark WHERE device_ip = @deviceIp;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(sql, new { deviceIp }, cancellationToken: ct));

        return row is null ? null : new FetchWatermark
        {
            DeviceIp = row.DeviceIp,
            LastEventTimeUtc = row.LastEventTime,
            LastSerialNo = row.LastSerialNo,
            LastRunAtUtc = row.LastRunAt,
            LastStatus = row.LastStatus,
            LastError = row.LastError,
        };
    }

    public async Task UpsertAsync(FetchWatermark w, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO fetch_watermark (device_ip, last_event_time, last_serial_no, last_run_at, last_status, last_error)
            VALUES (@DeviceIp, @LastEventTime, @LastSerialNo, @LastRunAt, @LastStatus, @LastError)
            ON CONFLICT (device_ip) DO UPDATE SET
                last_event_time = EXCLUDED.last_event_time,
                last_serial_no  = EXCLUDED.last_serial_no,
                last_run_at     = EXCLUDED.last_run_at,
                last_status     = EXCLUDED.last_status,
                last_error      = EXCLUDED.last_error;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            w.DeviceIp,
            LastEventTime = Utc(w.LastEventTimeUtc),
            w.LastSerialNo,
            LastRunAt = Utc(w.LastRunAtUtc),
            w.LastStatus,
            w.LastError,
        }, cancellationToken: ct));
    }

    private static DateTime? Utc(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private sealed class Row
    {
        public string DeviceIp { get; set; } = string.Empty;
        public DateTime? LastEventTime { get; set; }
        public long? LastSerialNo { get; set; }
        public DateTime? LastRunAt { get; set; }
        public string? LastStatus { get; set; }
        public string? LastError { get; set; }
    }
}
