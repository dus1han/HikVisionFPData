using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Logic;
using HikSync.Core.Models;
using System.Net;
using System.Text;
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

    var users = new List<DeviceUser>();
    await foreach (var u in s.ReadUsersAsync(ct)) users.Add(u);
    var fps = new List<FingerprintTemplate>();
    await foreach (var f in s.ReadFingerprintsAsync(ct)) fps.Add(f);
    Console.WriteLine($"Source: {users.Count} user(s), {fps.Count} fingerprint(s).\n");

    int uOk = 0, uErr = 0, fOk = 0, fErr = 0;
    foreach (var u in users)
    {
        try { await d.UpsertUserAsync(u, ct); uOk++; Console.WriteLine($"  user {u.EmployeeNo} -> OK"); }
        catch (Exception ex) { uErr++; Console.WriteLine($"  user {u.EmployeeNo} -> FAIL: {ex.Message}"); }
    }
    foreach (var f in fps)
    {
        try { await d.UpsertFingerprintAsync(f, ct); fOk++; Console.WriteLine($"  fingerprint {f.EmployeeNo}#{f.FingerIndex} ({f.Template.Length}b) -> OK"); }
        catch (Exception ex) { fErr++; Console.WriteLine($"  fingerprint {f.EmployeeNo}#{f.FingerIndex} -> FAIL: {ex.Message}"); }
    }
    Console.WriteLine($"\nSync done. Users {uOk} ok / {uErr} fail. Fingerprints {fOk} ok / {fErr} fail.");
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
    await Hit("Face lib capabilities", HttpMethod.Get, "/ISAPI/Intelligent/FDLib/capabilities?format=json", null);
}
