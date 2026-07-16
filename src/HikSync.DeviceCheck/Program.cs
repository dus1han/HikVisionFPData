using HikSync.Core.Abstractions;
using HikSync.Core.Configuration;
using HikSync.Core.Logic;
using HikSync.Core.Models;
using HikSync.Device.Fake;
using HikSync.Device.Hikvision;
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
          --sdk-path <dir>   HCNetSDK native folder (default native)
          --write-test       upsert a test user and read it back (WRITES to the device)
          --test-emp <no>    employee/card no for --write-test (default 999001)
          --fake             use the in-memory fake device (self-test, no hardware)

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

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));

var sdkOptions = Options.Create(new SdkOptions { NativeLibraryPath = Get("sdk-path", "native"), UseFakeDevice = fake });
IAccessDeviceFactory factory = fake
    ? new FakeAccessDeviceFactory()
    : new HikvisionDeviceFactory(new HcNetSdkManager(sdkOptions, loggerFactory.CreateLogger<HcNetSdkManager>()), loggerFactory);

var endpoint = new DeviceEndpoint { Ip = ip, Port = port, Username = Get("user", "admin"), Password = Get("pass", "") };
var ct = CancellationToken.None;

int failures = 0;
void Head(string t) => Console.WriteLine($"\n=== {t} ===");
void Ok(string m) => Console.WriteLine($"  [ OK ] {m}");
void Fail(string m) { Console.WriteLine($"  [FAIL] {m}"); failures++; }
void Info(string m) => Console.WriteLine($"         {m}");

Console.WriteLine($"HikSync.DeviceCheck -> {(fake ? "FAKE device" : endpoint.ToString())}  (user={endpoint.Username})");

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
