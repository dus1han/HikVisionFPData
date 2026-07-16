using HikSync.Core.Models;

namespace HikSync.Data.Internal;

/// <summary>Explicit enum &lt;-&gt; DB text mapping (no reliance on Dapper enum-parse fragility).</summary>
internal static class DbMappings
{
    public static string RoleToDb(DeviceRole role) => role == DeviceRole.In ? "IN" : "OUT";

    public static DeviceRole RoleFromDb(string value) =>
        string.Equals(value, "IN", StringComparison.OrdinalIgnoreCase) ? DeviceRole.In : DeviceRole.Out;

    public static string StatusToDb(UploadStatus status) => status switch
    {
        UploadStatus.Pending => "pending",
        UploadStatus.Uploaded => "uploaded",
        UploadStatus.DeadLetter => "dead_letter",
        _ => "pending",
    };

    public static UploadStatus StatusFromDb(string value) => value switch
    {
        "uploaded" => UploadStatus.Uploaded,
        "dead_letter" => UploadStatus.DeadLetter,
        _ => UploadStatus.Pending,
    };
}
