using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Logging;

namespace HikSync.Data;

/// <summary>Applies embedded SQL migrations to the local DB via DbUp on startup.</summary>
public static class DatabaseMigrator
{
    public static void Migrate(string connectionString, ILogger logger)
    {
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        UpgradeEngine upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(DatabaseMigrator).Assembly,
                name => name.Contains(".Migrations.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build();

        if (!upgrader.IsUpgradeRequired())
        {
            logger.LogInformation("Local DB schema up to date.");
            return;
        }

        DatabaseUpgradeResult result = upgrader.PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException("Local DB migration failed.", result.Error);

        logger.LogInformation("Local DB migrated: {Count} script(s) applied.", result.Scripts.Count());
    }
}
