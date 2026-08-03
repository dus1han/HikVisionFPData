using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Logic;
using HikSync.Core.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using HikSync.Device.Fake;
using HikSync.Device.Hikvision;
using HikSync.Device.Isapi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ---- args ----
var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) continue;
    var key = args[i][2..];
    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) opts[key] = args[++i];
    else flags.Add(key);
}

if (flags.Contains("help") || (args.Length == 0))
{
    Console.WriteLine("""
        HikSync.DeviceCheck — verify a terminal against the service's structure & functions.

          --ip <addr>        device IP (required unless --fake)
          --port <n>         SDK port (default 8000)
          --user <s>         username (default admin)
          --pass <s>         password (default empty)
          --minutes <n>      attendance window to read (default 120)
          --offset <min>     device local-time offset from UTC in minutes (default 0)
          --max <n>          max rows to print per section (default 10)
          --major <n>        ACS event major type (default 5 = event/attendance)
          --minor <n>        ACS event minor type (default 0 = all)
          --login-mode <n>   0=Private 1=ISAPI 2=Adaptive (default 2, like iVMS-4200)
          --https <n>        ISAPI login: 0=HTTP 1=HTTPS 2=adaptive (default 0)
          --transport <t>    sdk (HCNetSDK) or isapi (HTTP/REST). Try isapi if SDK gives errors.
          --isapi-port <n>   ISAPI HTTP port (default 80, NOT the SDK port 8000)
          --isapi-https      use HTTPS for ISAPI
          --sdk-path <dir>   HCNetSDK native folder (default native)
          --write-test       upsert a test user and read it back (WRITES to the device)
          --test-emp <no>    employee/card no for --write-test (default 999001)
          --fake             use the in-memory fake device (self-test, no hardware)
          --probe <emp>      dump raw ISAPI responses (capabilities + fingerprint) for an employee
          --fp-selftest <emp>  find the FingerPrintDownload payload this firmware accepts (writes the
                               employee's own fingerprint back unchanged — safe in production)
          --delete <emp[,emp]>    DELETE the given user(s); use "all" to delete everyone
          --delete-others <emp>   DELETE every user on the device except <emp>
          --sync-to <ip>     copy users + fingerprints from --ip to this target device
          --to-user <u>      target device username for --sync-to (default: same as --user)
          --to-pass <p>      target device password for --sync-to (default: same as --pass)

        Example:
          HikSync.DeviceCheck --ip 192.168.1.10 --user admin --pass secret --minutes 240
        """);
    return 0;
}

string Get(string k, string d) => opts.TryGetValue(k, out var v) ? v : d;
int GetInt(string k, int d) => opts.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d;

bool fake = flags.Contains("fake");
int port = GetInt("port", 8000);
int minutes = GetInt("minutes", 120);
int offsetMin = GetInt("offset", 0);
int max = GetInt("max", 10);
string ip = Get("ip", fake ? "10.0.0.1" : "");

if (!fake && string.IsNullOrWhiteSpace(ip))
{
    Console.Error.WriteLine("--ip is required (or use --fake). Run with --help.");
    return 2;
}

// Diagnostic: --probe <employeeNo> dumps raw ISAPI responses (capabilities + biometric endpoints).
if (opts.TryGetValue("probe", out var probeEmp))
{
    await RunProbe(ip, GetInt("isapi-port", 80), Get("user", "admin"), Get("pass", ""), probeEmp);
    return 0;
}

// Diagnostic: --fp-selftest <employeeNo> reads that employee's fingerprint and writes it BACK to the
// SAME employee (no data change) trying several FingerPrintDownload payload shapes, to discover which
// one this firmware accepts. Safe to run in production — it re-writes a person's own template.
if (opts.TryGetValue("fp-selftest", out var fpEmp))
{
    return await RunFpSelfTest(ip, GetInt("isapi-port", 80), Get("user", "admin"), Get("pass", ""), fpEmp);
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));

string transport = Get("transport", "isapi"); // default ISAPI; use --transport sdk for HCNetSDK
var sdkOptions = Options.Create(new SdkOptions
{
    NativeLibraryPath = Get("sdk-path", "native"),
    UseFakeDevice = fake,
    LoginMode = (byte)GetInt("login-mode", 2),
    Https = (byte)GetInt("https", 0),
    Transport = transport,
    IsapiPort = GetInt("isapi-port", 80),
    IsapiHttps = flags.Contains("isapi-https"),
});
bool isapi = transport.Equals("isapi", StringComparison.OrdinalIgnoreCase);
IAccessDeviceFactory factory =
    fake ? new FakeAccessDeviceFactory()
    : isapi ? new IsapiAccessDeviceFactory(sdkOptions, loggerFactory)
    : new HikvisionDeviceFactory(new HcNetSdkManager(sdkOptions, loggerFactory.CreateLogger<HcNetSdkManager>()), loggerFactory);

var endpoint = new DeviceEndpoint { Ip = ip, Port = port, Username = Get("user", "admin"), Password = Get("pass", "") };
var ct = CancellationToken.None;

int failures = 0;
void Head(string t) => Console.WriteLine($"\n=== {t} ===");
void Ok(string m) => Console.WriteLine($"  [ OK ] {m}");
void Fail(string m) { Console.WriteLine($"  [FAIL] {m}"); failures++; }
void Info(string m) => Console.WriteLine($"         {m}");

string[] modeNames = { "Private", "ISAPI", "Adaptive" };
string modeName = sdkOptions.Value.LoginMode < 3 ? modeNames[sdkOptions.Value.LoginMode] : sdkOptions.Value.LoginMode.ToString();
string target = fake ? "FAKE device"
    : isapi ? $"{(sdkOptions.Value.IsapiHttps ? "https" : "http")}://{ip}:{sdkOptions.Value.IsapiPort} (ISAPI)"
    : $"{endpoint} (SDK, loginMode={modeName})";
Console.WriteLine($"HikSync.DeviceCheck -> {target}  (user={endpoint.Username})");

// Maintenance modes (run instead of the standard checks).
if (opts.TryGetValue("delete", out var delSpec))
{
    await DeleteUsers(factory, endpoint, delSpec, ct);
    return 0;
}
if (opts.TryGetValue("delete-others", out var keepEmp))
{
    await DeleteOthers(factory, endpoint, keepEmp, ct);
    return 0;
}
if (opts.TryGetValue("sync-to", out var syncTargetIp))
{
    var targetEp = new DeviceEndpoint { Ip = syncTargetIp, Port = port, Username = Get("to-user", endpoint.Username), Password = Get("to-pass", endpoint.Password) };
    await SyncTo(factory, endpoint, targetEp, ct);
    return 0;
}

IAccessDevice? device = null;
try
{
    // 1. Login + device info
    Head("Connect / device info  (-> DeviceInfo, used for model-mismatch check in sync)");
    try
    {
        device = await factory.ConnectAsync(endpoint, ct);
        var info = await device.GetDeviceInfoAsync(ct);
        Ok($"login succeeded");
        Info($"Model='{info.Model}'  Serial='{info.SerialNumber}'  Firmware='{info.FirmwareVersion}'");
    }
    catch (Exception ex)
    {
        Fail($"login failed: {Describe(ex)}");
        Console.WriteLine($"\nRESULT: FAILED ({failures} check(s) failed).");
        return 1;
    }

    // 2. Attendance events
    Head($"Attendance events, last {minutes} min  (-> attendance_events columns)");
    try
    {
        var query = new AcsEventQuery
        {
            StartUtc = DateTime.UtcNow.AddMinutes(-minutes),
            EndUtc = DateTime.UtcNow,
            DeviceUtcOffset = TimeSpan.FromMinutes(offsetMin),
            Major = (uint)GetInt("major", 5),
            Minor = (uint)GetInt("minor", 0),
        };
        int n = 0;
        await foreach (var e in device.ReadEventsAsync(query, ct))
        {
            if (n < max)
            {
                string key = AttendanceIdentity.ComputeKey(endpoint.Ip, e.EmployeeNo, e.EventTimeUtc, e.Major, e.Minor);
                Info($"employee_no={e.EmployeeNo,-10} event_time={e.EventTimeUtc:yyyy-MM-dd HH:mm:ss}Z " +
                     $"verify_mode={e.VerifyMode,-11} card_no={e.CardNo ?? "-",-12} serial={e.SerialNo} major/minor={e.Major}/{e.Minor}");
                Info($"    idempotency_key={key}");
                if (n == 0 && !string.IsNullOrEmpty(e.Raw))
                    Info($"    raw: {(e.Raw.Length > 500 ? e.Raw[..500] + "…" : e.Raw)}");
            }
            n++;
        }
        Ok($"read {n} event(s)" + (n > max ? $" (showing first {max})" : ""));
        if (n == 0) Info("(no events in the window — try a larger --minutes, or punch a card/finger on the device)");
    }
    catch (Exception ex) { Fail($"event read failed: {Describe(ex)}"); }

    // 3. Users / cards
    Head("User (card) records  (-> DeviceUser; cardNo = employeeNo)");
    List<DeviceUser> users = new();
    try
    {
        await foreach (var u in device.ReadUsersAsync(ct)) users.Add(u);
        Ok($"read {users.Count} user(s)" + (users.Count > max ? $" (showing first {max})" : ""));
        foreach (var u in users.Take(max))
            Info($"employee_no={u.EmployeeNo,-10} name='{u.Name}'  enabled={u.Enabled}  userType={u.UserType}");
    }
    catch (Exception ex) { Fail($"user read failed: {Describe(ex)}"); }

    // 4. Fingerprints
    Head("Fingerprint templates  (-> FingerprintTemplate: employeeNo, fingerIndex, bytes)");
    try
    {
        var prints = new List<FingerprintTemplate>();
        await foreach (var f in device.ReadFingerprintsAsync(ct)) prints.Add(f);
        Ok($"read {prints.Count} fingerprint(s) across {prints.Select(p => p.EmployeeNo).Distinct().Count()} user(s)");
        foreach (var f in prints.Take(max))
            Info($"employee_no={f.EmployeeNo,-10} finger#={f.FingerIndex,-2} templateBytes={f.Template.Length}");
    }
    catch (Exception ex) { Fail($"fingerprint read failed: {Describe(ex)}"); }

    // 5. Optional write test
    if (flags.Contains("write-test"))
    {
        string testEmp = Get("test-emp", "999001");
        Head($"Write test — upsert user '{testEmp}' then read back  (WRITES to device)");
        try
        {
            await device.UpsertUserAsync(new DeviceUser { EmployeeNo = testEmp, Name = "HIKSYNC_TEST", Enabled = true }, ct);
            bool found = false;
            await foreach (var u in device.ReadUsersAsync(ct))
                if (u.EmployeeNo == testEmp) { found = true; break; }
            if (found) Ok($"user '{testEmp}' written and read back (remove it via the device UI when done)");
            else Fail($"user '{testEmp}' not found after upsert");
        }
        catch (Exception ex) { Fail($"write test failed: {Describe(ex)}"); }
    }
    else
    {
        Head("Write test  (skipped — pass --write-test to exercise SET_CARD)");
    }
}
finally
{
    if (device is not null) await device.DisposeAsync();
}

Console.WriteLine($"\nRESULT: {(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED")}.");
return failures == 0 ? 0 : 1;

static string Describe(Exception ex) =>
    ex is HcNetSdkException sdk ? $"{sdk.Message} (SDK error {sdk.ErrorCode})" : ex.Message;

static async Task DeleteUsers(IAccessDeviceFactory factory, DeviceEndpoint ep, string spec, CancellationToken ct)
{
    await using var dev = await factory.ConnectAsync(ep, ct);
    List<string> emps;
    if (spec.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        emps = new List<string>();
        await foreach (var u in dev.ReadUsersAsync(ct)) emps.Add(u.EmployeeNo);
        Console.WriteLine($"Deleting ALL {emps.Count} user(s)...");
    }
    else
    {
        emps = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Console.WriteLine($"Deleting {emps.Count} user(s): {string.Join(", ", emps)}");
    }

    int ok = 0;
    foreach (var e in emps)
    {
        try { await dev.DeleteUserAsync(e, ct); ok++; Console.WriteLine($"  deleted {e}"); }
        catch (Exception ex) { Console.WriteLine($"  FAILED delete {e}: {ex.Message}"); }
    }
    Console.WriteLine($"\nDeleted {ok}/{emps.Count}. Users now on device:");
    await foreach (var u in dev.ReadUsersAsync(ct)) Console.WriteLine($"  employee_no={u.EmployeeNo}  name='{u.Name}'");
}

static async Task DeleteOthers(IAccessDeviceFactory factory, DeviceEndpoint ep, string keep, CancellationToken ct)
{
    await using var dev = await factory.ConnectAsync(ep, ct);
    var users = new List<DeviceUser>();
    await foreach (var u in dev.ReadUsersAsync(ct)) users.Add(u);
    var toDelete = users.Where(u => u.EmployeeNo != keep).ToList();
    Console.WriteLine($"Found {users.Count} user(s). Keeping '{keep}', deleting {toDelete.Count}...");
    int ok = 0;
    foreach (var u in toDelete)
    {
        try { await dev.DeleteUserAsync(u.EmployeeNo, ct); ok++; Console.WriteLine($"  deleted {u.EmployeeNo}"); }
        catch (Exception ex) { Console.WriteLine($"  FAILED delete {u.EmployeeNo}: {ex.Message}"); }
    }
    Console.WriteLine($"\nDeleted {ok}/{toDelete.Count}. Users now on device:");
    await foreach (var u in dev.ReadUsersAsync(ct)) Console.WriteLine($"  employee_no={u.EmployeeNo}  name='{u.Name}'");
}

static async Task SyncTo(IAccessDeviceFactory factory, DeviceEndpoint src, DeviceEndpoint dst, CancellationToken ct)
{
    Console.WriteLine($"Sync {src.Ip} -> {dst.Ip} (user={dst.Username})\n");
    await using var s = await factory.ConnectAsync(src, ct);
    await using var d = await factory.ConnectAsync(dst, ct);

    // Read both sides.
    var srcUsersAll = new List<DeviceUser>();
    await foreach (var u in s.ReadUsersAsync(ct)) srcUsersAll.Add(u);
    var srcFps = new List<FingerprintTemplate>();
    await foreach (var f in s.ReadFingerprintsAsync(ct)) srcFps.Add(f);

    var dstUsersAll = new List<DeviceUser>();
    await foreach (var u in d.ReadUsersAsync(ct)) dstUsersAll.Add(u);
    var dstFps = new List<FingerprintTemplate>();
    await foreach (var f in d.ReadFingerprintsAsync(ct)) dstFps.Add(f);

    // Only users that have a fingerprint.
    var srcWithFp = new HashSet<string>(srcFps.Select(f => f.EmployeeNo), StringComparer.Ordinal);
    var dstWithFp = new HashSet<string>(dstFps.Select(f => f.EmployeeNo), StringComparer.Ordinal);
    var srcUsers = srcUsersAll.Where(u => srcWithFp.Contains(u.EmployeeNo)).ToList();
    var dstUsers = dstUsersAll.Where(u => dstWithFp.Contains(u.EmployeeNo)).ToList();

    Console.WriteLine($"{src.Ip}: {srcUsers.Count} user(s) w/ fp, {srcFps.Count} fingerprint(s)");
    Console.WriteLine($"{dst.Ip}: {dstUsers.Count} user(s) w/ fp, {dstFps.Count} fingerprint(s)");
    Console.WriteLine("Union sync — each device gets what it's missing.\n");

    var toDst = SyncPlanner.BuildMissingOnly(srcUsers, srcFps, dstUsers, dstFps);
    var toSrc = SyncPlanner.BuildMissingOnly(dstUsers, dstFps, srcUsers, srcFps);

    int ok = 0, err = 0;
    async Task Apply(IAccessDevice target, string label, SyncPlan plan)
    {
        if (plan.IsEmpty) { Console.WriteLine($"  -> {label}: nothing missing"); return; }
        foreach (var u in plan.UsersToUpsert)
        {
            try { await target.UpsertUserAsync(u, ct); ok++; Console.WriteLine($"  -> {label}: user {u.EmployeeNo} OK"); }
            catch (Exception ex) { err++; Console.WriteLine($"  -> {label}: user {u.EmployeeNo} FAIL: {ex.Message}"); }
        }
        foreach (var f in plan.FingerprintsToUpsert)
        {
            try { await target.UpsertFingerprintAsync(f, ct); ok++; Console.WriteLine($"  -> {label}: fingerprint {f.EmployeeNo}#{f.FingerIndex} ({f.Template.Length}b) OK"); }
            catch (Exception ex) { err++; Console.WriteLine($"  -> {label}: fingerprint {f.EmployeeNo}#{f.FingerIndex} FAIL: {ex.Message}"); }
        }
    }

    await Apply(d, dst.Ip, toDst);
    await Apply(s, src.Ip, toSrc);

    Console.WriteLine($"\nUnion sync done. {ok} written, {err} failed. Both devices should now hold the same set.");
}

static async Task RunProbe(string ip, int port, string user, string pass, string emp)
{
    using var handler = new HttpClientHandler { Credentials = new NetworkCredential(user, pass) };
    using var http = new HttpClient(handler) { BaseAddress = new Uri($"http://{ip}:{port}/"), Timeout = TimeSpan.FromSeconds(15) };
    Console.WriteLine($"PROBE {ip}:{port}  employee={emp}\n");

    async Task Hit(string label, HttpMethod method, string path, string? body)
    {
        Console.WriteLine($"===== {label} =====");
        Console.WriteLine($"{method} {path}");
        try
        {
            using var req = new HttpRequestMessage(method, path);
            if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            string txt = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"HTTP {(int)resp.StatusCode}");
            Console.WriteLine(txt.Length > 1800 ? txt[..1800] + "…(truncated)" : txt);
        }
        catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); }
        Console.WriteLine();
    }

    await Hit("AccessControl capabilities", HttpMethod.Get, "/ISAPI/AccessControl/capabilities?format=json", null);
    await Hit("Fingerprint get (FingerPrintUpload)", HttpMethod.Post, "/ISAPI/AccessControl/FingerPrintUpload?format=json",
        $"{{\"FingerPrintCond\":{{\"searchID\":\"1\",\"employeeNo\":\"{emp}\",\"cardReaderNo\":1}}}}");
    await Hit("UserInfo capabilities", HttpMethod.Get, "/ISAPI/AccessControl/UserInfo/capabilities?format=json", null);
    await Hit("FingerPrint write capabilities", HttpMethod.Get, "/ISAPI/AccessControl/FingerPrintDownload/capabilities?format=json", null);
    await Hit("Face lib capabilities", HttpMethod.Get, "/ISAPI/Intelligent/FDLib/capabilities?format=json", null);
}

// Reads <emp>'s own fingerprint and writes it straight back under several payload shapes, to find the
// one this firmware accepts. Non-destructive: the data written is the employee's existing template.
static async Task<int> RunFpSelfTest(string ip, int port, string user, string pass, string emp)
{
    using var handler = new HttpClientHandler { Credentials = new NetworkCredential(user, pass) };
    using var http = new HttpClient(handler) { BaseAddress = new Uri($"http://{ip}:{port}/"), Timeout = TimeSpan.FromSeconds(15) };
    Console.WriteLine($"FP SELF-TEST {ip}:{port}  employee={emp}");
    Console.WriteLine("Reads this employee's own fingerprint and writes it back unchanged, trying each payload shape.\n");

    // 1. Read the employee's current fingerprint.
    string readBody = $"{{\"FingerPrintCond\":{{\"searchID\":\"1\",\"employeeNo\":\"{emp}\",\"cardReaderNo\":1}}}}";
    using var readReq = new HttpRequestMessage(HttpMethod.Post, "/ISAPI/AccessControl/FingerPrintUpload?format=json")
    { Content = new StringContent(readBody, Encoding.UTF8, "application/json") };
    using var readResp = await http.SendAsync(readReq);
    string readTxt = await readResp.Content.ReadAsStringAsync();
    if (!readResp.IsSuccessStatusCode) { Console.WriteLine($"read failed HTTP {(int)readResp.StatusCode}: {readTxt}"); return 1; }

    JsonElement fp;
    try
    {
        using var doc = JsonDocument.Parse(readTxt);
        var list = doc.RootElement.GetProperty("FingerPrintInfo").GetProperty("FingerPrintList");
        if (list.GetArrayLength() == 0) { Console.WriteLine($"employee {emp} has no fingerprint to test with."); return 1; }
        fp = list[0].Clone();
    }
    catch (Exception ex) { Console.WriteLine("could not parse fingerprint: " + ex.Message); return 1; }

    int fingerId = fp.TryGetProperty("fingerPrintID", out var fid) && fid.TryGetInt32(out var i) ? i : 1;
    string fingerType = fp.TryGetProperty("fingerType", out var ft) ? ft.GetString() ?? "normalFP" : "normalFP";
    string data = fp.TryGetProperty("fingerData", out var fd) ? fd.GetString() ?? "" : "";
    Console.WriteLine($"read OK: fingerPrintID={fingerId}, fingerType={fingerType}, fingerData={data.Length} chars\n");

    // Round 1 proved the structure is FingerPrintCfg + enableCardReader + fingerPrintID + fingerType +
    // fingerData (the others lacked a required field). What remains is one bad VALUE — most likely
    // enableCardReader. These keep that structure and vary a single value, writing the employee's own
    // template back to their own slot (non-destructive).
    int empNum = int.TryParse(emp, out var en) ? en : 0;
    var candidates = new (string Label, object Body)[]
    {
        ("A1 enableCardReader [1] (baseline)",
            new { FingerPrintCfg = new { employeeNo = emp, enableCardReader = new[] { 1 }, fingerPrintID = fingerId, fingerType, fingerData = data } }),
        ("A2 enableCardReader [0]",
            new { FingerPrintCfg = new { employeeNo = emp, enableCardReader = new[] { 0 }, fingerPrintID = fingerId, fingerType, fingerData = data } }),
        ("A3 enableCardReader [1,1]",
            new { FingerPrintCfg = new { employeeNo = emp, enableCardReader = new[] { 1, 1 }, fingerPrintID = fingerId, fingerType, fingerData = data } }),
        ("A4 enableCardReader [] (empty)",
            new { FingerPrintCfg = new { employeeNo = emp, enableCardReader = Array.Empty<int>(), fingerPrintID = fingerId, fingerType, fingerData = data } }),
        ("A5 employeeNo as number",
            new { FingerPrintCfg = new { employeeNo = empNum, enableCardReader = new[] { 1 }, fingerPrintID = fingerId, fingerType, fingerData = data } }),
        ("A6 fingerPrintID as string",
            new { FingerPrintCfg = new { employeeNo = emp, enableCardReader = new[] { 1 }, fingerPrintID = fingerId.ToString(), fingerType, fingerData = data } }),
        ("A7 add cardReaderNo:1 alongside",
            new { FingerPrintCfg = new { employeeNo = emp, enableCardReader = new[] { 1 }, cardReaderNo = 1, fingerPrintID = fingerId, fingerType, fingerData = data } }),
    };

    // Report the device's actual card readers — enableCardReader is expected to match them.
    try
    {
        using var capResp = await http.GetAsync("/ISAPI/AccessControl/CardReaderCfg/capabilities?format=json");
        string capTxt = await capResp.Content.ReadAsStringAsync();
        Console.WriteLine($"CardReaderCfg capabilities (HTTP {(int)capResp.StatusCode}): {(capTxt.Length > 400 ? capTxt[..400] + "…" : capTxt.Replace("\n", " ").Replace("\t", ""))}\n");
    }
    catch { /* informational only */ }

    string? winner = null;
    foreach (var (label, body) in candidates)
    {
        string json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ISAPI/AccessControl/FingerPrintDownload?format=json")
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        try
        {
            using var resp = await http.SendAsync(req);
            string txt = await resp.Content.ReadAsStringAsync();
            bool ok = resp.IsSuccessStatusCode && !txt.Contains("badParameters") && !txt.Contains("\"statusCode\":\t6");
            Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {label}");
            Console.WriteLine($"        HTTP {(int)resp.StatusCode}: {(txt.Length > 300 ? txt[..300] + "…" : txt.Replace("\n", " ").Replace("\t", ""))}");
            if (ok && winner is null) winner = label;
        }
        catch (Exception ex) { Console.WriteLine($"[ERR ] {label}: {ex.Message}"); }
        Console.WriteLine();
    }

    Console.WriteLine(winner is null
        ? "No payload shape was accepted. Send this whole output back for the next step."
        : $"WINNER: {winner}\nTell the maintainer this label so the sync payload can be locked to it.");
    return winner is null ? 1 : 0;
}
