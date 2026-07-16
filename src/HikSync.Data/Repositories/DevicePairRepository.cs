using Dapper;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;

namespace HikSync.Data.Repositories;

public sealed class DevicePairRepository : IDevicePairRepository
{
    private readonly NpgsqlConnectionFactory _factory;

    public DevicePairRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<DevicePair>> GetEnabledPairsAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT id, location, in_ip, in_port, in_username, in_password,
                   out_ip, out_port, out_username, out_password, enabled
            FROM device_pairs
            WHERE enabled = true
            ORDER BY id;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PairRow>(new CommandDefinition(sql, cancellationToken: ct));

        return rows.Select(r => new DevicePair
        {
            Id = r.Id,
            Location = r.Location,
            Enabled = r.Enabled,
            In = new DeviceEndpoint
            {
                Ip = r.InIp,
                Port = r.InPort,
                Username = r.InUsername,
                Password = r.InPassword,
            },
            Out = new DeviceEndpoint
            {
                Ip = r.OutIp,
                Port = r.OutPort,
                Username = r.OutUsername,
                Password = r.OutPassword,
            },
        }).ToList();
    }

    private sealed class PairRow
    {
        public long Id { get; set; }
        public string Location { get; set; } = string.Empty;
        public string InIp { get; set; } = string.Empty;
        public int InPort { get; set; }
        public string InUsername { get; set; } = string.Empty;
        public string InPassword { get; set; } = string.Empty;
        public string OutIp { get; set; } = string.Empty;
        public int OutPort { get; set; }
        public string OutUsername { get; set; } = string.Empty;
        public string OutPassword { get; set; } = string.Empty;
        public bool Enabled { get; set; }
    }
}
