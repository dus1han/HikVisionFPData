using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HikSync.Push;

/// <summary>
/// Pushes not-yet-uploaded attendance rows to the remote API (HTTP POST). Each record carries its
/// device IP, IN/OUT direction and a stable idempotency key so the API can dedup.
///
/// Failure handling (REQUIREMENTS §7.3):
///  - 2xx                         -> whole batch accepted (marked uploaded locally);
///  - 4xx (bad request)           -> batch rejected (attempts++ -> dead_letter after MaxAttempts);
///  - 5xx / timeout / network err -> throws -> nothing marked, whole batch retried next cycle
///    (the local edge DB buffers meanwhile).
/// </summary>
public sealed class HttpAttendancePusher : IAttendancePusher
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly PushOptions _options;
    private readonly ILogger<HttpAttendancePusher> _logger;
    private readonly TimeZoneInfo? _timeZone;

    public HttpAttendancePusher(IHttpClientFactory httpFactory, IOptions<PushOptions> options, ILogger<HttpAttendancePusher> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.TimeZone))
        {
            try { _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZone); }
            catch (Exception ex) { _logger.LogWarning(ex, "Push:TimeZone '{Tz}' not found; checkTime will be UTC.", _options.TimeZone); }
        }
    }

    public bool Enabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.Endpoint);

    public async Task<PushResult> PushAsync(IReadOnlyList<AttendanceRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return PushResult.None;

        var keys = batch.Select(r => r.IdempotencyKey).ToList();
        var payload = batch.Select(ToDto).ToList(); // bare JSON array (API accepts a batch)

        var client = _httpFactory.CreateClient(nameof(HttpAttendancePusher));
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(payload),
        };
        ApplyAuth(request);

        HttpResponseMessage response = await client.SendAsync(request, ct); // network/timeout -> throws -> retry

        if (response.IsSuccessStatusCode)
            return await InterpretAcceptedAsync(response, keys);

        int code = (int)response.StatusCode;
        if (response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            // Client error: the API rejected the batch. Count it against the rows so they eventually dead-letter.
            string body = await SafeReadBodyAsync(response);
            _logger.LogError("Remote API rejected batch ({Code}): {Body}", code, body);
            return new PushResult(Array.Empty<string>(), keys);
        }

        // 5xx / other -> transient: throw so the batch stays pending and is retried next cycle.
        // Log the body first — without it a 500 gives no clue what the remote API actually choked on.
        _logger.LogError("Remote API push failed ({Code}): {Body}", code, await SafeReadBodyAsync(response));
        throw new HttpRequestException($"Remote API push failed with status {code}.");
    }

    /// <summary>
    /// A 2xx does not mean every row was stored. The API inserts what it can and reports the rest —
    /// so treating the whole batch as accepted marks skipped rows 'uploaded' locally and loses them
    /// silently. Honour the per-record result when the response carries one, and fall back to
    /// whole-batch acceptance only for an endpoint that says nothing (the older contract).
    /// </summary>
    private async Task<PushResult> InterpretAcceptedAsync(HttpResponseMessage response, List<string> keys)
    {
        string body = await SafeReadBodyAsync(response);
        if (string.IsNullOrWhiteSpace(body)) return new PushResult(keys, Array.Empty<string>());

        BulkInsertResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<BulkInsertResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Push response was not JSON; treating the batch as accepted. Body: {Body}", Trim(body));
            return new PushResult(keys, Array.Empty<string>());
        }

        // No skip list at all -> an endpoint on the old contract. Keep the previous behaviour.
        if (parsed?.SkippedRecords is null) return new PushResult(keys, Array.Empty<string>());

        var skipped = parsed.SkippedRecords;
        if (skipped.Count == 0) return new PushResult(keys, Array.Empty<string>());

        // Duplicates are benign: the row is already stored centrally, so it is genuinely done. Only a
        // real failure (unknown employee, invalid, ambiguous) should count against the row.
        var lost = skipped
            .Where(s => !string.Equals(s.Reason, "Duplicate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var errorsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in lost)
        {
            if (string.IsNullOrWhiteSpace(s.Key)) continue;
            errorsByKey[s.Key] = string.IsNullOrWhiteSpace(s.Detail) ? s.Reason : $"{s.Reason}: {s.Detail}";
        }

        var acceptedKeys = keys.Where(k => !errorsByKey.ContainsKey(k)).ToList();

        foreach (var s in lost.Take(5))
            _logger.LogWarning("Central API did not store {EmployeeNo} @ {CheckTime}: {Reason} — {Detail}",
                s.EmployeeNo, s.CheckTime, s.Reason, s.Detail);

        if (lost.Count > 5)
            _logger.LogWarning("...and {More} more record(s) not stored.", lost.Count - 5);

        return new PushResult(acceptedKeys, errorsByKey.Keys.ToList()) { Errors = errorsByKey };
    }

    private sealed class BulkInsertResponse
    {
        public List<SkippedRecordDto>? SkippedRecords { get; set; }
    }

    private sealed class SkippedRecordDto
    {
        public string Key { get; set; } = string.Empty;
        public string EmployeeNo { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public DateTimeOffset CheckTime { get; set; }
    }

    private static string Trim(string s) => s.Length <= 500 ? s : s[..500] + "…";

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (string.IsNullOrEmpty(_options.AuthValue)) return;
        switch (_options.AuthType.ToLowerInvariant())
        {
            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AuthValue);
                break;
            case "basic":
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _options.AuthValue);
                break;
            case "apikey":
                request.Headers.TryAddWithoutValidation(_options.ApiKeyHeader, _options.AuthValue);
                break;
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
    {
        try { return await response.Content.ReadAsStringAsync(); }
        catch { return "<unreadable>"; }
    }

    // Matches POST /api/attendanceraw/insertB — a bare array of these records.
    private object ToDto(AttendanceRecord r) => new
    {
        companyId = _options.CompanyId,
        // The device number as a string, so a leading zero ("05") survives — parsing it to an int
        // destroys that and can resolve to the wrong employee. employeeId stays for older endpoints.
        employeeNo = r.EmployeeNo,
        employeeId = int.TryParse(r.EmployeeNo, out var id) ? id : 0,
        // Echoed back in the skip list, which is how per-row failures are attributed.
        idempotencyKey = r.IdempotencyKey,
        checkTime = LocalTime(r.EventTimeUtc),
        checkType = r.Role == DeviceRole.In ? "IN" : "OUT",
        source = $"{r.Location}({r.DeviceIp})",
    };

    /// <summary>
    /// Event time in <see cref="PushOptions.TimeZone"/>. Returned as a DateTimeOffset so it serialises
    /// to ISO-8601 with the offset ("2026-07-17T14:53:13+05:30"): the API binds checkTime to a
    /// DateTimeOffset, and System.Text.Json rejects any other shape — which fails the whole batch.
    /// </summary>
    private DateTimeOffset LocalTime(DateTime utc)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (_timeZone is null) return new DateTimeOffset(u);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeFromUtc(u, _timeZone), _timeZone.GetUtcOffset(u));
    }
}
