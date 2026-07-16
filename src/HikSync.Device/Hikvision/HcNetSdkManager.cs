using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HikSync.Core.Configuration;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static HikSync.Device.Hikvision.HikvisionInterop;

namespace HikSync.Device.Hikvision;

/// <summary>Owns the process-wide HCNetSDK lifecycle (init/cleanup) and performs logins.</summary>
[SupportedOSPlatform("windows")]
public sealed class HcNetSdkManager : IDisposable
{
    private readonly SdkOptions _options;
    private readonly ILogger<HcNetSdkManager> _logger;
    private readonly object _gate = new();
    private bool _initialized;

    public HcNetSdkManager(IOptions<SdkOptions> options, ILogger<HcNetSdkManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    public void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_gate)
        {
            if (_initialized) return;

            // The native DLLs live in a subfolder, but P/Invoke's default search does NOT look there.
            // Add it to the DLL search path BEFORE the first HCNetSDK call so HCNetSDK.dll and its
            // sibling dependencies (HCCore.dll, OpenSSL, etc.) load.
            string nativeFull = Path.GetFullPath(_options.NativeLibraryPath, AppContext.BaseDirectory);
            if (Directory.Exists(nativeFull))
            {
                if (!SetDllDirectory(nativeFull))
                    _logger.LogWarning("SetDllDirectory({Path}) failed (Win32 {Error}).", nativeFull, Marshal.GetLastWin32Error());
            }
            else
            {
                _logger.LogWarning("HCNetSDK native folder not found: {Path}", nativeFull);
            }

            SetSdkPath(_options.NativeLibraryPath);

            if (!NET_DVR_Init())
                throw new HcNetSdkException("NET_DVR_Init", NET_DVR_GetLastError());

            _logger.LogInformation("HCNetSDK initialized (native path: {Path}).", nativeFull);
            _initialized = true;
        }
    }

    /// <summary>Log in to a terminal. Returns the login handle and basic device identity.</summary>
    public (int UserId, DeviceInfo Info) Login(DeviceEndpoint endpoint)
    {
        EnsureInitialized();

        var loginInfo = new NET_DVR_USER_LOGIN_INFO
        {
            sDeviceAddress = endpoint.Ip,
            wPort = (ushort)endpoint.Port,
            sUserName = endpoint.Username,
            sPassword = endpoint.Password,
            byUseTransport = 0,
            bUseAsynLogin = 0,
            byLoginMode = _options.LoginMode, // 0=Private 1=ISAPI 2=Adaptive (default)
            byHttps = _options.Https,
            byRes = new byte[119],
        };
        var deviceInfo = new NET_DVR_DEVICEINFO_V40
        {
            struDeviceV30 = new NET_DVR_DEVICEINFO_V30 { sSerialNumber = new byte[48] },
            byRes2 = new byte[253],
        };

        int userId = NET_DVR_Login_V40(ref loginInfo, ref deviceInfo);
        if (userId < 0)
            throw new HcNetSdkException($"NET_DVR_Login_V40({endpoint})", NET_DVR_GetLastError());

        string serial = Encoding.ASCII.GetString(deviceInfo.struDeviceV30.sSerialNumber).TrimEnd('\0', ' ');
        var info = new DeviceInfo
        {
            Model = string.Empty, // read via NET_DVR_GetDVRConfig when needed (TODO in device impl)
            SerialNumber = serial,
            FirmwareVersion = string.Empty,
        };
        return (userId, info);
    }

    public void Logout(int userId)
    {
        if (!NET_DVR_Logout(userId))
            _logger.LogWarning("NET_DVR_Logout({UserId}) failed (error {Error}).", userId, NET_DVR_GetLastError());
    }

    private void SetSdkPath(string relativePath)
    {
        string full = Path.GetFullPath(relativePath, AppContext.BaseDirectory);
        if (!Directory.Exists(full))
        {
            _logger.LogWarning("HCNetSDK native path {Path} not found; relying on the default DLL search path.", full);
            return;
        }

        var cfg = new NET_DVR_LOCAL_SDK_PATH { sPath = full, byRes = new byte[128] };
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(cfg));
        try
        {
            Marshal.StructureToPtr(cfg, ptr, false);
            if (!NET_DVR_SetSDKInitCfg(NET_SDK_INIT_CFG_SDK_PATH, ptr))
                _logger.LogWarning("NET_DVR_SetSDKInitCfg(path) failed (error {Error}).", NET_DVR_GetLastError());
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            NET_DVR_Cleanup();
            _initialized = false;
        }
    }
}
