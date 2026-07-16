using Dapper;
using HikSync.Core.Abstractions;
using HikSync.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace HikSync.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddHikSyncData(this IServiceCollection services)
    {
        // Map snake_case columns to PascalCase members for the row DTOs.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.AddSingleton<NpgsqlConnectionFactory>();
        services.AddSingleton<IDevicePairRepository, DevicePairRepository>();
        services.AddSingleton<IWatermarkRepository, WatermarkRepository>();
        services.AddSingleton<IAttendanceRepository, AttendanceRepository>();
        services.AddSingleton<ISyncStateRepository, SyncStateRepository>();
        services.AddSingleton<IOperationLogRepository, OperationLogRepository>();

        return services;
    }
}
