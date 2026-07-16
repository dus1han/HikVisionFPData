using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HikSync.Push;

public static class PushServiceCollectionExtensions
{
    public static IServiceCollection AddHikSyncPush(this IServiceCollection services)
    {
        services.AddSingleton<StubAttendancePusher>();
        services.AddHttpClient();

        // Real HTTP pusher when Push is enabled + an endpoint is set; else the no-op stub.
        services.AddSingleton<IAttendancePusher>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PushOptions>>().Value;
            return options.Enabled && !string.IsNullOrWhiteSpace(options.Endpoint)
                ? ActivatorUtilities.CreateInstance<HttpAttendancePusher>(sp)
                : sp.GetRequiredService<StubAttendancePusher>();
        });

        return services;
    }
}
