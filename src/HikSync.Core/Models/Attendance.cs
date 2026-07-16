namespace HikSync.Core.Models;

/// <summary>A raw access event as read from a device (device-native fields).</summary>
public sealed record AccessEvent
{
    /// <summary>Per-device monotonic serial. Ordering cursor only — NOT the identity (resets on device wipe).</summary>
    public long SerialNo { get; init; }

    /// <summary>Event time already normalized to UTC by the device layer.</summary>
    public DateTime EventTimeUtc { get; init; }

    public string EmployeeNo { get; init; } = string.Empty;
    public string? CardNo { get; init; }
    public int Major { get; init; }
    public int Minor { get; init; }
    public VerifyMode VerifyMode { get; init; } = VerifyMode.Unknown;

    /// <summary>Free-form debug/audit payload of the original event.</summary>
    public string Raw { get; init; } = string.Empty;
}

/// <summary>Query window for reading access events from a device.</summary>
public sealed record AcsEventQuery
{
    public DateTime StartUtc { get; init; }
    public DateTime EndUtc { get; init; }

    /// <summary>Device local-time offset from UTC (device reports local time).</summary>
    public TimeSpan DeviceUtcOffset { get; init; } = TimeSpan.Zero;

    /// <summary>ACS event major type. 5 = event (attendance verifications). 0 is invalid on newer firmware.</summary>
    public uint Major { get; init; } = 5;

    /// <summary>ACS event minor type. 0 = all minors under the major.</summary>
    public uint Minor { get; init; } = 0;
}

/// <summary>An attendance row as stored in the LOCAL edge database.</summary>
public sealed class AttendanceRecord
{
    public long Id { get; set; }
    public long PairId { get; set; }
    public string DeviceIp { get; set; } = string.Empty;
    public DeviceRole Role { get; set; }
    public string Location { get; set; } = string.Empty;
    public string EmployeeNo { get; set; } = string.Empty;
    public string? CardNo { get; set; }
    public DateTime EventTimeUtc { get; set; }
    public long DeviceSerialNo { get; set; }
    public int Major { get; set; }
    public int Minor { get; set; }
    public string VerifyMode { get; set; } = string.Empty;
    public string RawJson { get; set; } = string.Empty;
    public DateTime FetchedAtUtc { get; set; }

    /// <summary>Reset-immune identity: {ip}:{employee}:{unix(eventTime)}:{major}:{minor}.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public UploadStatus UploadStatus { get; set; } = UploadStatus.Pending;
    public int UploadAttempts { get; set; }
    public string? LastUploadError { get; set; }
    public DateTime? UploadedAt { get; set; }
}

/// <summary>Per-device delta cursor for attendance capture.</summary>
public sealed class FetchWatermark
{
    public string DeviceIp { get; set; } = string.Empty;
    public DateTime? LastEventTimeUtc { get; set; }
    public long? LastSerialNo { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
}
