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
            VerifyMode = string.IsNullOrEmpty(vm) ? MapVerifyModeFromMinor(minor) : MapVerifyMode(vm),
            Raw = e.GetRawText(),
        };
    }

    // AcsEvent has no verify-mode field on this firmware; the minor code carries it.
    private static VerifyMode MapVerifyModeFromMinor(int minor) => minor switch
    {
        38 => VerifyMode.Fingerprint, // observed: fingerprint verification passed
        1 => VerifyMode.Card,
        _ => VerifyMode.Unknown,
    };

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

    // ---------------- Users (ISAPI /AccessControl/UserInfo) ----------------

    public async IAsyncEnumerable<DeviceUser> ReadUsersAsync([EnumeratorCancellation] CancellationToken ct)
    {
        int position = 0;
        const int pageSize = 30;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var body = new { UserInfoSearchCond = new { searchID = "hiksync", searchResultPosition = position, maxResults = pageSize } };
            using var doc = await SendJsonAsync(HttpMethod.Post, "/ISAPI/AccessControl/UserInfo/Search?format=json", body, ct);

            int returned = 0;
            string status = "OK";
            var users = new List<DeviceUser>();
            if (doc.RootElement.TryGetProperty("UserInfoSearch", out var s))
            {
                if (s.TryGetProperty("responseStatusStrg", out var rs)) status = rs.GetString() ?? "OK";
                if (s.TryGetProperty("numOfMatches", out var nm)) returned = nm.GetInt32();
                if (s.TryGetProperty("UserInfo", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var u in list.EnumerateArray())
                    {
                        string emp = Str(u, "employeeNo");
                        if (string.IsNullOrEmpty(emp)) continue;
                        bool enabled = !u.TryGetProperty("Valid", out var v) || !v.TryGetProperty("enable", out var en) || en.GetBoolean();
                        users.Add(new DeviceUser { EmployeeNo = emp, Name = Str(u, "name"), Enabled = enabled, UserType = Str(u, "userType") is { Length: > 0 } t ? t : "normal" });
                    }
            }
            foreach (var u in users) yield return u;
            if (returned == 0 || !string.Equals(status, "MORE", StringComparison.OrdinalIgnoreCase)) break;
            position += returned;
        }
    }

    public async Task UpsertUserAsync(DeviceUser user, CancellationToken ct)
    {
        object payload = new
        {
            UserInfo = new
            {
                employeeNo = user.EmployeeNo,
                name = string.IsNullOrEmpty(user.Name) ? user.EmployeeNo : user.Name,
                userType = "normal",
                Valid = new { enable = user.Enabled, beginTime = "2020-01-01T00:00:00", endTime = "2037-12-31T23:59:59", timeType = "local" },
                doorRight = "1",
                RightPlan = new[] { new { doorNo = 1, planTemplateNo = "1" } },
            },
        };
        // Add; if the user already exists, modify instead.
        try { (await SendJsonAsync(HttpMethod.Post, "/ISAPI/AccessControl/UserInfo/Record?format=json", payload, ct)).Dispose(); }
        catch { (await SendJsonAsync(HttpMethod.Put, "/ISAPI/AccessControl/UserInfo/Modify?format=json", payload, ct)).Dispose(); }
    }

    public async Task DeleteUserAsync(string employeeNo, CancellationToken ct)
    {
        var body = new { UserInfoDelCond = new { EmployeeNoList = new[] { new { employeeNo } } } };
        (await SendJsonAsync(HttpMethod.Put, "/ISAPI/AccessControl/UserInfo/Delete?format=json", body, ct)).Dispose();
    }

    // ---------------- Fingerprints (ISAPI /AccessControl/FingerPrint*) ----------------

    public async IAsyncEnumerable<FingerprintTemplate> ReadFingerprintsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var employees = new List<string>();
        await foreach (var u in ReadUsersAsync(ct)) employees.Add(u.EmployeeNo);

        foreach (var emp in employees)
        {
            List<FingerprintTemplate> prints;
            try { prints = await GetFingerprintsAsync(emp, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "fingerprint read failed for {Emp}", emp); continue; }
            foreach (var f in prints) yield return f;
        }
    }

    private async Task<List<FingerprintTemplate>> GetFingerprintsAsync(string employeeNo, CancellationToken ct)
    {
        var body = new { FingerPrintCond = new { searchID = "hiksync", cardReaderNo = 1, employeeNo } };
        using var doc = await SendJsonAsync(HttpMethod.Post, "/ISAPI/AccessControl/FingerPrintUpload?format=json", body, ct);
        var result = new List<FingerprintTemplate>();
        // Response shape: { "FingerPrintInfo": { "status": "OK", "FingerPrintList": [ { fingerPrintID, fingerData, ... } ] } }
        if (doc.RootElement.TryGetProperty("FingerPrintInfo", out var info) &&
            info.TryGetProperty("FingerPrintList", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var f in list.EnumerateArray())
            {
                string data = Str(f, "fingerData");
                if (string.IsNullOrEmpty(data)) continue;
                int id = f.TryGetProperty("fingerPrintID", out var fid) && fid.TryGetInt32(out var i) ? i : 1;
                result.Add(new FingerprintTemplate { EmployeeNo = employeeNo, FingerIndex = id, Template = SafeBase64(data) });
            }
        return result;
    }

    public async Task UpsertFingerprintAsync(FingerprintTemplate fingerprint, CancellationToken ct)
    {
        var body = new
        {
            FingerPrintDownload = new
            {
                employeeNo = fingerprint.EmployeeNo,
                enableCardReader = new[] { 1 },
                fingerPrintID = fingerprint.FingerIndex,
                fingerType = "normalFP",
                fingerData = Convert.ToBase64String(fingerprint.Template),
            },
        };
        (await SendJsonAsync(HttpMethod.Post, "/ISAPI/AccessControl/FingerPrintDownload?format=json", body, ct)).Dispose();
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ISAPI {method} {path} HTTP {(int)resp.StatusCode}: {Trim(json)}");
        return string.IsNullOrWhiteSpace(json) ? JsonDocument.Parse("{}") : JsonDocument.Parse(json);
    }

    private static byte[] SafeBase64(string s)
    {
        try { return Convert.FromBase64String(s); }
        catch { return Array.Empty<byte>(); }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
