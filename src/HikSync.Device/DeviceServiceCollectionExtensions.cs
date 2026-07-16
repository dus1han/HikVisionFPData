using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Device.Fake;
using HikSync.Device.Hikvision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HikSync.Device;

public static class DeviceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the access-device factory. Selects the HCNetSDK-backed factory or the in-memory
    /// fake based on <see cref="SdkOptions.UseFakeDevice"/>.
    /// </summary>
    public static IServiceCollection AddHikSyncDevices(this IServiceCollection services)
    {
        services.AddSingleton<FakeAccessDeviceFactory>();
        services.AddSingleton<HcNetSdkManager>();

        services.AddSingleton<IAccessDeviceFactory>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SdkOptions>>().Value;
            return options.UseFakeDevice
                ? sp.GetRequiredService<FakeAccessDeviceFactory>()
                : ActivatorUtilities.CreateInstance<HikvisionDeviceFactory>(sp);
        });

        return services;
    }
}
