# HCNetSDK native libraries (x64)

Drop the **x64** HCNetSDK DLL set for the DS-K1A8503MF-B into this folder. Everything here is
copied next to the service executable at build/publish time and the service points HCNetSDK at it
via `NET_DVR_SetSDKInitCfg`.

Typical contents (names vary by SDK release):

```
HCNetSDK.dll
HCCore.dll
PlayCtrl.dll
HCNetSDKCom\        (plugin folder — HCCoreDevCfg.dll, SystemTransform.dll, AudioRender.dll, ...)
libcrypto-1_1-x64.dll / libssl-1_1-x64.dll  (or the OpenSSL DLLs shipped with your SDK)
```

Rules:
- **Bit-width must match the process.** The service builds x64 (`PlatformTarget=x64`); use x64 DLLs.
- Keep the `HCNetSDKCom\` plugin folder intact — missing plugins cause login/config calls to fail
  with obscure error codes.
- These binaries are **not** committed to source control; obtain them from your Hikvision SDK package.
- The service adds this `native\` folder to the DLL search path at startup, so the DLLs may stay in
  this subfolder (no need to place them next to the exe).

**On the target machine** (required for HCNetSDK to load):
- Install the **Microsoft Visual C++ 2015–2022 Redistributable (x64)** (`vc_redist.x64.exe`).
  Without it you get: *"Unable to load DLL 'HCNetSDK.dll' or one of its dependencies: the specified
  module could not be found."* even when all DLLs are present.
