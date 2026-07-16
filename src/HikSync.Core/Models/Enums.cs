namespace HikSync.Core.Models;

/// <summary>Which terminal of a pair an event/device belongs to. Determines clock-in vs clock-out.</summary>
public enum DeviceRole
{
    In,
    Out,
}

/// <summary>Normalized verification mode of an access event.</summary>
public enum VerifyMode
{
    Unknown = 0,
    Fingerprint,
    Card,
    Face,
    Pin,
    Combination,
}

/// <summary>Local push state of a captured attendance row.</summary>
public enum UploadStatus
{
    Pending = 0,
    Uploaded,
    DeadLetter,
}
