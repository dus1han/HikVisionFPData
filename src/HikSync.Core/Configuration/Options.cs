using System.ComponentModel.DataAnnotations;

namespace HikSync.Core.Configuration;

/// <summary>Connection to the LOCAL edge Postgres (capture + offline buffer).</summary>
public sealed class LocalDatabaseOptions
{
    public const string Section = "LocalDatabase";

    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Run DbUp migrations against the local DB on startup.</summary>
    public bool MigrateOnStartup { get; set; } = true;
}

/// <summary>HCNetSDK / device-connection options.</summary>
public sealed class SdkOptions
{
    public const string Section = "Sdk";

    /// <summary>Folder (relative to the exe) holding the HCNetSDK x64 DLL set.</summary>
    public string NativeLibraryPath { get; set; } = "native";

    /// <summary>When true, use the in-memory fake device instead of HCNetSDK (dev/test without hardware).</summary>
    public bool UseFakeDevice { get; set; } = false;

    public int LoginTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Login protocol: 0 = Private (port 8000), 1 = ISAPI, 2 = Adaptive (tries both, like iVMS-4200).
    /// Default Adaptive — newer terminals reject the Private login and require ISAPI.
    /// </summary>
    public byte LoginMode { get; set; } = 2;

    /// <summary>For ISAPI login: 0 = HTTP, 1 = HTTPS, 2 = adaptive.</summary>
    public byte Https { get; set; } = 0;

    /// <summary>How to talk to the device: "sdk" (HCNetSDK) or "isapi" (HTTP/REST). Newer terminals often need "isapi".</summary>
    public string Transport { get; set; } = "sdk";

    /// <summary>ISAPI HTTP port (device web port, usually 80 — NOT the SDK port 8000).</summary>
    public int IsapiPort { get; set; } = 80;

    /// <summary>Use HTTPS for ISAPI.</summary>
    public bool IsapiHttps { get; set; } = false;
}

/// <summary>Attendance-collection job options.</summary>
public sealed class AttendanceOptions
{
    public const string Section = "Attendance";

    public int IntervalSeconds { get; set; } = 60;

    /// <summary>First-run window start per device. Null = start from service start time (no history import).</summary>
    public DateTime? BackfillStartUtc { get; set; }

    /// <summary>
    /// Which verification modes count as a punch. Empty = any successful verification
    /// (fingerprint/card/face/pin). Values match <see cref="Models.VerifyMode"/> names.
    /// </summary>
    public string[] CountedVerifyModes { get; set; } = Array.Empty<string>();

    /// <summary>Device local-time offset from UTC, in minutes. Null = treat device time as UTC.</summary>
    public int? DeviceUtcOffsetMinutes { get; set; }

    public int MaxConcurrentDevices { get; set; } = 4;
}

/// <summary>User + fingerprint sync (IN -> OUT) job options.</summary>
public sealed class SyncOptions
{
    public const string Section = "Sync";

    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Delete OUT users that no longer exist on IN. Default off (never auto-delete).</summary>
    public bool DeleteRemovedUsers { get; set; } = false;

    /// <summary>Only sync users that have at least one fingerprint (skip users without biometrics).</summary>
    public bool OnlyUsersWithFingerprints { get; set; } = true;
}

/// <summary>Operation-log retention. A nightly job deletes rows older than <see cref="RetentionDays"/>.</summary>
public sealed class LogOptions
{
    public const string Section = "Log";

    public bool RetentionEnabled { get; set; } = true;

    /// <summary>Delete operation_log rows older than this many days. Default 30.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Local hour (0-23) at/after which the once-per-day cleanup runs. Default 02:00.</summary>
    public int CleanupHourLocal { get; set; } = 2;
}

/// <summary>Push-to-remote-API options. The service POSTs not-yet-uploaded attendance rows to this API.</summary>
public sealed class PushOptions
{
    public const string Section = "Push";

    public bool Enabled { get; set; } = false;
    public int IntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 200;
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Full remote API URL to POST attendance batches to, e.g. http://10.0.0.50:8080/api/attendanceraw/insertB</summary>
    public string? Endpoint { get; set; }

    /// <summary>Fixed companyId sent with every record (not present in device data).</summary>
    public int CompanyId { get; set; } = 0;

    /// <summary>Minutes to add to the stored UTC event time for `checkTime`. 0 = UTC; set to your
    /// device's UTC offset (e.g. 240 for UTC+4) to send local time. Format is yyyy-MM-dd HH:mm:ss.</summary>
    public int TimeOffsetMinutes { get; set; } = 0;

    /// <summary>None | Bearer | ApiKey | Basic.</summary>
    public string AuthType { get; set; } = "None";

    /// <summary>Token/key/base64 credential, per AuthType.</summary>
    public string? AuthValue { get; set; }

    /// <summary>Header name used when AuthType = ApiKey.</summary>
    public string ApiKeyHeader { get; set; } = "X-API-Key";

    /// <summary>Per-request timeout.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
