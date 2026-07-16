using System.Net;
using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Device.Isapi;

/// <summary>Creates ISAPI (HTTP) device connections with per-device digest credentials.</summary>
public sealed class IsapiAccessDeviceFactory : IAccessDeviceFactory
{
    private readonly SdkOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public IsapiAccessDeviceFactory(IOptions<SdkOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public Task<IAccessDevice> ConnectAsync(DeviceEndpoint endpoint, CancellationToken ct)
    {
        string scheme = _options.IsapiHttps ? "https" : "http";
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(endpoint.Username, endpoint.Password),
            PreAuthenticate = false,
        };
        if (_options.IsapiHttps)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{scheme}://{endpoint.Ip}:{_options.IsapiPort}/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.LoginTimeoutSeconds)),
        };

        IAccessDevice device = new IsapiAccessDevice(endpoint, http, _loggerFactory.CreateLogger<IsapiAccessDevice>());
        return Task.FromResult(device);
    }
}
