# HikSync — session handoff (2026-08-04)

State for a Claude Code session running **on the server** (the box that can reach the terminals on
192.168.1.x and the HikSync Postgres). The prior work happened on a dev box that cannot reach the
devices, which is why everything went through copy-paste.

## What works
- **Attendance capture** (ISAPI AcsEvent) → local Postgres → **push to HRIS API** (`insertB`). Solid.
- **User/card sync** over ISAPI. `--write-test` persists. Fine.
- Diagnostic tables: `sync_failure`, `device_enrollment` (per-device roster). Auto-migrated (0003/0004).

## The open problem: fingerprint sync
Fingerprint **templates won't transfer over ISAPI** on the DS-K1A8503MF-B (V1.4.1) — proven
exhaustively (`--fp-selftest`): correct structure, every scalar field validated, `fingerData`
required inline, inline rejected as `badParameters`.

**SDK is the path** (as iVMS uses). SDK login WORKS (`--transport sdk --port 8000`). The write is
routed **out of process** (service spawns `HikSync.Service.exe fp-sdk-apply <job>`) so a native crash
can't kill the service — that isolation works; the service is now stable.

**But the SDK write does not persist.** `--fp-sdk-writeback <emp>` (writes the employee's template to
a FREE finger slot, then re-reads to confirm it appears) shows **NOT PERSISTED**. The device's
per-record status now surfaces: `NET_DVR_SET_FINGERPRINT` → `recvStatus=5` (1 = stored), empty
`byErrorMsg`. So the device silently rejects the downloaded template with code 5.

### Where to dig (SDK P/Invoke in `src/HikSync.Device/Hikvision/`)
- `SetFingerprintBlocking` (HikvisionAccessDevice.cs) builds `NET_DVR_FINGERPRINT_RECORD`:
  `byCardNo = employeeNo`, `byFingerType = 0`, `byFingerPrintID = slot`, `byFingerData`, len.
  **Suspects for recvStatus=5:** `byFingerType=0` may be wrong (try 1); the person may need a card
  association the SDK sees; card-less users (SDK `GET_CARD` returns 0 — these are card-less); reader
  number; or these devices only accept on-scanner enrollment via `CaptureFingerPrint`, not download.
- Read path (`ReadCardFingerprintsBlocking`) works and round-trips via `GetNextRemoteConfig`.
- `--fp-sdk-writeback <emp>` is the fast persistence test (writes to a free slot, verifies it appears).
  Run it against a device with `--transport sdk --port 8000` on an employee that has a **normalFP**
  (dismissingFP is skipped on read).

### Config
`Sync:FingerprintTransport` = `sdk` routes fp writes over the SDK (default `isapi`). `Sync:SdkPort`
= 8000. `Sync:SyncFingerprints=false` disables fp entirely. Deploy copies must use
`robocopy <src> <dst> /E /XF appsettings.json` so the config isn't clobbered.

## Environment gotchas
- Two Postgres exist; the service's `ConnectionString` must point at the one holding `device_pairs`
  (a wrong/empty `localhost/hiksync` cost hours — the service ran but saw 0 pairs). Verify with the
  connection string from `appsettings.json`, not pgAdmin's default.
- Service runs as **LocalSystem**; native SDK crashes there (hence out-of-process). Consider running
  under the user account DeviceCheck worked under.
- Devices: `.219/.220` (Printing), `.221/.222` (Forming). `.220` pass `123456bio`, `.219` `Asd@1234`.
- `.220` employee 56 has a `dismissingFP` (special, skipped on read).

## Immediate next step
Figure out `recvStatus=5`. Iterate on `SetFingerprintBlocking`: try `byFingerType=1`; inspect the
full status struct; compare our record bytes to what iVMS sends (a Wireshark capture of iVMS pushing
a fingerprint to one of these devices would be definitive). `--fp-sdk-writeback` gives a 30-second
persist/reject signal per attempt.
