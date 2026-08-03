using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Device.Fake;
using HikSync.Device.Hikvision;
using HikSync.Device.Isapi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HikSync.Device;

public static class DeviceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the access-device factory: fake, ISAPI (HTTP), or HCNetSDK — chosen by
    /// <see cref="SdkOptions.UseFakeDevice"/> and <see cref="SdkOptions.Transport"/>.
    /// </summary>
    public static IServiceCollection AddHikSyncDevices(this IServiceCollection services)
    {
        services.AddSingleton<FakeAccessDeviceFactory>();
        services.AddSingleton<HcNetSdkManager>();

        services.AddSingleton<IAccessDeviceFactory>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SdkOptions>>().Value;
            if (options.UseFakeDevice)
                return sp.GetRequiredService<FakeAccessDeviceFactory>();
            return string.Equals(options.Transport, "isapi", StringComparison.OrdinalIgnoreCase)
                ? ActivatorUtilities.CreateInstance<IsapiAccessDeviceFactory>(sp)
                : ActivatorUtilities.CreateInstance<HikvisionDeviceFactory>(sp);
        });

        return services;
    }
}
