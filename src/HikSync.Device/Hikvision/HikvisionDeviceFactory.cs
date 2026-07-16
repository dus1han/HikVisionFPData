using System.Runtime.Versioning;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace HikSync.Device.Hikvision;

[SupportedOSPlatform("windows")]
public sealed class HikvisionDeviceFactory : IAccessDeviceFactory
{
    private readonly HcNetSdkManager _manager;
    private readonly ILoggerFactory _loggerFactory;

    public HikvisionDeviceFactory(HcNetSdkManager manager, ILoggerFactory loggerFactory)
    {
        _manager = manager;
        _loggerFactory = loggerFactory;
    }

    public async Task<IAccessDevice> ConnectAsync(DeviceEndpoint endpoint, CancellationToken ct)
    {
        // NET_DVR_Login_V40 is a blocking native call — keep it off the async caller's thread.
        var (userId, info) = await Task.Run(() => _manager.Login(endpoint), ct).ConfigureAwait(false);
        return new HikvisionAccessDevice(_manager, userId, endpoint, info, _loggerFactory.CreateLogger<HikvisionAccessDevice>());
    }
}
