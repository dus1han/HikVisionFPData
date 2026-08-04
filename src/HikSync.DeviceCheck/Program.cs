using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Logic;
using HikSync.Core.Models;
using System.Net;
using System.Net.Http.Headers;
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
          --compare <ip>     read-only: what a two-way sync would transfer between two devices
          --fp-inventory     every fingerprint record, incl. types the sync reader filters out
          --push-fp <emp> --from <ip>   copy one fingerprint, print the device verdict, verify
          --fp-dup-test [n]  prove the device refuses already-enrolled fingers (names the owner)
          --fp-repair --from <ip> [--apply]  rebuild enrolments stored under a non-attendance type
          --isapi <path> [--method M] [--body JSON]   raw authenticated ISAPI call
          --only <emp,...>   restrict --sync-to to specific employees
          --fp-selftest <emp>  find the FingerPrintDownload payload this firmware accepts (writes the
                               employee's own fingerprint back unchanged — safe in production)
          --fp-sdk-writeback <emp>  read fingerprint via ISAPI, write it back via HCNetSDK (needs
                               --transport sdk --port 8000). Tests the iVMS method. Non-destructive.
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

// Diagnostic: --fp-sdk-writeback <emp> reads the employee's fingerprint via ISAPI and writes it back
// to the SAME device via the HCNetSDK (NET_DVR_SET_FINGERPRINT) — the path iVMS uses. Non-destructive.
// Run with --transport sdk --port 8000.
if (opts.TryGetValue("fp-sdk-writeback", out var fpwEmp))
{
    return await RunFpSdkWriteback(sdkOptions, loggerFactory, endpoint, fpwEmp, ct);
}

// Diagnostic: --push-fp <emp> --from <ip> reads that employee's template from the source device,
// applies it to --ip, prints the device's raw verdict, and re-reads to prove persistence.
if (opts.TryGetValue("push-fp", out var pushEmp))
{
    var fromEp = new DeviceEndpoint
    {
        Ip = Get("from", ""), Port = port,
        Username = Get("from-user", endpoint.Username), Password = Get("from-pass", endpoint.Password),
    };
    if (string.IsNullOrWhiteSpace(fromEp.Ip)) { Console.Error.WriteLine("--push-fp requires --from <ip>"); return 2; }
    return await RunPushFp(sdkOptions, loggerFactory, fromEp, endpoint, pushEmp, Get("as-emp", pushEmp), ct);
}

// Maintenance: --fp-repair --from <ip> rebuilds enrolments stored under a non-attendance fingerprint
// type. Dry run unless --apply is given.
if (flags.Contains("fp-repair") || opts.ContainsKey("fp-repair"))
{
    var fromEp = new DeviceEndpoint
    {
        Ip = Get("from", ""), Port = port,
        Username = Get("from-user", endpoint.Username), Password = Get("from-pass", endpoint.Password),
    };
    if (string.IsNullOrWhiteSpace(fromEp.Ip)) { Console.Error.WriteLine("--fp-repair requires --from <ip>"); return 2; }
    return await RunFpRepair(sdkOptions, loggerFactory, fromEp, endpoint, flags.Contains("apply"), ct);
}

// Diagnostic: --isapi <path> [--method M] [--body JSON] — raw authenticated ISAPI call.
if (opts.TryGetValue("isapi", out var rawPath))
{
    using var rawHandler = new HttpClientHandler { Credentials = new NetworkCredential(endpoint.Username, endpoint.Password) };
    using var rawHttp = new HttpClient(rawHandler)
    {
        BaseAddress = new Uri($"http://{endpoint.Ip}:{sdkOptions.Value.IsapiPort}/"),
        Timeout = TimeSpan.FromSeconds(20),
    };
    using var rawReq = new HttpRequestMessage(new HttpMethod(Get("method", opts.ContainsKey("body") ? "POST" : "GET")), rawPath);
    if (opts.TryGetValue("body", out var rawBody))
        rawReq.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
    using var rawResp = await rawHttp.SendAsync(rawReq, ct);
    Console.WriteLine($"HTTP {(int)rawResp.StatusCode}");
    Console.WriteLine((await rawResp.Content.ReadAsStringAsync(ct)).Replace("\t", ""));
    return rawResp.IsSuccessStatusCode ? 0 : 1;
}

// Diagnostic: --fp-inventory lists EVERY fingerprint record on the device, including the types the
// sync reader deliberately skips. Read-only.
if (flags.Contains("fp-inventory"))
{
    var invFactory = new IsapiAccessDeviceFactory(sdkOptions, loggerFactory);
    await using var invDev = (IsapiAccessDevice)await invFactory.ConnectAsync(endpoint, ct);
    var people = new List<string>();
    await foreach (var u in invDev.ReadUsersAsync(ct)) people.Add(u.EmployeeNo);

    var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    int noFp = 0;
    Console.WriteLine($"\nFINGERPRINT INVENTORY — {endpoint.Ip} ({people.Count} people)\n");
    foreach (var p in people)
    {
        var raw = await invDev.ReadRawFingerprintsAsync(p, ct);
        if (raw.Count == 0) { noFp++; continue; }
        foreach (var r in raw)
            byType[r.FingerType] = byType.GetValueOrDefault(r.FingerType) + 1;
        Console.WriteLine($"  {p,-8} {string.Join(", ", raw.Select(r => $"slot {r.Slot}: {r.FingerType} ({r.Bytes}B)"))}");
    }
    Console.WriteLine($"\ntotals: {string.Join(", ", byType.Select(kv => $"{kv.Key}={kv.Value}"))}; people with no fingerprint = {noFp}");
    return 0;
}

// Diagnostic: --fp-type-test <emp> --from <ip> — why a pushed normalFP can come back dismissingFP.
if (opts.TryGetValue("fp-type-test", out var typeEmp))
{
    var fromEp = new DeviceEndpoint
    {
        Ip = Get("from", ""), Port = port,
        Username = Get("from-user", endpoint.Username), Password = Get("from-pass", endpoint.Password),
    };
    if (string.IsNullOrWhiteSpace(fromEp.Ip)) { Console.Error.WriteLine("--fp-type-test requires --from <ip>"); return 2; }
    return await RunFpTypeTest(sdkOptions, loggerFactory, fromEp, endpoint, typeEmp, Get("lab-emp", "999123"), ct);
}

// Diagnostic: --fp-dup-test writes several different employees' templates to one throwaway person to
// show whether the device is refusing duplicate fingers. Only the throwaway user is written to.
if (flags.Contains("fp-dup-test") || opts.ContainsKey("fp-dup-test"))
{
    return await RunFpDupTest(sdkOptions, loggerFactory, endpoint, Get("lab-emp", "999123"), GetInt("fp-dup-test", 4), ct);
}

// Diagnostic: --fp-sdk-lab <emp> sweeps the SET_FINGERPRINT parameters against a THROWAWAY employee
// (created and deleted by the test) to find what the device actually accepts. Run with --transport sdk.
if (opts.TryGetValue("fp-sdk-lab", out var labEmp))
{
    return await RunFpSdkLab(sdkOptions, loggerFactory, endpoint, labEmp, Get("lab-emp", "999123"), ct);
}

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
if (opts.TryGetValue("compare", out var compareIp))
{
    var otherEp = new DeviceEndpoint { Ip = compareIp, Port = port, Username = Get("to-user", endpoint.Username), Password = Get("to-pass", endpoint.Password) };
    await CompareDevices(factory, endpoint, otherEp, ct);
    return 0;
}
if (opts.TryGetValue("sync-to", out var syncTargetIp))
{
    var targetEp = new DeviceEndpoint { Ip = syncTargetIp, Port = port, Username = Get("to-user", endpoint.Username), Password = Get("to-pass", endpoint.Password) };
    var only = opts.TryGetValue("only", out var onlySpec)
        ? onlySpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
        : null;
    await SyncTo(factory, endpoint, targetEp, only, ct);
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

// READ-ONLY: reports what a two-way union sync would have to transfer between two devices.
static async Task CompareDevices(IAccessDeviceFactory factory, DeviceEndpoint a, DeviceEndpoint b, CancellationToken ct)
{
    async Task<(List<DeviceUser> Users, List<FingerprintTemplate> Fps)> Read(DeviceEndpoint ep)
    {
        await using var d = await factory.ConnectAsync(ep, ct);
        var users = new List<DeviceUser>();
        await foreach (var u in d.ReadUsersAsync(ct)) users.Add(u);
        var fps = new List<FingerprintTemplate>();
        await foreach (var f in d.ReadFingerprintsAsync(ct)) fps.Add(f);
        return (users, fps);
    }

    var (usersA, fpsA) = await Read(a);
    var (usersB, fpsB) = await Read(b);

    var withA = fpsA.Select(f => f.EmployeeNo).ToHashSet(StringComparer.Ordinal);
    var withB = fpsB.Select(f => f.EmployeeNo).ToHashSet(StringComparer.Ordinal);

    Console.WriteLine($"\n{a.Ip}: {usersA.Count} users, {fpsA.Count} fingerprints across {withA.Count} people");
    Console.WriteLine($"{b.Ip}: {usersB.Count} users, {fpsB.Count} fingerprints across {withB.Count} people\n");

    var missingOnB = withA.Except(withB).OrderBy(x => x).ToList();
    var missingOnA = withB.Except(withA).OrderBy(x => x).ToList();
    Console.WriteLine($"fingerprints on {a.Ip} but not {b.Ip}: {missingOnB.Count}  [{string.Join(", ", missingOnB)}]");
    Console.WriteLine($"fingerprints on {b.Ip} but not {a.Ip}: {missingOnA.Count}  [{string.Join(", ", missingOnA)}]");

    Console.WriteLine($"\nusers with no fingerprint on {a.Ip}: [{string.Join(", ", usersA.Select(u => u.EmployeeNo).Where(e => !withA.Contains(e)))}]");
    Console.WriteLine($"users with no fingerprint on {b.Ip}: [{string.Join(", ", usersB.Select(u => u.EmployeeNo).Where(e => !withB.Contains(e)))}]");

    int same = 0, differ = 0;
    foreach (var emp in withA.Intersect(withB))
    {
        var ta = fpsA.First(f => f.EmployeeNo == emp).Template;
        var tb = fpsB.First(f => f.EmployeeNo == emp).Template;
        if (ta.AsSpan().SequenceEqual(tb)) same++; else differ++;
    }
    Console.WriteLine($"\nshared people: {same} byte-identical template(s), {differ} differing (independently enrolled)");
}

static async Task SyncTo(IAccessDeviceFactory factory, DeviceEndpoint src, DeviceEndpoint dst, HashSet<string>? only, CancellationToken ct)
{
    Console.WriteLine($"Sync {src.Ip} -> {dst.Ip} (user={dst.Username})"
        + (only is null ? "\n" : $"  [restricted to: {string.Join(", ", only)}]\n"));
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

    if (only is not null)
    {
        static void Restrict(SyncPlan p, HashSet<string> keep)
        {
            p.UsersToUpsert.RemoveAll(u => !keep.Contains(u.EmployeeNo));
            p.FingerprintsToUpsert.RemoveAll(f => !keep.Contains(f.EmployeeNo));
            p.EmployeesToDelete.RemoveAll(e => !keep.Contains(e));
        }
        Restrict(toDst, only);
        Restrict(toSrc, only);
    }

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

// Reads <emp>'s fingerprint via ISAPI, then writes it back to the SAME device via the HCNetSDK. Tests
// whether NET_DVR_SET_FINGERPRINT accepts the template (the method iVMS uses). Non-destructive.
static async Task<int> RunFpSdkWriteback(
    Microsoft.Extensions.Options.IOptions<SdkOptions> sdkOptions,
    ILoggerFactory lf, DeviceEndpoint endpoint, string emp, CancellationToken ct)
{
    Console.WriteLine($"\nFP SDK PERSIST TEST  employee={emp}");
    Console.WriteLine("Reads the employee's fingerprint, writes it to a FREE finger slot via the SDK, then");
    Console.WriteLine("re-reads to prove the slot actually gained a print (real persistence, not an overwrite).\n");

    // 1. Read the employee's current fingerprints over ISAPI (which slots are used).
    var current = new List<FingerprintTemplate>();
    try
    {
        var isapiFactory = new HikSync.Device.Isapi.IsapiAccessDeviceFactory(sdkOptions, lf);
        await using var readDev = await isapiFactory.ConnectAsync(endpoint, ct);
        await foreach (var f in readDev.ReadFingerprintsAsync(ct))
            if (string.Equals(f.EmployeeNo, emp, StringComparison.Ordinal)) current.Add(f);
    }
    catch (Exception ex) { Console.WriteLine("[FAIL] ISAPI read failed: " + ex.Message); return 1; }

    if (current.Count == 0) { Console.WriteLine($"employee {emp} has no fingerprint to copy from."); return 1; }

    var used = current.Select(f => f.FingerIndex).ToHashSet();
    int freeSlot = Enumerable.Range(1, 10).FirstOrDefault(i => !used.Contains(i));
    if (freeSlot == 0) { Console.WriteLine("all 10 finger slots are in use; cannot test a fresh add."); return 1; }

    var source = current[0];
    Console.WriteLine($"ISAPI read OK: {current.Count} print(s), slots [{string.Join(",", used.OrderBy(x => x))}]. Will write to FREE slot {freeSlot}.\n");

    // 2. Write the template to the FREE slot via the SDK — a genuine new record, like the sync does.
    try
    {
        var sdkMgr = new HcNetSdkManager(sdkOptions, lf.CreateLogger<HcNetSdkManager>());
        var sdkFactory = new HikvisionDeviceFactory(sdkMgr, lf);
        await using var sdkDev = await sdkFactory.ConnectAsync(endpoint, ct);
        await sdkDev.UpsertFingerprintAsync(new FingerprintTemplate
        {
            EmployeeNo = emp, FingerIndex = freeSlot, FingerType = source.FingerType, Template = source.Template,
        }, ct);
        Console.WriteLine($"[ OK ] SDK NET_DVR_SET_FINGERPRINT returned success for slot {freeSlot}.\n");
    }
    catch (Exception ex) { Console.WriteLine("[FAIL] SDK write failed: " + ex.Message); return 1; }

    // 3. Re-read: did slot `freeSlot` actually appear? THIS is the real persistence check — a slot the
    // employee did not have before must now exist.
    try
    {
        var verifyFactory = new HikSync.Device.Isapi.IsapiAccessDeviceFactory(sdkOptions, lf);
        await using var verifyDev = await verifyFactory.ConnectAsync(endpoint, ct);
        bool appeared = false;
        await foreach (var f in verifyDev.ReadFingerprintsAsync(ct))
            if (string.Equals(f.EmployeeNo, emp, StringComparison.Ordinal) && f.FingerIndex == freeSlot) { appeared = true; break; }

        if (appeared)
            Console.WriteLine($"[ OK ] PERSISTED: slot {freeSlot} now exists on the device. SDK fingerprint writes really store — sync will work. (Delete this test finger via the device UI if you like.)");
        else
            Console.WriteLine($"[FAIL] NOT PERSISTED: slot {freeSlot} still absent after a 'successful' write. The SDK reports success but stores nothing — this is the sync's problem.");
        return appeared ? 0 : 1;
    }
    catch (Exception ex) { Console.WriteLine("[WARN] verify read failed: " + ex.Message); return 1; }
}

// Repairs enrolments that exist on the target but under a non-attendance fingerprint type
// (dismissingFP etc.). The device fixes a record's type at CREATE time and ignores it on update, so
// the only way to correct one is to remove the record and write it again — and the only removal this
// firmware accepts is deleting the person. Both are reconstructed from the source device.
// Dry-run unless --apply is passed.
static async Task<int> RunFpRepair(
    Microsoft.Extensions.Options.IOptions<SdkOptions> sdkOptions,
    ILoggerFactory lf, DeviceEndpoint from, DeviceEndpoint to, bool apply, CancellationToken ct)
{
    var factory = new IsapiAccessDeviceFactory(sdkOptions, lf);
    Console.WriteLine($"\nFINGERPRINT TYPE REPAIR  source {from.Ip} -> target {to.Ip}   ({(apply ? "APPLY" : "DRY RUN — pass --apply to execute")})\n");

    var sourceUsers = new Dictionary<string, DeviceUser>(StringComparer.Ordinal);
    var sourceFps = new Dictionary<string, List<FingerprintTemplate>>(StringComparer.Ordinal);
    await using (var src = (IsapiAccessDevice)await factory.ConnectAsync(from, ct))
    {
        await foreach (var u in src.ReadUsersAsync(ct)) sourceUsers[u.EmployeeNo] = u;
        await foreach (var f in src.ReadFingerprintsAsync(ct))
        {
            if (f.Template.Length == 0) continue;
            if (!sourceFps.TryGetValue(f.EmployeeNo, out var l)) sourceFps[f.EmployeeNo] = l = new();
            l.Add(f);
        }
    }
    Console.WriteLine($"{from.Ip}: {sourceUsers.Count} people, {sourceFps.Count} with a normalFP template\n");

    await using var dst = (IsapiAccessDevice)await factory.ConnectAsync(to, ct);
    var targetUsers = new List<DeviceUser>();
    await foreach (var u in dst.ReadUsersAsync(ct)) targetUsers.Add(u);

    var broken = new List<(string Emp, string Types)>();
    foreach (var u in targetUsers)
    {
        var raw = await dst.ReadRawFingerprintsAsync(u.EmployeeNo, ct);
        if (raw.Count == 0) continue;
        if (raw.Any(r => string.Equals(r.FingerType, "normalFP", StringComparison.OrdinalIgnoreCase))) continue;
        if (!sourceFps.ContainsKey(u.EmployeeNo)) continue; // nothing to rebuild from — leave it alone
        broken.Add((u.EmployeeNo, string.Join("/", raw.Select(r => r.FingerType))));
    }

    Console.WriteLine($"{to.Ip}: {broken.Count} person(s) enrolled only under a non-attendance type and repairable from the source:");
    foreach (var b in broken) Console.WriteLine($"  {b.Emp,-8} currently {b.Types}");
    if (broken.Count == 0) { Console.WriteLine("\nnothing to repair."); return 0; }
    if (!apply) { Console.WriteLine("\nDry run — nothing was changed. Re-run with --apply to repair."); return 0; }

    Console.WriteLine();
    int fixedCount = 0, failed = 0;
    foreach (var (emp, _) in broken)
    {
        try
        {
            await dst.DeleteUserAsync(emp, ct);
            var user = sourceUsers.TryGetValue(emp, out var su)
                ? new DeviceUser { EmployeeNo = emp, Name = su.Name, Enabled = su.Enabled, UserType = su.UserType }
                : new DeviceUser { EmployeeNo = emp, Name = emp, Enabled = true };
            await dst.UpsertUserAsync(user, ct);

            bool allOk = true;
            foreach (var fp in sourceFps[emp])
            {
                var status = await dst.SetFingerprintAsync(
                    new FingerprintTemplate { EmployeeNo = emp, FingerIndex = fp.FingerIndex, FingerType = "normalFP", Template = fp.Template }, ct);
                if (!status.Accepted) { allOk = false; Console.WriteLine($"  {emp,-8} FAILED: {status}"); }
            }

            var after = await dst.ReadRawFingerprintsAsync(emp, ct);
            bool nowNormal = after.Any(r => string.Equals(r.FingerType, "normalFP", StringComparison.OrdinalIgnoreCase));
            if (allOk && nowNormal) { fixedCount++; Console.WriteLine($"  {emp,-8} repaired -> {string.Join("/", after.Select(r => r.FingerType))}"); }
            else { failed++; Console.WriteLine($"  {emp,-8} NOT repaired -> [{string.Join("/", after.Select(r => r.FingerType))}]"); }
        }
        catch (Exception ex) { failed++; Console.WriteLine($"  {emp,-8} ERROR: {ex.Message.Split('\n')[0]}"); }
    }

    Console.WriteLine($"\nrepaired {fixedCount}, failed {failed}, of {broken.Count}.");
    return failed == 0 ? 0 : 1;
}

// Applies one non-duplicate template to a THROWAWAY employee under several fingerType spellings and
// reports what the device actually stored. Answers "why does a pushed normalFP come back dismissingFP".
static async Task<int> RunFpTypeTest(
    Microsoft.Extensions.Options.IOptions<SdkOptions> sdkOptions,
    ILoggerFactory lf, DeviceEndpoint from, DeviceEndpoint to, string srcEmp, string labEmp, CancellationToken ct)
{
    var factory = new IsapiAccessDeviceFactory(sdkOptions, lf);
    Console.WriteLine($"\nFINGERTYPE TEST — template of {srcEmp} from {from.Ip} -> throwaway {labEmp} on {to.Ip}\n");

    FingerprintTemplate? source = null;
    await using (var src = (IsapiAccessDevice)await factory.ConnectAsync(from, ct))
        await foreach (var f in src.ReadFingerprintsAsync(ct))
            if (string.Equals(f.EmployeeNo, srcEmp, StringComparison.Ordinal)) { source = f; break; }
    if (source is null) { Console.WriteLine($"{from.Ip} has no normalFP for {srcEmp}."); return 1; }
    Console.WriteLine($"source template: {source.Template.Length} bytes, declared type '{source.FingerType}'\n");

    await using var dst = (IsapiAccessDevice)await factory.ConnectAsync(to, ct);
    await dst.UpsertUserAsync(new DeviceUser { EmployeeNo = labEmp, Name = "HIKSYNC_FPLAB", Enabled = true }, ct);

    var variants = new (string Label, Dictionary<string, object?>? Extra)[]
    {
        ("fingerType=normalFP (as shipped)",     null),
        ("fingerType=dismissingFP (control)",    new() { ["fingerType"] = "dismissingFP" }),
        ("fingerType omitted entirely",          new() { ["fingerType"] = null }),
        ("fingerType=normalFP + fingerPrintType",new() { ["fingerPrintType"] = "normalFP" }),
        ("enableCardReader=[1] + cardReaderNo=1",new() { ["cardReaderNo"] = 1 }),
    };

    try
    {
        int slot = 1;
        foreach (var (label, extra) in variants)
        {
            var fp = new FingerprintTemplate { EmployeeNo = labEmp, FingerIndex = slot, FingerType = "normalFP", Template = source.Template };
            IsapiFingerprintStatus status;
            try { status = await dst.SetFingerprintAsync(fp, extra, ct); }
            catch (Exception ex) { Console.WriteLine($"  {label,-40} ERROR {ex.Message.Split('\n')[0]}"); slot++; continue; }

            var raw = await dst.ReadRawFingerprintsAsync(labEmp, ct);
            var stored = raw.FirstOrDefault(r => r.Slot == slot);
            Console.WriteLine($"  {label,-40} recv={status.RecvStatus} msg='{status.ErrorMessage}' -> stored slot {slot}: " +
                              (stored.FingerType is null or "" ? "(absent)" : $"type='{stored.FingerType}' {stored.Bytes}B"));
            slot++;
        }

        Console.WriteLine($"\nall records on {labEmp}: " +
            string.Join(", ", (await dst.ReadRawFingerprintsAsync(labEmp, ct)).Select(r => $"slot {r.Slot}={r.FingerType}")));
    }
    finally
    {
        try { await dst.DeleteUserAsync(labEmp, ct); Console.WriteLine($"cleanup: deleted {labEmp}"); }
        catch (Exception ex) { Console.WriteLine($"cleanup FAILED for {labEmp}: {ex.Message}"); }
    }
    return 0;
}

// Copies one employee's fingerprint from `from` to `to` over ISAPI and reports the raw device verdict
// plus a re-read, so "reported OK" and "actually stored" can be told apart.
static async Task<int> RunPushFp(
    Microsoft.Extensions.Options.IOptions<SdkOptions> sdkOptions,
    ILoggerFactory lf, DeviceEndpoint from, DeviceEndpoint to, string emp, string asEmp, CancellationToken ct)
{
    var factory = new IsapiAccessDeviceFactory(sdkOptions, lf);
    Console.WriteLine($"\nPUSH FINGERPRINT  {from.Ip} employee {emp}  ->  {to.Ip} employee {asEmp}\n");

    var source = new List<FingerprintTemplate>();
    await using (var src = (IsapiAccessDevice)await factory.ConnectAsync(from, ct))
        await foreach (var f in src.ReadFingerprintsAsync(ct))
            if (string.Equals(f.EmployeeNo, emp, StringComparison.Ordinal)) source.Add(f);

    if (source.Count == 0) { Console.WriteLine($"{from.Ip} has no fingerprint for {emp}."); return 1; }
    Console.WriteLine($"source: {source.Count} template(s), slots [{string.Join(",", source.Select(s => s.FingerIndex))}], {source[0].Template.Length} bytes\n");

    await using var dst = (IsapiAccessDevice)await factory.ConnectAsync(to, ct);

    async Task<List<int>> SlotsOnTarget()
    {
        var slots = new List<int>();
        await foreach (var f in dst.ReadFingerprintsAsync(ct))
            if (string.Equals(f.EmployeeNo, asEmp, StringComparison.Ordinal)) slots.Add(f.FingerIndex);
        return slots;
    }

    Console.WriteLine($"target slots before: [{string.Join(",", await SlotsOnTarget())}]");

    await dst.UpsertUserAsync(new DeviceUser { EmployeeNo = asEmp, Name = asEmp, Enabled = true }, ct);
    Console.WriteLine($"target person {asEmp} ensured");

    foreach (var f in source)
    {
        var status = await dst.SetFingerprintAsync(
            new FingerprintTemplate { EmployeeNo = asEmp, FingerIndex = f.FingerIndex, FingerType = f.FingerType, Template = f.Template }, ct);
        Console.WriteLine($"  apply slot {f.FingerIndex}: recvStatus={status.RecvStatus} errorMsg='{status.ErrorMessage}' -> {status.Describe()}");
    }

    var after = await SlotsOnTarget();
    Console.WriteLine($"\ntarget slots after : [{string.Join(",", after)}]");
    bool ok = source.All(f => after.Contains(f.FingerIndex));
    Console.WriteLine(ok ? "RESULT: PERSISTED." : "RESULT: NOT PERSISTED.");
    return ok ? 0 : 1;
}

// Writes several DIFFERENT source templates to one throwaway employee over ISAPI FingerPrint/SetUp.
// If the device is refusing duplicates, each rejection names the employee that already owns that
// template — so errorMsg should track the source. Only the throwaway user is ever written to.
static async Task<int> RunFpDupTest(
    Microsoft.Extensions.Options.IOptions<SdkOptions> sdkOptions,
    ILoggerFactory lf, DeviceEndpoint endpoint, string labEmp, int sampleCount, CancellationToken ct)
{
    Console.WriteLine($"\nFP DUPLICATE TEST on {endpoint.Ip} — throwaway employee {labEmp}");
    Console.WriteLine("Writes several different employees' templates to one throwaway person.");
    Console.WriteLine("If the device dedups fingers, each rejection should name a DIFFERENT owner.\n");

    var factory = new IsapiAccessDeviceFactory(sdkOptions, lf);
    await using var dev = (IsapiAccessDevice)await factory.ConnectAsync(endpoint, ct);

    var samples = new List<FingerprintTemplate>();
    await foreach (var f in dev.ReadFingerprintsAsync(ct))
    {
        if (f.Template.Length == 0) continue;
        samples.Add(f);
        if (samples.Count >= sampleCount) break;
    }
    if (samples.Count == 0) { Console.WriteLine("no templates to sample."); return 1; }
    Console.WriteLine($"sampled {samples.Count} template(s) from: {string.Join(", ", samples.Select(s => s.EmployeeNo))}\n");

    await dev.UpsertUserAsync(new DeviceUser { EmployeeNo = labEmp, Name = "HIKSYNC_FPLAB", Enabled = true }, ct);
    Console.WriteLine($"created throwaway person {labEmp}\n");

    try
    {
        foreach (var s in samples)
        {
            var status = await dev.SetFingerprintAsync(
                new FingerprintTemplate { EmployeeNo = labEmp, FingerIndex = 1, FingerType = "normalFP", Template = s.Template }, ct);
            Console.WriteLine($"  template of employee {s.EmployeeNo,-8} -> recvStatus={status.RecvStatus}, errorMsg='{status.ErrorMessage}'  {(status.Accepted ? "STORED" : "")}");
        }

        var slots = new List<int>();
        await foreach (var f in dev.ReadFingerprintsAsync(ct))
            if (string.Equals(f.EmployeeNo, labEmp, StringComparison.Ordinal)) slots.Add(f.FingerIndex);
        Console.WriteLine($"\nslots now on {labEmp}: [{string.Join(",", slots)}]");
        Console.WriteLine(
            samples.Count > 1 && samples.Select(s => s.EmployeeNo).Distinct().Count() > 1
                ? "\nIf each errorMsg equals the source employee, the device is refusing DUPLICATE fingers —\n" +
                  "which is exactly what every previous test wrote. A genuinely new finger should store."
                : "");
    }
    finally
    {
        try { await dev.DeleteUserAsync(labEmp, ct); Console.WriteLine($"cleanup: deleted {labEmp}"); }
        catch (Exception ex) { Console.WriteLine($"cleanup FAILED for {labEmp}: {ex.Message}"); }
    }
    return 0;
}

// Sweeps NET_DVR_SET_FINGERPRINT parameters against a throwaway employee, to find what the device
// accepts. The template comes from <srcEmp>; the writes all target <labEmp>, which this test creates
// and deletes, so no real person's enrolment is touched.
static async Task<int> RunFpSdkLab(
    Microsoft.Extensions.Options.IOptions<SdkOptions> sdkOptions,
    ILoggerFactory lf, DeviceEndpoint endpoint, string srcEmp, string labEmp, CancellationToken ct)
{
    Console.WriteLine($"\nFP SDK LAB — template from employee {srcEmp}, written to throwaway employee {labEmp}\n");

    var isapiFactory = new IsapiAccessDeviceFactory(sdkOptions, lf);

    // 1. Source template (ISAPI read is the path that works).
    FingerprintTemplate? source = null;
    await using (var readDev = await isapiFactory.ConnectAsync(endpoint, ct))
    {
        await foreach (var f in readDev.ReadFingerprintsAsync(ct))
            if (string.Equals(f.EmployeeNo, srcEmp, StringComparison.Ordinal)) { source = f; break; }
    }
    if (source is null) { Console.WriteLine($"employee {srcEmp} has no fingerprint to copy."); return 1; }
    Console.WriteLine($"source template: {source.Template.Length} bytes, finger #{source.FingerIndex}, type={source.FingerType}\n");

    var sdkMgr = new HcNetSdkManager(sdkOptions, lf.CreateLogger<HcNetSdkManager>());
    var sdkFactory = new HikvisionDeviceFactory(sdkMgr, lf);

    async Task<int> CountCards()
    {
        await using var d = await sdkFactory.ConnectAsync(endpoint, ct);
        int n = 0;
        await foreach (var _ in d.ReadUsersAsync(ct)) n++;
        return n;
    }

    async Task<bool> IsapiHasUser(string emp)
    {
        await using var d = await isapiFactory.ConnectAsync(endpoint, ct);
        await foreach (var u in d.ReadUsersAsync(ct))
            if (string.Equals(u.EmployeeNo, emp, StringComparison.Ordinal)) return true;
        return false;
    }

    async Task<List<int>> IsapiSlots(string emp)
    {
        await using var d = await isapiFactory.ConnectAsync(endpoint, ct);
        var slots = new List<int>();
        await foreach (var f in d.ReadFingerprintsAsync(ct))
            if (string.Equals(f.EmployeeNo, emp, StringComparison.Ordinal)) slots.Add(f.FingerIndex);
        return slots;
    }

    Console.WriteLine($"cards visible to the SDK (NET_DVR_GET_CARD): {await CountCards()}");

    // 2. Create the throwaway PERSON over ISAPI (card-less, exactly like the real users).
    await using (var d = await isapiFactory.ConnectAsync(endpoint, ct))
        await d.UpsertUserAsync(new DeviceUser { EmployeeNo = labEmp, Name = "HIKSYNC_FPLAB", Enabled = true }, ct);
    Console.WriteLine($"created person {labEmp} over ISAPI: exists={await IsapiHasUser(labEmp)}\n");

    async Task Sweep(string phase)
    {
        Console.WriteLine($"--- {phase} ---");
        await using var sdkDev = (HikvisionAccessDevice)await sdkFactory.ConnectAsync(endpoint, ct);
        foreach (var (label, type, reader) in new (string, byte?, uint?)[]
        {
            ("byFingerType=1 reader=1", (byte)1, 1u),
            ("byFingerType=0 reader=1", (byte)0, 1u),
            ("byFingerType=1 reader=0", (byte)1, 0u),
            ("byFingerType=2 reader=1", (byte)2, 1u),
        })
        {
            try
            {
                var r = await sdkDev.TrySetFingerprintAsync(
                    new FingerprintTemplate { EmployeeNo = labEmp, FingerIndex = 1, Template = source.Template }, type, reader, ct);
                Console.WriteLine($"  {label,-26} recvStatus={r.RecvStatus} ({HikvisionAccessDevice.DescribeRecvStatus(r.RecvStatus)}), readerStatus={r.CardReaderRecvStatus}, msg='{r.ErrorMessage}'");
            }
            catch (Exception ex) { Console.WriteLine($"  {label,-26} EXCEPTION: {ex.Message}"); }
        }
        Console.WriteLine($"  -> slots on {labEmp} after this phase: [{string.Join(",", await IsapiSlots(labEmp))}]\n");
    }

    await Sweep("PHASE 1: person exists, NO card (this is today's production state)");

    // 3. Give the person a card whose number is the employee no, then repeat.
    try
    {
        await using var sdkDev = await sdkFactory.ConnectAsync(endpoint, ct);
        await sdkDev.UpsertUserAsync(new DeviceUser { EmployeeNo = labEmp, Name = "HIKSYNC_FPLAB", Enabled = true }, ct);
        Console.WriteLine($"SDK SET_CARD for {labEmp} OK; cards visible to the SDK now: {await CountCards()}\n");
    }
    catch (Exception ex) { Console.WriteLine($"SDK SET_CARD failed: {ex.Message}\n"); }

    await Sweep("PHASE 2: person + card");

    // 4. Clean up the throwaway person (and its card).
    try
    {
        await using var d = await isapiFactory.ConnectAsync(endpoint, ct);
        await d.DeleteUserAsync(labEmp, ct);
        Console.WriteLine($"cleanup: deleted {labEmp}; still present={await IsapiHasUser(labEmp)}");
    }
    catch (Exception ex) { Console.WriteLine($"cleanup FAILED for {labEmp} — remove it via the device UI: {ex.Message}"); }

    return 0;
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

    // If inline base64 was refused for every scalar permutation, the template may need to go as raw
    // binary in a multipart/form-data body (JSON metadata part + binary template part). Part names are
    // firmware-specific, so try a few. Still non-destructive — the employee's own template.
    if (winner is null)
    {
        Console.WriteLine("=== multipart/form-data attempts (template as raw binary) ===\n");
        byte[] raw;
        try { raw = Convert.FromBase64String(data); }
        catch { Console.WriteLine("fingerData is not valid base64; cannot try multipart."); raw = Array.Empty<byte>(); }

        if (raw.Length > 0)
        {
            string jsonNoData = JsonSerializer.Serialize(new
            {
                FingerPrintCfg = new { employeeNo = emp, enableCardReader = new[] { 1 }, fingerPrintID = fingerId, fingerType }
            });

            var multipart = new (string Label, string JsonPart, string BinPart)[]
            {
                ("M1 FingerPrintCfg(json) + FingerPrintData(bin)",    "FingerPrintCfg",     "FingerPrintData"),
                ("M2 FingerPrintCfg(json) + fingerData(bin)",         "FingerPrintCfg",     "fingerData"),
                ("M3 FingerPrintDataInfo(json) + FingerPrintData(bin)","FingerPrintDataInfo","FingerPrintData"),
            };

            foreach (var (label, jsonPart, binPart) in multipart)
            {
                using var mp = new MultipartFormDataContent();
                mp.Add(new StringContent(jsonNoData, Encoding.UTF8, "application/json"), jsonPart);
                var bin = new ByteArrayContent(raw);
                bin.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                mp.Add(bin, binPart);

                using var req = new HttpRequestMessage(HttpMethod.Post, "/ISAPI/AccessControl/FingerPrintDownload?format=json") { Content = mp };
                try
                {
                    using var resp = await http.SendAsync(req);
                    string txt = await resp.Content.ReadAsStringAsync();
                    bool ok = resp.IsSuccessStatusCode
                        && !txt.Contains("badParameters") && !txt.Contains("ParametersLack")
                        && !txt.Contains("illegal") && !txt.Contains("errorFinger");
                    Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {label}");
                    Console.WriteLine($"        HTTP {(int)resp.StatusCode}: {(txt.Length > 300 ? txt[..300] + "…" : txt.Replace("\n", " ").Replace("\t", ""))}");
                    if (ok && winner is null) winner = label;
                }
                catch (Exception ex) { Console.WriteLine($"[ERR ] {label}: {ex.Message}"); }
                Console.WriteLine();
            }
        }
    }

    Console.WriteLine(winner is null
        ? "No payload shape was accepted (inline base64 or multipart). Send this whole output back."
        : $"WINNER: {winner}\nTell the maintainer this label so the sync payload can be locked to it.");
    return winner is null ? 1 : 0;
}
