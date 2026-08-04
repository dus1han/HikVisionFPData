using System.Text.Json;

namespace HikSync.Device.Isapi;

/// <summary>
/// The device's verdict on one fingerprint apply, read from
/// <c>FingerPrintStatus.StatusList[].cardReaderRecvStatus</c>.
///
/// A 200 response does NOT mean the template was stored — DS-K1A8503MF-B answers 200 and reports
/// the real outcome here, so callers must check <see cref="Accepted"/>.
/// </summary>
public sealed record IsapiFingerprintStatus(int RecvStatus, string ErrorMessage)
{
    /// <summary>1 = the device stored the template. Everything else is a rejection.</summary>
    public bool Accepted => RecvStatus == 1;

    /// <summary>
    /// Reads the first StatusList entry. Returns null when the response carries no verdict yet
    /// (the apply is asynchronous — poll /AccessControl/FingerPrintProgress).
    /// </summary>
    public static IsapiFingerprintStatus? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("FingerPrintStatus", out var status)) return null;
        if (!status.TryGetProperty("StatusList", out var list) || list.ValueKind != JsonValueKind.Array) return null;

        foreach (var entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("cardReaderRecvStatus", out var code) || !code.TryGetInt32(out int recv)) continue;
            string msg = entry.TryGetProperty("errorMsg", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            return new IsapiFingerprintStatus(recv, msg);
        }
        return null;
    }

    /// <summary>
    /// Status meanings confirmed against DS-K1A8503MF-B V1.4.1. For a duplicate, the device puts the
    /// employee number that already owns the template in <c>errorMsg</c>.
    /// </summary>
    public string Describe() => RecvStatus switch
    {
        0 => "the device returned no verdict for the apply",
        1 => "stored",
        5 => string.IsNullOrEmpty(ErrorMessage)
            ? "the fingerprint module refused the template (it is already enrolled on this device)"
            : $"this finger is already enrolled on this device under employee '{ErrorMessage}'",
        _ => $"the card reader refused the template (cardReaderRecvStatus={RecvStatus})",
    };

    public override string ToString() =>
        $"{Describe()} (cardReaderRecvStatus={RecvStatus}" +
        (string.IsNullOrEmpty(ErrorMessage) ? ")" : $", errorMsg='{ErrorMessage}')");
}
