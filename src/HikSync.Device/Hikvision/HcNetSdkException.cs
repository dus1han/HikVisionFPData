namespace HikSync.Device.Hikvision;

/// <summary>An HCNetSDK call failed; carries the SDK error code from NET_DVR_GetLastError.</summary>
public sealed class HcNetSdkException : Exception
{
    public uint ErrorCode { get; }

    public HcNetSdkException(string operation, uint errorCode)
        : base($"HCNetSDK {operation} failed (error {errorCode}: {Describe(errorCode)}).")
    {
        ErrorCode = errorCode;
    }

    /// <summary>Human-readable text for common NET_DVR_GetLastError codes.</summary>
    public static string Describe(uint code) => code switch
    {
        0 => "no error",
        1 => "username or password error",
        2 => "no permission for this operation",
        3 => "SDK not initialized",
        4 => "channel number error",
        5 => "max connections reached on device",
        6 => "SDK/device version mismatch",
        7 => "failed to connect — device offline or unreachable",
        8 => "network send failed",
        9 => "network receive failed",
        10 => "network receive timeout",
        12 => "operation not supported by device",
        17 => "parameter error",
        18 => "no more resources on device",
        29 => "device operation failed",
        41 => "resource allocation error",
        43 => "insufficient resources / buffer",
        72 => "user not online / login required",
        73 => "user already logged in on another session",
        84 => "account locked (too many failed logins) — wait or unlock via the device",
        96 => "OSD/parameter out of range",
        _ => "see HCNetSDK ErrorCode reference",
    };
}
