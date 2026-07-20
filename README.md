# HikSync

Windows service that (1) collects attendance from Hikvision **DS-K1A8503MF-B** terminals into a
local Postgres, (2) pushes captured rows to your central API, and (3) keeps each pair of terminals
holding the same users + fingerprints via a two-way union sync.

**To deploy, follow `DEPLOYMENT.md`** — it is the authoritative step-by-step runbook (DB setup,
device pairs, config, install, verify, troubleshoot). This README covers the codebase and local
development. See `REQUIREMENTS.md` for the full spec and `DEVELOPMENT_PLAN.md` for the phased plan.

> **These terminals speak ISAPI, not the private protocol.** The HCNetSDK/Private transport is
> rejected by the tested devices (`login failed: error 1`), so `Sdk:Transport` defaults to `isapi`.
> In ISAPI mode **no native DLLs and no Visual C++ redistributable are needed** — the `native/`
> folder is unused. The SDK path is kept in the codebase but is not the deployed configuration.

## Solution layout

| Project | Role |
|---|---|
| `HikSync.Core` | Domain models, options, interfaces, pure logic (idempotency key, sync planner). No dependencies. |
| `HikSync.Application` | Orchestration services: `AttendanceCollector`, `DeviceSyncService`, `PushService`, `LogRetentionService`. Host-agnostic, unit-tested. |
| `HikSync.Data` | Local Postgres: Npgsql + Dapper repositories, DbUp migrations, DPAPI secret protection. |
| `HikSync.Device` | Two `IAccessDevice` implementations — `IsapiAccessDevice` (HTTP/REST, **the deployed one**) and `HikvisionAccessDevice` (HCNetSDK P/Invoke) — plus an in-memory `FakeAccessDevice` for dev/test. |
| `HikSync.Push` | `IAttendancePusher`: `HttpAttendancePusher` (real) and `StubAttendancePusher` (no-op, used when `Push:Enabled=false`). |
| `HikSync.Service` | Windows-service host: DI, config, Serilog, migrations-on-startup, four periodic workers (attendance, sync, push, log retention). |
| `HikSync.DeviceCheck` | Standalone diagnostics/maintenance CLI. No DB, no config file. |
| `HikSync.UnitTests` | Tests for dedup/cursor (`AttendanceCollector`), `SyncPlanner`, `AttendanceIdentity`. |

## Status

- ✅ Builds clean; **17 unit tests pass** (`dotnet test`).
- ✅ **Validated on real hardware over ISAPI:** device login, attendance read, user read/write,
  fingerprint read/write, two-way sync, delete.
- ✅ End-to-end runnable against a local Postgres using the **fake device**
  (`Sdk:UseFakeDevice=true`) — no hardware needed to see capture/sync/dedup working.
- ✅ **Remote-API push implemented** (`HttpAttendancePusher`): POSTs not-yet-uploaded rows to the API
  you provide. Failure handling per REQUIREMENTS §7.3 — 2xx = accepted, 4xx = rejected→dead-letter,
  5xx/timeout = retried (the local buffer absorbs the outage). **Off by default** — set
  `Push:Enabled=true` plus `Push:Endpoint` and `Push:CompanyId` to activate; otherwise the no-op stub
  keeps rows local. **You set up only the local DB — the API/central storage is yours.**
- ⏳ **SDK transport not runtime-verified.** The HCNetSDK calls compile and follow Hikvision's demo
  patterns/structs, but the tested terminals reject that protocol, so they have never run against
  live hardware. Only the ISAPI path is proven.
- ⚠️ **Known limits:** card-reader number is hardcoded to 1 (`DefaultReaderNo`) — standalone-terminal
  assumption. User delete is implemented for ISAPI only (`HikvisionAccessDevice.DeleteUserAsync`
  throws) and is off by default (`Sync:DeleteRemovedUsers=false`, ignored entirely in bidirectional
  mode). `attendance_events` has no retention job — only `operation_log` is pruned.

## Prerequisites

- .NET 8 SDK (repo builds on .NET 10 SDK too; libraries target `net8.0`, tests `net10.0`).
- A local PostgreSQL instance.
- Network access to the terminals: **port 80 (ISAPI/HTTP)** in the default configuration.
- Only if you switch to `Sdk:Transport=sdk`: the **x64** HCNetSDK DLL set in
  `src/HikSync.Service/native/` (see its README) and the SDK port (default 8000).

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

The service migrates the local DB on startup, then the workers run on their intervals. With the fake
device the sync worker unions seeded users/fingerprints between the pair; capture stores rows to
`attendance_events`. To push, set `Push:Enabled=true` + `Push:Endpoint` to your API.

## Test

```powershell
dotnet test
```

## Verify a device (HikSync.DeviceCheck)

A console tool that connects to a terminal with the **same code the service uses** and prints what it
reads — device info, attendance events, user/card records, and fingerprints — each labelled with the
local-DB column it maps to. Use it to confirm a device before running the full service. No database
and no config file: everything comes from the command line.

```powershell
dotnet publish src/HikSync.DeviceCheck -c Release -r win-x64 --self-contained true `
  -o C:\Users\Saboor.a\Desktop\Personal\HikSync-DeviceCheck

cd C:\Users\Saboor.a\Desktop\Personal\HikSync-DeviceCheck
.\HikSync.DeviceCheck.exe --ip 192.168.1.220 --user admin --pass secret --minutes 240
```

- Defaults to ISAPI. Read-only unless you pass a writing flag.
- `--write-test` upserts a test user and reads it back (**writes to the device**).
- `--delete <emp[,emp]>` / `--delete all` / `--delete-others <emp>` — **destructive**, deletes users.
- `--sync-to <ip>` runs a one-off two-way union sync against a second device.
- `--probe <emp>` dumps raw ISAPI responses for one employee (diagnostics).
- `--fake` runs the checks against the in-memory device (self-test, no hardware).
- Exit code 0 = all checks passed, 1 = a check failed (error codes are printed) — scriptable across
  many devices.
- Run `--help` for the full option list.

## Install as a Windows service

Full instructions, including config, are in **`DEPLOYMENT.md`**. In brief:

```powershell
dotnet publish src/HikSync.Service -c Release -r win-x64 --self-contained true `
  -o C:\Users\Saboor.a\Desktop\Personal\HikSync

# edit appsettings.json first — see DEPLOYMENT.md §3
./scripts/install-service.ps1 -BinPath C:\Users\Saboor.a\Desktop\Personal\HikSync\HikSync.Service.exe
```

Publishing is self-contained, so **no .NET install is required on the host**. Note that republishing
overwrites `appsettings.json` with the placeholder template — back up your configured copy first.

## Key design points (enforced in code)

- **Only-new capture:** identity is the reset-immune composite `(device_ip, employee_no, event_time,
  major, minor)` surfaced as `idempotency_key`; inserts use `ON CONFLICT DO NOTHING`. The device
  serial number is only the ordering cursor, never identity, because it resets on a device wipe.
  Verified by `AttendanceCollectorTests` (incl. serial-reset).
- **Nothing is ever deleted from the device.** Attendance reads are strictly read-only; a local
  per-device watermark (`fetch_watermark`) tracks how far we've read, and it only advances after a
  window completes without error. Device-side event storage is finite and overwrites oldest-first, so
  an outage longer than the device's buffer depth loses events — keep the service running.
- **Clock-in vs clock-out** = which terminal (IN/OUT) produced the event, not a device status field.
- **Any successful verification counts** as a punch by default (`Attendance:CountedVerifyModes`).
- **Sync is a two-way union** (`Sync:Bidirectional`, default on): each device in a couple receives the
  users/fingerprints the other has and it lacks, so both hold the complete set — you can enroll on
  either terminal. Additive only — never overwrites a differing record (which would make two devices
  ping-pong) and never deletes. Only users with a fingerprint are synced
  (`Sync:OnlyUsersWithFingerprints`). Set `Bidirectional=false` for the legacy one-way IN→OUT
  master/slave behaviour.
- **employeeNo IS the card number** — enrollment must set card no = employee no.
- **Audit log** (`operation_log` table): every device transaction connect→operation→disconnect is
  recorded with device IP, IN/OUT role, operation, status, message and duration. Writes are
  best-effort (never break the flow).
- **Log retention:** a nightly job deletes `operation_log` rows older than `Log:RetentionDays`
  (default 30), running once per day after `Log:CleanupHourLocal` (default 02:00 local).
