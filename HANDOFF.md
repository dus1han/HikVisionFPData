# HikSync — session handoff (2026-08-04)

Fingerprint sync **works**. This note records what was wrong, how it was proven on the hardware, and
what is left to decide.

## Status

| Function | State |
| --- | --- |
| Attendance capture → local Postgres → push to HRIS (`insertB`) | working |
| User sync over ISAPI | working |
| **Fingerprint sync between paired terminals** | **working** — Printing pair converged, 54 users / 54 fingerprints on both `.219` and `.220` |
| Forming pair `.221` / `.222` | **offline** — no ping, no ISAPI on :80, no SDK on :8000. Nothing to do with the code. |

## What was actually wrong

Three separate faults stacked on top of each other, and each one hid the next.

**1. Wrong ISAPI endpoint.** The write used `POST /ISAPI/AccessControl/FingerPrintDownload` — the
endpoint the older ISAPI guides document. It exists on DS-K1A8503MF-B V1.4.1 and validates each field
individually (send a bad `enableCardReader` and it answers `illegalCardReaderNo`, a bad
`fingerPrintID` and it answers `errorFingerID`), but once every field is valid it rejects the request
with a bare `badParameters` — regardless of payload, encoding, length, target slot, or employee. It
is simply not wired up on this firmware.

The endpoint that works is **`POST /ISAPI/AccessControl/FingerPrint/SetUp`**, which takes the byte-for-byte
identical `FingerPrintCfg` body and applies it.

**2. The device's verdict was never read.** `FingerPrint/SetUp` answers **HTTP 200 whether or not it
stored anything**. The real outcome is in `FingerPrintStatus.StatusList[].cardReaderRecvStatus`
(1 = stored). Nothing checked it, so rejected templates were reported as synced.

**3. `cardReaderRecvStatus = 5` was misread as a malformed request.** It means *"this finger is
already enrolled on this device"*, and `errorMsg` carries **the employee number that already owns
it**. This is the finding that unblocked everything: every earlier diagnostic
(`--fp-selftest`, `--fp-sdk-writeback`) wrote a person's own template back to a free slot, which is by
definition a duplicate — so a working write path was being tested with input the device is *supposed*
to refuse, and the resulting "5" was read as proof the path was broken. Proof it is a duplicate check:
push four different people's templates to one throwaway person and each rejection names a different
owner.

```
template of employee 692  -> recvStatus=5, errorMsg='692'
template of employee 244  -> recvStatus=5, errorMsg='244'
template of employee 632  -> recvStatus=5, errorMsg='632'
template of employee 479  -> recvStatus=5, errorMsg='479'
```

The SDK path (`NET_DVR_SET_FINGERPRINT`) was never broken either — same status code, same meaning. It
did have a real bug: `byFingerType` was sent as `0`, and the device filed those templates as
`dismissingFP`. They stored fine; the ISAPI reader then skipped them as a non-attendance type, so
`--fp-sdk-writeback` re-read and concluded "NOT PERSISTED". That is where the 49 mistyped records on
`.219` came from.

## Two device behaviours the sync now has to respect

- **Deduplication is biometric, not byte-wise.** The same finger enrolled on two terminals produces
  two different 512-byte templates, and the device still recognises and refuses the copy. Confirmed:
  `.219`'s employee-56 template, pushed to `.220` under a different employee, came back
  `recvStatus=5 errorMsg='56'`. Diffing enrolment slot-by-slot therefore never converges — the plan
  keeps proposing a copy the device keeps declining. `SyncPlanner.BuildMissingOnly` now compares
  coverage **per person**.
- **A record's `fingerType` is fixed when it is created** and ignored on update. Re-applying a
  template with `fingerType: normalFP` over a `dismissingFP` record leaves it `dismissingFP`. The only
  way to correct one is to delete the person and write them again.

## Changes

- `IsapiAccessDevice.UpsertFingerprintAsync` → `FingerPrint/SetUp`, and it now parses the device
  verdict and throws with the decoded reason (`IsapiFingerprintStatus`). Falls back to polling
  `/AccessControl/FingerPrintProgress` when the apply answers asynchronously.
- The ISAPI reader returns **every** enrolled finger, not just `normalFP`. It used to hide special
  types, so the sync believed those people had no fingerprint and re-pushed them every cycle forever.
- `SyncPlanner.BuildMissingOnly`: copies only attendance fingers, but counts **every** enrolled finger
  as coverage, per person.
- `HikvisionAccessDevice`: `byFingerType` 1 (not 0); `recvStatus` decoded correctly; added
  `TrySetFingerprintAsync` which returns the verdict instead of throwing.
- `Sync:FingerprintTransport` → **`isapi`** in the deployed config. With this the service makes no
  native SDK calls at all, so the LocalSystem native-crash problem is gone.
- Tests: 26 pass (was 17). New coverage for the status parsing and for both planner rules.

## Open decision: the 49 `dismissingFP` records on `.219`

They are **working** — those people punch on `.219` and the events arrive as ordinary fingerprint
verifications (major 5 / minor 38, 130 events in the last two weeks). The sync now recognises them as
coverage and leaves them alone, so nothing is broken and nothing loops.

They are still the wrong type. To normalise them:

```
HikSync.DeviceCheck --ip 192.168.1.219 --pass <pw> --transport isapi \
    --fp-repair --from 192.168.1.220 --from-pass <pw>          # dry run
    ... --apply                                                 # execute
```

It deletes and recreates each person from the partner device, because that is the only way to change a
record's type. Employees 692 and 244 were already put through it and came back `normalFP`. Weigh a
brief window where a person is absent from the terminal against a cosmetic fix — they work as they are.

## Diagnostics added to `HikSync.DeviceCheck`

| Flag | What it does |
| --- | --- |
| `--compare <ip>` | read-only: what a two-way sync would transfer between two devices |
| `--fp-inventory` | every fingerprint record incl. types the sync reader filters |
| `--push-fp <emp> --from <ip>` | copy one person's fingerprint, print the raw verdict, verify persistence |
| `--fp-dup-test [n]` | prove the device refuses duplicates and names the owner |
| `--fp-repair --from <ip> [--apply]` | rebuild enrolments stored under a non-attendance type |
| `--isapi <path> [--method M] [--body JSON]` | raw authenticated ISAPI call |
| `--sync-to <ip> [--only emp,emp]` | union sync, optionally restricted to named employees |

Build the tool with `dotnet build`; it targets net8.0, and this server has only .NET 10, so run the
dev build with `DOTNET_ROLL_FORWARD=LatestMajor` or use a self-contained publish.

## Environment notes

- Service is deployed at `C:\Users\User\Desktop\HikSync\HikSync\` (self-contained) and is registered
  as `HikSync`, **start it from an elevated shell** — `Start-Service HikSync`. It was found stopped
  with its install directory missing, which is why nothing had run recently.
- Deploy updates with `robocopy <src> <dst> /E /XF appsettings.json` so the config is not clobbered.
- `ConnectionString` must point at the Postgres holding `device_pairs` —
  `hris_biopack_hanwalla_devices`, not the default `hiksync`.
- Device passwords live in `device_pairs`: `.219` `Asd@1234`, `.220`/`.221`/`.222` `123456bio`.
- ISAPI search sessions are keyed on `searchID`: reuse the same value for the same query and it
  returns an empty continuation. The shipping reader uses the constant `"hiksync"`, one query per
  employee. Ad-hoc scripts that vary it per call will silently read zero fingerprints.
- `.220` still carries a leftover test user `999001` (no fingerprint) from an old `--write-test`.
