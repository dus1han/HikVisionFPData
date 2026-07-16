using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HikSync.Core.Abstractions;
using HikSync.Core.Models;
using Microsoft.Extensions.Logging;
using static HikSync.Device.Hikvision.HikvisionInterop;

namespace HikSync.Device.Hikvision;

/// <summary>
/// HCNetSDK-backed device connection (SDK V6.1.9.4).
///
/// Implemented: login/lifecycle, device info, and attendance event read (NET_DVR_GET_ACS_EVENT).
/// The user/fingerprint read+write paths for sync are wired next (NET_DVR_GET/SET_USERINFO,
/// NET_DVR_GET/SET_FINGERPRINT_CFG) and currently throw.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HikvisionAccessDevice : IAccessDevice
{
    private readonly HcNetSdkManager _manager;
    private readonly int _userId;
    private readonly DeviceInfo _info;
    private readonly ILogger _logger;

    public HikvisionAccessDevice(HcNetSdkManager manager, int userId, DeviceEndpoint endpoint, DeviceInfo info, ILogger logger)
    {
        _manager = manager;
        _userId = userId;
        Endpoint = endpoint;
        _info = info;
        _logger = logger;
    }

    public DeviceEndpoint Endpoint { get; }

    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct) => Task.FromResult(_info);

    public async IAsyncEnumerable<AccessEvent> ReadEventsAsync(
        AcsEventQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        // The SDK search loop is blocking; run it off-thread and stream the collected results.
        var events = await Task.Run(() => ReadEventsBlocking(query, ct), ct).ConfigureAwait(false);
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    private List<AccessEvent> ReadEventsBlocking(AcsEventQuery query, CancellationToken ct)
    {
        var results = new List<AccessEvent>();

        var cond = new NET_DVR_ACS_EVENT_COND();
        cond.Init();
        cond.dwSize = (uint)Marshal.SizeOf<NET_DVR_ACS_EVENT_COND>();
        cond.dwMajor = 0;                 // 0 = all majors
        cond.dwMinor = 0;                 // 0 = all minors
        cond.byPicEnable = 0;             // no picture payload
        cond.byTimeType = 0;
        cond.wInductiveEventType = 65535;
        cond.struStartTime = ToDeviceTime(query.StartUtc, query.DeviceUtcOffset);
        cond.struEndTime = ToDeviceTime(query.EndUtc, query.DeviceUtcOffset);

        IntPtr condPtr = Marshal.AllocHGlobal((int)cond.dwSize);
        int handle;
        try
        {
            Marshal.StructureToPtr(cond, condPtr, false);
            handle = NET_DVR_StartRemoteConfig(_userId, NET_DVR_GET_ACS_EVENT, condPtr, (int)cond.dwSize, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(condPtr);
        }

        if (handle < 0)
            throw new HcNetSdkException($"NET_DVR_StartRemoteConfig(GET_ACS_EVENT, {Endpoint})", NET_DVR_GetLastError());

        try
        {
            var cfg = new NET_DVR_ACS_EVENT_CFG();
            cfg.Init();
            cfg.dwSize = (uint)Marshal.SizeOf<NET_DVR_ACS_EVENT_CFG>();
            int cfgSize = (int)cfg.dwSize;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int status = NET_DVR_GetNextRemoteConfig(handle, ref cfg, cfgSize);
                switch (status)
                {
                    case NEXT_STATUS_SUCCESS:
                        var mapped = Map(cfg, query.DeviceUtcOffset);
                        if (mapped is not null) results.Add(mapped);
                        break;
                    case NEXT_STATUS_NEED_WAIT:
                        Thread.Sleep(200);
                        break;
                    case NEXT_STATUS_FINISH:
                        return results;
                    default: // FAILED / unknown — don't advance the cursor; surface the error.
                        throw new HcNetSdkException($"NET_DVR_GetNextRemoteConfig(GET_ACS_EVENT, {Endpoint})", NET_DVR_GetLastError());
                }
            }
        }
        finally
        {
            NET_DVR_StopRemoteConfig(handle);
        }
    }

    private static AccessEvent? Map(NET_DVR_ACS_EVENT_CFG cfg, TimeSpan offset)
    {
        var info = cfg.struAcsEventInfo;

        string employeeNo = AsciiTrim(info.byEmployeeNo);
        if (string.IsNullOrEmpty(employeeNo))
        {
            if (info.dwEmployeeNo == 0) return null; // not a person verification (door/alarm event)
            employeeNo = info.dwEmployeeNo.ToString();
        }

        DateTime? eventUtc = FromDeviceTime(cfg.struTime, offset);
        if (eventUtc is null) return null;

        string card = AsciiTrim(info.byCardNo);

        return new AccessEvent
        {
            SerialNo = info.dwSerialNo,
            EventTimeUtc = eventUtc.Value,
            EmployeeNo = employeeNo,
            CardNo = string.IsNullOrEmpty(card) ? null : card,
            Major = (int)cfg.dwMajor,
            Minor = (int)cfg.dwMinor,
            VerifyMode = MapVerifyMode(info.byCurrentVerifyMode),
            Raw = $"verifyModeCode={info.byCurrentVerifyMode};doorNo={info.dwDoorNo};cardReaderNo={info.dwCardReaderNo}",
        };
    }

    private static NET_DVR_TIME ToDeviceTime(DateTime utc, TimeSpan offset)
    {
        DateTime local = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) + offset;
        return new NET_DVR_TIME
        {
            dwYear = local.Year,
            dwMonth = local.Month,
            dwDay = local.Day,
            dwHour = local.Hour,
            dwMinute = local.Minute,
            dwSecond = local.Second,
        };
    }

    private static DateTime? FromDeviceTime(NET_DVR_TIME t, TimeSpan offset)
    {
        if (t.dwYear < 2000 || t.dwMonth is < 1 or > 12 || t.dwDay is < 1 or > 31) return null;
        try
        {
            var local = new DateTime(t.dwYear, t.dwMonth, t.dwDay, t.dwHour, t.dwMinute, t.dwSecond, DateTimeKind.Unspecified);
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // Best-effort mapping of the device's current-verify-mode code. Unrecognized codes -> Unknown
    // (the raw code is preserved in AccessEvent.Raw). Refine against your device's verify-mode table
    // if you plan to filter on specific modes via Attendance:CountedVerifyModes.
    private static VerifyMode MapVerifyMode(byte code) => code switch
    {
        1 => VerifyMode.Card,
        2 or 3 or 4 => VerifyMode.Fingerprint,
        6 or 7 => VerifyMode.Face,
        _ => VerifyMode.Unknown,
    };

    private static string AsciiTrim(byte[] bytes)
    {
        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, len).Trim();
    }

    // --- Sync (IN -> OUT). This deployment uses employeeNo AS the card number, which is what the
    //     card/fingerprint APIs key on. Cards carry identity; fingerprints attach to the card no. ---

    // DS-K1A8503MF-B is a standalone door terminal; its card reader is number 1.
    private const int DefaultReaderNo = 1;

    public async IAsyncEnumerable<DeviceUser> ReadUsersAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var users = await Task.Run(() => ReadUsersBlocking(ct), ct).ConfigureAwait(false);
        foreach (var u in users) { ct.ThrowIfCancellationRequested(); yield return u; }
    }

    public async IAsyncEnumerable<FingerprintTemplate> ReadFingerprintsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var prints = await Task.Run(() => ReadFingerprintsBlocking(ct), ct).ConfigureAwait(false);
        foreach (var f in prints) { ct.ThrowIfCancellationRequested(); yield return f; }
    }

    public Task UpsertUserAsync(DeviceUser user, CancellationToken ct) =>
        Task.Run(() => SetCardBlocking(user), ct);

    public Task UpsertFingerprintAsync(FingerprintTemplate fingerprint, CancellationToken ct) =>
        Task.Run(() => SetFingerprintBlocking(fingerprint), ct);

    public Task DeleteUserAsync(string employeeNo, CancellationToken ct) =>
        throw new NotSupportedException(
            "DeleteUserAsync (NET_DVR_DEL_CARD union) is not implemented; sync delete is off by default. " +
            "Enable only after wiring/validating the delete structs on hardware.");

    private List<DeviceUser> ReadUsersBlocking(CancellationToken ct)
    {
        var cond = new NET_DVR_CARD_COND();
        cond.Init();
        cond.dwSize = (uint)Marshal.SizeOf<NET_DVR_CARD_COND>();
        cond.dwCardNum = 0xffffffff; // all cards

        int handle = StartRemoteConfig(NET_DVR_GET_CARD, cond, cond.dwSize);
        var users = new List<DeviceUser>();
        try
        {
            var rec = new NET_DVR_CARD_RECORD();
            rec.Init();
            rec.dwSize = (uint)Marshal.SizeOf<NET_DVR_CARD_RECORD>();
            int size = (int)rec.dwSize;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int status = NET_DVR_GetNextRemoteConfig(handle, ref rec, size);
                switch (status)
                {
                    case NEXT_STATUS_SUCCESS:
                        string emp = AsciiTrim(rec.byCardNo);
                        if (!string.IsNullOrEmpty(emp))
                            users.Add(new DeviceUser
                            {
                                EmployeeNo = emp,
                                Name = AsciiTrim(rec.byName),
                                Enabled = rec.struValid.byEnable == 1,
                                UserType = "normal",
                            });
                        break;
                    case NEXT_STATUS_NEED_WAIT: Thread.Sleep(100); break;
                    case NEXT_STATUS_FINISH: return users;
                    default:
                        throw new HcNetSdkException($"NET_DVR_GetNextRemoteConfig(GET_CARD, {Endpoint})", NET_DVR_GetLastError());
                }
            }
        }
        finally { NET_DVR_StopRemoteConfig(handle); }
    }

    private List<FingerprintTemplate> ReadFingerprintsBlocking(CancellationToken ct)
    {
        var result = new List<FingerprintTemplate>();
        foreach (var user in ReadUsersBlocking(ct))
        {
            ct.ThrowIfCancellationRequested();
            result.AddRange(ReadCardFingerprintsBlocking(user.EmployeeNo, ct));
        }
        return result;
    }

    private List<FingerprintTemplate> ReadCardFingerprintsBlocking(string cardNo, CancellationToken ct)
    {
        var cond = new NET_DVR_FINGERPRINT_COND();
        cond.Init();
        cond.dwSize = (uint)Marshal.SizeOf<NET_DVR_FINGERPRINT_COND>();
        cond.dwFingerPrintNum = 0xffffffff; // all fingers for this card
        cond.dwEnableReaderNo = DefaultReaderNo;
        cond.byFingerPrintID = 0;
        WriteAscii(cond.byCardNo, cardNo);

        var prints = new List<FingerprintTemplate>();
        int handle = StartRemoteConfig(NET_DVR_GET_FINGERPRINT, cond, cond.dwSize);
        try
        {
            var rec = new NET_DVR_FINGERPRINT_RECORD();
            rec.Init();
            rec.dwSize = (uint)Marshal.SizeOf<NET_DVR_FINGERPRINT_RECORD>();
            int size = (int)rec.dwSize;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int status = NET_DVR_GetNextRemoteConfig(handle, ref rec, size);
                if (status == NEXT_STATUS_SUCCESS)
                {
                    int len = (int)Math.Min(rec.dwFingerPrintLen, (uint)rec.byFingerData!.Length);
                    if (len > 0 && rec.byFingerPrintID is >= 1 and <= 10)
                    {
                        var template = new byte[len];
                        Array.Copy(rec.byFingerData, template, len);
                        prints.Add(new FingerprintTemplate { EmployeeNo = cardNo, FingerIndex = rec.byFingerPrintID, Template = template });
                    }
                }
                else if (status == NEXT_STATUS_NEED_WAIT) { Thread.Sleep(50); }
                else { return prints; } // FINISH / no data for this card
            }
        }
        finally { NET_DVR_StopRemoteConfig(handle); }
    }

    private void SetCardBlocking(DeviceUser user)
    {
        var cond = new NET_DVR_CARD_COND();
        cond.Init();
        cond.dwSize = (uint)Marshal.SizeOf<NET_DVR_CARD_COND>();
        cond.dwCardNum = 1;

        int handle = StartRemoteConfig(NET_DVR_SET_CARD, cond, cond.dwSize);
        try
        {
            var rec = new NET_DVR_CARD_RECORD();
            rec.Init();
            rec.dwSize = (uint)Marshal.SizeOf<NET_DVR_CARD_RECORD>();
            rec.byCardType = 1;   // normal card
            rec.byUserType = 0;   // normal user
            WriteAscii(rec.byCardNo, user.EmployeeNo);
            if (uint.TryParse(user.EmployeeNo, out uint empNo)) rec.dwEmployeeNo = empNo;
            WriteAscii(rec.byName, user.Name ?? string.Empty);
            rec.byDoorRight![0] = 1;
            rec.wCardRightPlan![0] = 1;
            rec.struValid.byEnable = (byte)(user.Enabled ? 1 : 0);
            rec.struValid.struBeginTime = ToTimeEx(user.ValidBeginUtc ?? new DateTime(2000, 1, 1));
            rec.struValid.struEndTime = ToTimeEx(user.ValidEndUtc ?? new DateTime(2037, 12, 31));

            SendOne(handle, rec, rec.dwSize, Marshal.SizeOf<NET_DVR_CARD_STATUS>(), $"SET_CARD({user.EmployeeNo})");
        }
        finally { NET_DVR_StopRemoteConfig(handle); }
    }

    private void SetFingerprintBlocking(FingerprintTemplate fp)
    {
        var cond = new NET_DVR_FINGERPRINT_COND();
        cond.Init();
        cond.dwSize = (uint)Marshal.SizeOf<NET_DVR_FINGERPRINT_COND>();
        cond.dwFingerPrintNum = 1;
        cond.dwEnableReaderNo = DefaultReaderNo;

        int handle = StartRemoteConfig(NET_DVR_SET_FINGERPRINT, cond, cond.dwSize);
        try
        {
            var rec = new NET_DVR_FINGERPRINT_RECORD();
            rec.Init();
            rec.dwSize = (uint)Marshal.SizeOf<NET_DVR_FINGERPRINT_RECORD>();
            WriteAscii(rec.byCardNo, fp.EmployeeNo);
            rec.dwEnableReaderNo = DefaultReaderNo;
            rec.byFingerPrintID = (byte)fp.FingerIndex;
            rec.byFingerType = 0; // normal
            int len = Math.Min(fp.Template.Length, rec.byFingerData!.Length);
            Array.Copy(fp.Template, rec.byFingerData, len);
            rec.dwFingerPrintLen = (uint)len;

            SendOne(handle, rec, rec.dwSize, Marshal.SizeOf<NET_DVR_FINGERPRINT_STATUS>(), $"SET_FINGERPRINT({fp.EmployeeNo}/{fp.FingerIndex})");
        }
        finally { NET_DVR_StopRemoteConfig(handle); }
    }

    // Marshals a condition struct, starts a remote-config session, returns the handle (throws on failure).
    private int StartRemoteConfig<T>(int command, T cond, uint size) where T : struct
    {
        IntPtr ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.StructureToPtr(cond, ptr, false);
            int handle = NET_DVR_StartRemoteConfig(_userId, command, ptr, (int)size, IntPtr.Zero, IntPtr.Zero);
            if (handle < 0)
                throw new HcNetSdkException($"NET_DVR_StartRemoteConfig({command}, {Endpoint})", NET_DVR_GetLastError());
            return handle;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // Sends one record via SendWithRecvRemoteConfig and drives the status loop until FINISH.
    private static void SendOne<T>(int handle, T record, uint inSize, int outSize, string op) where T : struct
    {
        IntPtr inPtr = Marshal.AllocHGlobal((int)inSize);
        IntPtr outPtr = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(record, inPtr, false);
            uint returned = 0;
            for (int guard = 0; guard < 32; guard++)
            {
                int status = NET_DVR_SendWithRecvRemoteConfig(handle, inPtr, inSize, outPtr, (uint)outSize, ref returned);
                switch (status)
                {
                    case SEND_STATUS_SUCCESS: break;          // accepted; next call returns FINISH
                    case SEND_STATUS_NEEDWAIT: Thread.Sleep(20); break;
                    case SEND_STATUS_FINISH: return;
                    default: throw new HcNetSdkException($"NET_DVR_SendWithRecvRemoteConfig({op})", NET_DVR_GetLastError());
                }
            }
            throw new HcNetSdkException($"NET_DVR_SendWithRecvRemoteConfig({op}) did not finish", NET_DVR_GetLastError());
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    private static NET_DVR_TIME_EX ToTimeEx(DateTime dt) => new()
    {
        wYear = (ushort)dt.Year,
        byMonth = (byte)dt.Month,
        byDay = (byte)dt.Day,
        byHour = (byte)dt.Hour,
        byMinute = (byte)dt.Minute,
        bySecond = (byte)dt.Second,
    };

    private static void WriteAscii(byte[]? dest, string value)
    {
        if (dest is null) return;
        Array.Clear(dest);
        byte[] src = Encoding.ASCII.GetBytes(value);
        Array.Copy(src, dest, Math.Min(src.Length, dest.Length));
    }

    public ValueTask DisposeAsync()
    {
        _manager.Logout(_userId);
        return ValueTask.CompletedTask;
    }
}
