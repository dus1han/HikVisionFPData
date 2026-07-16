using System.Runtime.InteropServices;

namespace HikSync.Device.Hikvision;

/// <summary>
/// P/Invoke surface for HCNetSDK (Device Network SDK).
///
/// IMPORTANT — verify before running against hardware:
///  * The native DLL set is x64; the host process must be x64 (see csproj PlatformTarget).
///  * Struct layouts below cover the login/lifecycle path. The remote-config command structures
///    (ACS event / user / fingerprint) are version-specific and MUST be transcribed from the
///    exact HCNetSDK build you deploy before the read/write paths will work. Until then run with
///    Sdk:UseFakeDevice = true.
/// </summary>
internal static class HikvisionInterop
{
    private const string Dll = "HCNetSDK.dll";

    // ---- Lifecycle ----
    [DllImport(Dll)] public static extern bool NET_DVR_Init();
    [DllImport(Dll)] public static extern bool NET_DVR_Cleanup();
    [DllImport(Dll)] public static extern uint NET_DVR_GetLastError();
    [DllImport(Dll)] public static extern bool NET_DVR_SetSDKInitCfg(int enumType, IntPtr lpInBuff);
    [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern bool NET_DVR_SetLogToFile(int level, string dir, bool waitComplete);

    // ---- Login / logout ----
    [DllImport(Dll)] public static extern int NET_DVR_Login_V40(ref NET_DVR_USER_LOGIN_INFO loginInfo, ref NET_DVR_DEVICEINFO_V40 deviceInfo);
    [DllImport(Dll)] public static extern bool NET_DVR_Logout(int userId);

    // ---- Remote config (search / set) ----
    [DllImport(Dll)] public static extern int NET_DVR_StartRemoteConfig(int userId, int command, IntPtr lpInBuff, int inBufLen, IntPtr callback, IntPtr userData);
    [DllImport(Dll)] public static extern int NET_DVR_GetNextRemoteConfig(int handle, IntPtr lpOutBuff, int outBufLen);
    [DllImport(Dll)] public static extern bool NET_DVR_StopRemoteConfig(int handle);

    // Typed overload for the ACS-event search (same native entry point).
    [DllImport(Dll)] public static extern int NET_DVR_GetNextRemoteConfig(int handle, ref NET_DVR_ACS_EVENT_CFG cfg, int outBufLen);

    // ---- Remote-config command ids (from HCNetSDK V6.1.9.4) ----
    public const int NET_DVR_GET_ACS_EVENT = 2514;

    // ---- GetNextRemoteConfig return codes (HCNetSDK V6.1.9.4) ----
    public const int NEXT_STATUS_SUCCESS = 1000;
    public const int NEXT_STATUS_NEED_WAIT = 1001;
    public const int NEXT_STATUS_FINISH = 1002;
    public const int NEXT_STATUS_FAILED = 1003;

    // ---- Field lengths (HCNetSDK V6.1.9.4) ----
    public const int ACS_CARD_NO_LEN = 32;
    public const int NAME_LEN = 32;
    public const int MACADDR_LEN = 6;
    public const int MAX_NAMELEN = 16;
    public const int NET_SDK_EMPLOYEE_NO_LEN = 32;
    public const int NET_SDK_MONITOR_ID_LEN = 64;

    // ---- SetSDKInitCfg types ----
    public const int NET_SDK_INIT_CFG_SDK_PATH = 2; // component-library folder (HCNetSDKCom)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_DVR_LOCAL_SDK_PATH
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string sPath;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] byRes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_DVR_USER_LOGIN_INFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string sDeviceAddress;
        public byte byUseTransport;
        public ushort wPort;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string sUserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string sPassword;
        public IntPtr cbLoginResult;
        public IntPtr pUser;
        public int bUseAsynLogin;
        public byte byProxyType;
        public byte byUseUTCTime;
        public byte byLoginMode;
        public byte byHttps;
        public int iProxyID;
        public byte byVerifyMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 119)] public byte[] byRes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_DEVICEINFO_V30
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] sSerialNumber;
        public byte byAlarmInPortNum;
        public byte byAlarmOutPortNum;
        public byte byDiskNum;
        public byte byDVRType;
        public byte byChanNum;
        public byte byStartChan;
        public byte byAudioChanNum;
        public byte byIPChanNum;
        public byte byZeroChanNum;
        public byte byMainProto;
        public byte bySubProto;
        public byte bySupport;
        public byte bySupport1;
        public byte bySupport2;
        public ushort wDevType;
        public byte bySupport3;
        public byte byMultiStreamProto;
        public byte byStartDChan;
        public byte byStartDTalkChan;
        public byte byHighDChanNum;
        public byte bySupport4;
        public byte byLanguageType;
        public byte byVoiceInChanNum;
        public byte byStartVoiceInChanNo;
        public byte bySupport5;
        public byte bySupport6;
        public byte byMirrorChanNum;
        public ushort wStartMirrorChanNo;
        public byte bySupport7;
        public byte byRes2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_DEVICEINFO_V40
    {
        public NET_DVR_DEVICEINFO_V30 struDeviceV30;
        public byte bySupportLock;
        public byte byRetryLoginTime;
        public byte byPasswordLevel;
        public byte byProxyType;
        public uint dwSurplusLockTime;
        public byte byCharEncodeType;
        public byte bySupportDev5;
        public byte bySocketProtocolType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 253)] public byte[] byRes2;
    }

    // ===== ACS event search structures (transcribed verbatim from HCNetSDK V6.1.9.4 CHCNetSDK.cs) =====

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_TIME
    {
        public int dwYear;
        public int dwMonth;
        public int dwDay;
        public int dwHour;
        public int dwMinute;
        public int dwSecond;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_ACS_EVENT_COND
    {
        public uint dwSize;
        public uint dwMajor;
        public uint dwMinor;
        public NET_DVR_TIME struStartTime;
        public NET_DVR_TIME struEndTime;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ACS_CARD_NO_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byCardNo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NAME_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byName;
        public uint dwBeginSerialNo;
        public byte byPicEnable;
        public byte byTimeType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2, ArraySubType = UnmanagedType.I1)] public byte[] byRes2;
        public uint dwEndSerialNo;
        public uint dwIOTChannelNo;
        public ushort wInductiveEventType;
        public byte bySearchType;
        public byte byRes1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NET_SDK_MONITOR_ID_LEN)] public string szMonitorID;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NET_SDK_EMPLOYEE_NO_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byEmployeeNo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 140, ArraySubType = UnmanagedType.I1)] public byte[] byRes;

        public void Init()
        {
            byCardNo = new byte[ACS_CARD_NO_LEN];
            byName = new byte[NAME_LEN];
            byRes2 = new byte[2];
            byEmployeeNo = new byte[NET_SDK_EMPLOYEE_NO_LEN];
            byRes = new byte[140];
            szMonitorID = string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_IPADDR
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string sIpV4;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128, ArraySubType = UnmanagedType.I1)] public byte[] byIPv6;
        public void Init() => byIPv6 = new byte[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_ACS_EVENT_DETAIL
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ACS_CARD_NO_LEN)] public byte[] byCardNo;
        public byte byCardType;
        public byte byWhiteListNo;
        public byte byReportChannel;
        public byte byCardReaderKind;
        public uint dwCardReaderNo;
        public uint dwDoorNo;
        public uint dwVerifyNo;
        public uint dwAlarmInNo;
        public uint dwAlarmOutNo;
        public uint dwCaseSensorNo;
        public uint dwRs485No;
        public uint dwMultiCardGroupNo;
        public ushort wAccessChannel;
        public byte byDeviceNo;
        public byte byDistractControlNo;
        public uint dwEmployeeNo;
        public ushort wLocalControllerID;
        public byte byInternetAccess;
        public byte byType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MACADDR_LEN)] public byte[] byMACAddr;
        public byte bySwipeCardType;
        public byte byRes2;
        public uint dwSerialNo;
        public byte byChannelControllerID;
        public byte byChannelControllerLampID;
        public byte byChannelControllerIRAdaptorID;
        public byte byChannelControllerIREmitterID;
        public uint dwRecordChannelNum;
        public IntPtr pRecordChannelData;
        public byte byUserType;
        public byte byCurrentVerifyMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public byte[] byRe2;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NET_SDK_EMPLOYEE_NO_LEN)] public byte[] byEmployeeNo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] byRes;

        public void Init()
        {
            byCardNo = new byte[ACS_CARD_NO_LEN];
            byMACAddr = new byte[MACADDR_LEN];
            byRe2 = new byte[2];
            byEmployeeNo = new byte[NET_SDK_EMPLOYEE_NO_LEN];
            byRes = new byte[64];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_ACS_EVENT_CFG
    {
        public uint dwSize;
        public uint dwMajor;
        public uint dwMinor;
        public NET_DVR_TIME struTime;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_NAMELEN)] public byte[] sNetUser;
        public NET_DVR_IPADDR struRemoteHostAddr;
        public NET_DVR_ACS_EVENT_DETAIL struAcsEventInfo;
        public uint dwPicDataLen;
        public IntPtr pPicData;
        public ushort wInductiveEventType;
        public byte byTimeType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 61)] public byte[] byRes;

        public void Init()
        {
            sNetUser = new byte[MAX_NAMELEN];
            struRemoteHostAddr.Init();
            struAcsEventInfo.Init();
            byRes = new byte[61];
        }
    }

    // ===== Card (user) + fingerprint structures (transcribed from HCNetSDK V6.1.9.4) =====
    // NOTE: fingerprints are keyed on CARD NUMBER. This deployment uses employeeNo AS the card no.

    public const int MAX_DOOR_NUM_256 = 256;
    public const int MAX_GROUP_NUM_128 = 128;
    public const int MAX_FINGER_PRINT_LEN = 768;
    public const int CARD_PASSWORD_LEN = 8;
    public const int ERROR_MSG_LEN = 32;

    public const int NET_DVR_GET_CARD = 2560;
    public const int NET_DVR_SET_CARD = 2561;
    public const int NET_DVR_DEL_CARD = 2562;
    public const int NET_DVR_GET_FINGERPRINT = 2563;
    public const int NET_DVR_SET_FINGERPRINT = 2564;
    public const int NET_DVR_DEL_FINGERPRINT = 2565;

    // SendWithRecvRemoteConfig status codes.
    public const int SEND_STATUS_SUCCESS = 1000;
    public const int SEND_STATUS_NEEDWAIT = 1001;
    public const int SEND_STATUS_FINISH = 1002;
    public const int SEND_STATUS_FAILED = 1003;
    public const int SEND_STATUS_EXCEPTION = 1004;

    // Typed GetNext overloads (same native entry point).
    [DllImport(Dll)] public static extern int NET_DVR_GetNextRemoteConfig(int handle, ref NET_DVR_CARD_RECORD cfg, int outBufLen);
    [DllImport(Dll)] public static extern int NET_DVR_GetNextRemoteConfig(int handle, ref NET_DVR_FINGERPRINT_RECORD cfg, int outBufLen);

    // Generic send (IntPtr in/out), used for SET_CARD / SET_FINGERPRINT.
    [DllImport(Dll)] public static extern int NET_DVR_SendWithRecvRemoteConfig(int handle, IntPtr lpInBuff, uint inBufLen, IntPtr lpOutBuff, uint outBufLen, ref uint outDataLen);

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_TIME_EX
    {
        public ushort wYear;
        public byte byMonth;
        public byte byDay;
        public byte byHour;
        public byte byMinute;
        public byte bySecond;
        public byte byRes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_VALID_PERIOD_CFG
    {
        public byte byEnable;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3, ArraySubType = UnmanagedType.I1)] public byte[] byRes1;
        public NET_DVR_TIME_EX struBeginTime;
        public NET_DVR_TIME_EX struEndTime;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32, ArraySubType = UnmanagedType.I1)] public byte[] byRes2;
        public void Init() { byRes1 = new byte[3]; byRes2 = new byte[32]; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_CARD_COND
    {
        public uint dwSize;
        public uint dwCardNum; // 0xffffffff = all
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64, ArraySubType = UnmanagedType.I1)] public byte[] byRes;
        public void Init() => byRes = new byte[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_CARD_RECORD
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ACS_CARD_NO_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byCardNo;
        public byte byCardType;
        public byte byLeaderCard;
        public byte byUserType;
        public byte byRes1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_DOOR_NUM_256, ArraySubType = UnmanagedType.I1)] public byte[] byDoorRight;
        public NET_DVR_VALID_PERIOD_CFG struValid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_GROUP_NUM_128, ArraySubType = UnmanagedType.I1)] public byte[] byBelongGroup;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = CARD_PASSWORD_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byCardPassword;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_DOOR_NUM_256, ArraySubType = UnmanagedType.I1)] public ushort[] wCardRightPlan;
        public uint dwMaxSwipeTimes;
        public uint dwSwipeTimes;
        public uint dwEmployeeNo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NAME_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byName;
        public uint dwCardRight;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256, ArraySubType = UnmanagedType.I1)] public byte[] byRes;

        public void Init()
        {
            byCardNo = new byte[ACS_CARD_NO_LEN];
            byDoorRight = new byte[MAX_DOOR_NUM_256];
            struValid.Init();
            byBelongGroup = new byte[MAX_GROUP_NUM_128];
            byCardPassword = new byte[CARD_PASSWORD_LEN];
            wCardRightPlan = new ushort[MAX_DOOR_NUM_256];
            byName = new byte[NAME_LEN];
            byRes = new byte[256];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_CARD_STATUS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ACS_CARD_NO_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byCardNo;
        public uint dwErrorCode;
        public byte byStatus; // 0-fail 1-success
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 23, ArraySubType = UnmanagedType.I1)] public byte[] byRes;
        public void Init() { byCardNo = new byte[ACS_CARD_NO_LEN]; byRes = new byte[23]; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_FINGERPRINT_COND
    {
        public uint dwSize;
        public uint dwFingerPrintNum; // 0xffffffff = all when getting
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ACS_CARD_NO_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byCardNo;
        public uint dwEnableReaderNo;
        public byte byFingerPrintID;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 131, ArraySubType = UnmanagedType.I1)] public byte[] byRes;
        public void Init() { byCardNo = new byte[ACS_CARD_NO_LEN]; byRes = new byte[131]; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_FINGERPRINT_RECORD
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ACS_CARD_NO_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byCardNo;
        public uint dwFingerPrintLen;
        public uint dwEnableReaderNo;
        public byte byFingerPrintID;
        public byte byFingerType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 30, ArraySubType = UnmanagedType.I1)] public byte[] byRes1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_FINGER_PRINT_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byFingerData;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 96, ArraySubType = UnmanagedType.I1)] public byte[] byRes;
        public void Init()
        {
            byCardNo = new byte[ACS_CARD_NO_LEN];
            byRes1 = new byte[30];
            byFingerData = new byte[MAX_FINGER_PRINT_LEN];
            byRes = new byte[96];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_FINGERPRINT_STATUS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ACS_CARD_NO_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byCardNo;
        public byte byCardReaderRecvStatus;
        public byte byFingerPrintID;
        public byte byFingerType;
        public byte byRecvStatus;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ERROR_MSG_LEN, ArraySubType = UnmanagedType.I1)] public byte[] byErrorMsg;
        public uint dwCardReaderNo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20, ArraySubType = UnmanagedType.I1)] public byte[] byRes;
        public void Init() { byCardNo = new byte[ACS_CARD_NO_LEN]; byErrorMsg = new byte[ERROR_MSG_LEN]; byRes = new byte[20]; }
    }
}
