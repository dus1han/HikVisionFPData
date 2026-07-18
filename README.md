# HikSync

Windows service that (1) collects attendance from Hikvision **DS-K1A8503MF-B** terminals into a
local Postgres and (2) syncs users + fingerprints from each pair's **IN** (enrollment master) to its
**OUT** terminal. The push of captured rows to the central (API) Postgres is intentionally deferred
(see `REQUIREMENTS.md` §7–8).

See `REQUIREMENTS.md` for the full spec and `DEVELOPMENT_PLAN.md` for the phased plan.

## Solution layout

| Project | Role |
|---|---|
| `HikSync.Core` | Domain models, options, interfaces, pure logic (idempotency key, sync planner). No dependencies. |
| `HikSync.Application` | Orchestration services: `AttendanceCollector`, `DeviceSyncService`, `PushService`. Host-agnostic, unit-tested. |
| `HikSync.Data` | Local Postgres: Npgsql + Dapper repositories, DbUp migrations, DPAPI secret protection. |
| `HikSync.Device` | HCNetSDK P/Invoke interop + `IAccessDevice`; plus an in-memory `FakeAccessDevice` for dev/test. |
| `HikSync.Push` | `IAttendancePusher` — currently a no-op stub (central push deferred). |
| `HikSync.Service` | Windows-service host: DI, config, Serilog, migrations-on-startup, three periodic workers. |
| `HikSync.UnitTests` | Tests for dedup/cursor (`AttendanceCollector`), `SyncPlanner`, `AttendanceIdentity`. |

## Status

- ✅ Builds clean; 12 unit tests pass (`dotnet test`).
- ✅ End-to-end runnable against a local Postgres using the **fake device** (`Sdk:UseFakeDevice=true`)
  — no hardware needed to see capture/sync flow and dedup working.
- ✅ **Attendance capture wired against the real SDK** (HCNetSDK V6.1.9.4): `ReadEventsAsync` drives
  `NET_DVR_GET_ACS_EVENT` via `StartRemoteConfig` → `GetNextRemoteConfig` with the exact
  `NET_DVR_ACS_EVENT_COND`/`_CFG` structs.
- ✅ **User + fingerprint sync wired against the real SDK:** `ReadUsersAsync` (`NET_DVR_GET_CARD` all),
  `ReadFingerprintsAsync` (`NET_DVR_GET_FINGERPRINT` per card), `UpsertUserAsync` (`NET_DVR_SET_CARD`),
  `UpsertFingerprintAsync` (`NET_DVR_SET_FINGERPRINT` via `SendWithRecvRemoteConfig`). **Convention:
  employeeNo IS the card number** — enrollment on the IN terminal must set card no = employee no.
- ⏳ **Not runtime-verified:** all SDK calls compile and follow Hikvision's own demo patterns/structs,
  but have NOT been executed against a live terminal. Validate on hardware before production.
- ⚠️ **Known limits:** card-reader number is hardcoded to 1 (`DefaultReaderNo`) — standalone terminal
  assumption; sync **delete** is not implemented (off by default via `Sync:DeleteRemovedUsers`);
  SET success is judged by the config `FINISH` status (per-record device rejection codes not yet inspected).
- ✅ **Remote-API push implemented** (`HttpAttendancePusher`): POSTs not-yet-uploaded rows (each with
  device IP, IN/OUT direction, idempotency key) to the API you provide. Failure handling per
  REQUIREMENTS §7.3 — 2xx = accepted, 4xx = rejected→dead-letter, 5xx/timeout = retried (local buffer
  absorbs the outage). **Off by default** — set `Push:Enabled=true` + `Push:Endpoint` (an IP:port URL
  is fine) to activate; otherwise the no-op stub keeps rows local. **You set up only the local DB —
  the API/central storage is yours.**

## Prerequisites

- .NET 8 SDK (repo builds on .NET 10 SDK too; libraries target `net8.0`, tests `net10.0`).
- A local PostgreSQL instance.
- For real devices: the **x64** HCNetSDK DLL set in `src/HikSync.Service/native/` (see its README),
  and network access to the terminals (SDK port, default 8000).

## Local database setup

The service **auto-creates and migrates** the local schema on startup (DbUp), so normally you only
point it at a Postgres and run it. Scripts are provided for manual setup/review:

- `scripts/local_db_setup.sql` — full schema (all tables + indexes), plus commented DB/role/grant
  statements. Safe to run manually first; the startup migration then no-ops.
- `scripts/seed_device_pairs_example.sql` — insert your device pairs (location, IN/OUT IPs, creds).

Device passwords are stored as **plain text** in `device_pairs.in_password` / `out_password`. Use `''`
when using `Sdk:UseFakeDevice` or passwordless terminals.

## Run locally (fake device + local Postgres)

```powershell
# 1. Point LocalDatabase:ConnectionString at your Postgres in appsettings.json
# 2. Development profile already sets Sdk:UseFakeDevice=true
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src/HikSync.Service
```

The service migrates the local DB on startup, then the attendance/sync workers run on their
intervals. With the fake device the sync worker copies seeded users/fingerprints IN→OUT; capture
stores rows to `attendance_events`. To push, set `Push:Enabled=true` + `Push:Endpoint` to your API.

## Verify a device (HikSync.DeviceCheck)

A small console tool that connects to a terminal with the **same interop the service uses** and
prints what it reads — device info, attendance events, user/card records, and fingerprints — each
labelled with the local-DB column it maps to. Use it to confirm a device matches the service before
running the full service.

```powershell
dotnet publish src/HikSync.DeviceCheck -c Release -r win-x64 -o C:\HikCheck
# copy the HCNetSDK x64 DLLs into C:\HikCheck\native\  (or build already copied them)
C:\HikCheck\HikSync.DeviceCheck.exe --ip 192.168.1.10 --user admin --pass secret --minutes 240
```

- Read-only by default; `--write-test` additionally upserts a test user and reads it back.
- `--fake` runs the checks against the in-memory device (self-test, no hardware).
- Exit code 0 = all checks passed, 1 = a check failed (SDK error codes are printed).
- Run `--help` for all options.

## Test

```powershell
dotnet test
```

## Install as a Windows service

```powershell
dotnet publish src/HikSync.Service -c Release -r win-x64 -o C:\HikSync
# copy your HCNetSDK x64 DLLs into C:\HikSync\native\
./scripts/install-service.ps1 -BinPath C:\HikSync\HikSync.Service.exe
```

## Key design points (enforced in code)

- **Only-new capture:** identity is the reset-immune composite `(device_ip, employee_no, event_time,
  major, minor)` surfaced as `idempotency_key`; inserts use `ON CONFLICT DO NOTHING`. The serial
  number is only the ordering cursor. Verified by `AttendanceCollectorTests` (incl. serial-reset).
- **Clock-in vs clock-out** = which terminal (IN/OUT) produced the event, not a device status field.
- **Any successful verification counts** as a punch by default (`Attendance:CountedVerifyModes`).
- **Sync is a two-way union** (`Sync:Bidirectional`, default on): each device in a couple receives the
  users/fingerprints the other has and it lacks, so both hold the complete set. Additive only — never
  overwrites a differing record (which would make two devices ping-pong) and never deletes. Only users
  with a fingerprint are synced (`Sync:OnlyUsersWithFingerprints`). Set `Bidirectional=false` for the
  legacy one-way IN→OUT master/slave behaviour.
- **Audit log** (`operation_log` table): every device transaction connect→operation→disconnect is
  recorded with device IP, IN/OUT role, operation, status, message and duration. Writes are
  best-effort (never break the flow).
- **Log retention:** a nightly job deletes rows older than `Log:RetentionDays` (default 30), running
  once per day after `Log:CleanupHourLocal` (default 02:00 local) — `DELETE ... WHERE logged_at < now() - 30 days`.
