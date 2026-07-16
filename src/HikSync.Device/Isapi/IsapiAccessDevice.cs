using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace HikSync.Device.Isapi;

/// <summary>
/// ISAPI (HTTP/REST) device connection — for terminals where the HCNetSDK private protocol is
/// rejected. Attendance events come from POST /ISAPI/AccessControl/AcsEvent (employee-keyed).
/// User/fingerprint sync over ISAPI is not implemented yet (throws).
/// </summary>
public sealed class IsapiAccessDevice : IAccessDevice
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public IsapiAccessDevice(DeviceEndpoint endpoint, HttpClient http, ILogger logger)
    {
        Endpoint = endpoint;
        _http = http;
        _logger = logger;
    }

    public DeviceEndpoint Endpoint { get; }

    public async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("/ISAPI/System/deviceInfo", ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ISAPI deviceInfo HTTP {(int)resp.StatusCode}: {Trim(body)}");

        try
        {
            var doc = XDocument.Parse(body);
            string Val(string local) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value ?? string.Empty;
            return new DeviceInfo { Model = Val("model"), SerialNumber = Val("serialNumber"), FirmwareVersion = Val("firmwareVersion") };
        }
        catch
        {
            return new DeviceInfo();
        }
    }

    public async IAsyncEnumerable<AccessEvent> ReadEventsAsync(AcsEventQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        string start = FormatIsapiTime(query.StartUtc, query.DeviceUtcOffset);
        string end = FormatIsapiTime(query.EndUtc, query.DeviceUtcOffset);
        int position = 0;
        const int pageSize = 30;
        long serialFallback = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var cond = new Dictionary<string, object>
            {
                ["searchID"] = "hiksync",
                ["searchResultPosition"] = position,
                ["maxResults"] = pageSize,
                ["major"] = (int)query.Major,
                ["minor"] = (int)query.Minor, // required by firmware; 0 = all minors
                ["startTime"] = start,
                ["endTime"] = end,
            };

            string reqJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["AcsEventCond"] = cond });
            using var content = new StringContent(reqJson, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("/ISAPI/AccessControl/AcsEvent?format=json", content, ct);
            string json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"ISAPI AcsEvent HTTP {(int)resp.StatusCode}: {Trim(json)}");

            int returned = 0;
            string status = "OK";
            List<AccessEvent> page = new();
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("AcsEvent", out var acs))
                {
                    if (acs.TryGetProperty("responseStatusStrg", out var rs)) status = rs.GetString() ?? "OK";
                    if (acs.TryGetProperty("numOfMatches", out var nm)) returned = nm.GetInt32();
                    if (acs.TryGetProperty("InfoList", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var e in list.EnumerateArray())
                        {
                            var ev = MapEvent(e, query.DeviceUtcOffset, ref serialFallback);
                            if (ev is not null) page.Add(ev);
                        }
                }
            }

            foreach (var ev in page) yield return ev;

            if (returned == 0 || !string.Equals(status, "MORE", StringComparison.OrdinalIgnoreCase)) break;
            position += returned;
        }
    }

    private static AccessEvent? MapEvent(JsonElement e, TimeSpan offset, ref long serialFallback)
    {
        string employee = Str(e, "employeeNoString");
        if (string.IsNullOrEmpty(employee)) employee = Str(e, "cardNo"); // fall back to card as identity
        if (string.IsNullOrEmpty(employee)) return null;                 // door/alarm event, no person

        string timeStr = Str(e, "time");
        DateTime eventUtc = DateTimeOffset.TryParse(timeStr, out var dto) ? dto.UtcDateTime : DateTime.UtcNow;

        long serial = e.TryGetProperty("serialNo", out var sn) && sn.TryGetInt64(out var s) ? s : ++serialFallback;
        int major = e.TryGetProperty("major", out var mj) && mj.TryGetInt32(out var m) ? m : 5;
        int minor = e.TryGetProperty("minor", out var mi) && mi.TryGetInt32(out var n) ? n : 0;
        string card = Str(e, "cardNo");
        string vm = Str(e, "currentVerifyMode");

        return new AccessEvent
        {
            SerialNo = serial,
            EventTimeUtc = eventUtc,
            EmployeeNo = employee,
            CardNo = string.IsNullOrEmpty(card) ? null : card,
            Major = major,
            Minor = minor,
            VerifyMode = MapVerifyMode(vm),
            Raw = $"verifyMode={vm};minor={minor}",
        };
    }

    private static VerifyMode MapVerifyMode(string vm) => vm.ToLowerInvariant() switch
    {
        var s when s.Contains("fp") || s.Contains("finger") => VerifyMode.Fingerprint,
        var s when s.Contains("face") => VerifyMode.Face,
        var s when s.Contains("card") => VerifyMode.Card,
        var s when s.Contains("pw") || s.Contains("pin") => VerifyMode.Pin,
        _ => VerifyMode.Unknown,
    };

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string FormatIsapiTime(DateTime utc, TimeSpan offset)
    {
        var local = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(offset);
        return local.ToString("yyyy-MM-ddTHH:mm:sszzz");
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;

    // --- Sync over ISAPI not implemented yet ---
    public IAsyncEnumerable<DeviceUser> ReadUsersAsync(CancellationToken ct) => throw Ni(nameof(ReadUsersAsync));
    public IAsyncEnumerable<FingerprintTemplate> ReadFingerprintsAsync(CancellationToken ct) => throw Ni(nameof(ReadFingerprintsAsync));
    public Task UpsertUserAsync(DeviceUser user, CancellationToken ct) => throw Ni(nameof(UpsertUserAsync));
    public Task UpsertFingerprintAsync(FingerprintTemplate fingerprint, CancellationToken ct) => throw Ni(nameof(UpsertFingerprintAsync));
    public Task DeleteUserAsync(string employeeNo, CancellationToken ct) => throw Ni(nameof(DeleteUserAsync));

    private static NotSupportedException Ni(string m) =>
        new($"IsapiAccessDevice.{m} is not implemented yet (ISAPI UserInfo/FingerPrint endpoints).");

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
