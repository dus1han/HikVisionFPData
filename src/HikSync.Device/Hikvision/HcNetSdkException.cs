namespace HikSync.Device.Hikvision;

/// <summary>An HCNetSDK call failed; carries the SDK error code from NET_DVR_GetLastError.</summary>
public sealed class HcNetSdkException : Exception
{
    public uint ErrorCode { get; }

    public HcNetSdkException(string operation, uint errorCode)
        : base($"HCNetSDK {operation} failed (error {errorCode}).")
    {
        ErrorCode = errorCode;
    }
}
