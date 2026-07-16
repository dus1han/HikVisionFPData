using HikSync.Core.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HikSync.Data;

/// <summary>Creates connections to the LOCAL edge Postgres.</summary>
public sealed class NpgsqlConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(IOptions<LocalDatabaseOptions> options)
    {
        _connectionString = options.Value.ConnectionString;
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }
}
