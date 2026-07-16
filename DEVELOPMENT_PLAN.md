# Hikvision Attendance & Sync Service — Tech Stack & Development Plan

**Companion to:** `REQUIREMENTS.md`
**Date:** 2026-07-16
**Data topology:** the service writes to a **local edge Postgres** (capture + offline buffer next to the devices) and then **pushes rows into the central, API-owned Postgres** (DB→DB, transactional — not HTTP). Two `Npgsql` connections.

**Sequencing principle:** local capture first, sync second, hardening third, **central-DB push last** (it's a separable concern — attendance accumulates locally as `upload_status='pending'` and the pusher is bolted on at the end without touching storage or dedup).

---

## Part 1 — Technology Stack

### 1.1 Platform & language
| Concern | Choice | Rationale |
|---|---|---|
| Runtime | **.NET 8 (LTS)** | Long-term support, first-class Windows Service hosting. |
| Language | **C# 12** | Matches .NET 8. |
| Service host | **`Microsoft.Extensions.Hosting` + `Microsoft.Extensions.Hosting.WindowsServices`** | `BackgroundService` workers, DI, config, graceful shutdown, native `sc.exe` install. |
| Process architecture | **x64 (pinned)** | Must match the HCNetSDK DLL bit-width. Build target locked to `win-x64`; **`PlatformTarget=x64`, `Prefer32Bit=false`.** |

### 1.2 Key libraries (NuGet)
| Area | Package | Notes |
|---|---|---|
| PostgreSQL driver | **`Npgsql`** (8.x) | Native ADO.NET provider. **Two** configured connections: local edge DB and central API DB. |
| Data access | **`Dapper`** (2.x) | Thin micro-ORM. Chosen over EF Core: we need explicit SQL for `INSERT ... ON CONFLICT DO NOTHING` (dedup, both local capture and central push) and bulk upserts; no need for change-tracking/LINQ. |
| Schema migrations | **`DbUp`** (5.x) | Versioned, idempotent SQL scripts run on startup. Pairs naturally with Dapper. |
| Logging | **`Serilog`** + `Serilog.Extensions.Hosting`, sinks: `Serilog.Sinks.File`, `Serilog.Sinks.Console`, (optional) `Serilog.Sinks.PostgreSQL` | Structured logs, rolling files, per-device context via enrichers. |
| Resilience | **`Polly`** (v8) | Retry + backoff + circuit-breaker for device reconnect and the central-DB push (wrap the push in a resilience pipeline). |
| Secret protection | **`Microsoft.AspNetCore.DataProtection`** (or Windows DPAPI `ProtectedData`) | Encrypt device passwords at rest (`device_pairs.password_enc`); keys scoped to the service account. |
| Scheduling | **`System.Threading.PeriodicTimer`** (built-in) | One timer per job (Attendance / Sync / Upload). Add **`Quartz.NET`** later only if cron-style windows are needed. |
| Options/validation | `Microsoft.Extensions.Options` + `Options.DataAnnotations` | Strongly-typed, validated config. |

### 1.3 Device SDK interop
- **HCNetSDK** native DLLs (x64 set): `HCNetSDK.dll`, `HCCore.dll`, `PlayCtrl.dll`, the `HCNetSDKCom\` plugin folder, and shipped dependency DLLs — deployed alongside the executable and pointed at via `NET_DVR_SetSDKInitCfg`.
- **Interop layer:** hand-written `[DllImport]` P/Invoke signatures + `[StructLayout(LayoutKind.Sequential)]` structs, isolated in `HikSync.Device` so the rest of the code never sees raw SDK types. Bind struct definitions against the **exact SDK build version** shipped (field offsets drift between releases).
- No third-party HCNetSDK wrapper is assumed; if a vetted C# wrapper for the deployed SDK version exists, it can replace the hand-rolled interop without affecting callers.

### 1.4 Testing & quality
| Area | Choice |
|---|---|
| Unit tests | **xUnit** + **FluentAssertions** + **NSubstitute** (mocks) |
| Integration (DB) | **Testcontainers for .NET** (throwaway Postgres) — exercises real `ON CONFLICT` dedup and migrations |
| Device testing | An `IAccessDevice` abstraction with a **fake/in-memory device** for logic tests; a thin manual harness against a real terminal for interop verification |
| Static analysis | Nullable reference types on, `.editorconfig`, analyzers, `TreatWarningsAsErrors` in CI |

### 1.5 Build, packaging, ops
| Concern | Choice |
|---|---|
| Build | `dotnet publish -c Release -r win-x64` (framework-dependent or self-contained) |
| Install | PowerShell script using `sc.exe create` / `New-Service` + SC recovery flags (auto-restart). **WiX MSI** optional later. |
| Config | `appsettings.json` + `appsettings.{Environment}.json` + environment variables; **User Secrets** in dev only |
| Health | Heartbeat file + optional minimal Kestrel `/health` endpoint (last successful attendance/sync/upload timestamps) |
| CI (optional) | GitHub Actions / Azure DevOps: build, unit + Testcontainers integration, publish artifact |

### 1.6 Solution structure
```
HikSync.sln
├── src/
│   ├── HikSync.Service        // host: Program.cs, DI wiring, worker registration, appsettings
│   ├── HikSync.Core           // domain models, interfaces (IAccessDevice, IAttendanceUploader), options
│   ├── HikSync.Device         // HCNetSDK P/Invoke interop + IAccessDevice implementation + connection cache
│   ├── HikSync.Data           // Npgsql + Dapper repos (LOCAL edge DB), DbUp migration scripts (embedded)
│   └── HikSync.Push           // IAttendancePusher + Npgsql transactional writer to CENTRAL DB (built LAST)
└── tests/
    ├── HikSync.UnitTests
    └── HikSync.IntegrationTests   // Testcontainers Postgres
```
Dependency direction: `Service → {Device, Data, Push} → Core`. `Core` depends on nothing. Interop is quarantined in `Device`; the pusher in `Push` is a leaf that can stay a stub until the final phase.

---

## Part 2 — Development Plan

Six phases. Each lists **goal → tasks → exit criteria (demoable) → risks**. The API is Phase 5.

### Phase 0 — Foundations & scaffolding
**Goal:** an installable, running, logging service wired to Postgres — no device logic yet.
- Create solution + projects (§1.6), enable nullable + analyzers.
- Host as Windows Service; DI, `appsettings`, strongly-typed validated options.
- Serilog rolling-file + console logging.
- Postgres schema via DbUp: `device_pairs`, `attendance_events`, `fetch_watermark`, `sync_state`, `sync_user_map` (per `REQUIREMENTS.md` §4).
- Config providers + DataProtection for encrypting `password_enc`.
- `PairConfigProvider` reads enabled pairs from DB.
- Install/uninstall PowerShell script with SC recovery.
- **Exit criteria:** `install.ps1` registers the service; it starts, connects to Postgres, applies migrations, reads the pair list, and writes a heartbeat + startup log; clean stop calls shutdown hooks.
- **Risks:** low. Service-account permissions for DB + DataProtection key ring.

### Phase 1 — HCNetSDK bring-up (highest-risk; do a spike first)
**Goal:** reliably log into a real terminal and read its identity.
- `HikSync.Device`: P/Invoke signatures + structs for `NET_DVR_Init/Cleanup`, `NET_DVR_SetSDKInitCfg`, `NET_DVR_Login_V40`, `NET_DVR_GetLastError`, `NET_DVR_GetDVRConfig` (device info).
- Deploy x64 DLL set next to the exe; confirm plugin folder resolves.
- `IAccessDevice` abstraction + connection/login-handle cache with re-login on failure; SDK error-code → message decoder.
- **Exit criteria:** service logs into both IN and OUT terminals of a pair and logs model + firmware; wrong-password / offline paths produce decoded, actionable errors; handles released cleanly on shutdown.
- **Risks:** **the critical path.** Bit-width mismatch (`BadImageFormatException`), missing plugin DLLs, struct-offset mismatches vs the SDK build. Mitigate with an early throwaway spike against a real device before committing the interface.

### Phase 2 — Attendance collection (the "only-new" engine)
**Goal:** capture punches from both terminals, store only previously-unseen ones.
- Implement ACS event search: `NET_DVR_StartRemoteConfig(NET_DVR_GET_ACS_EVENT)` → `NET_DVR_GetNextRemoteConfig` loop → `NET_DVR_StopRemoteConfig`.
- Decode events → domain model; apply `attendance.countedVerifyModes` filter (default: any successful verification).
- Reset-immune identity + dedup: `INSERT ... ON CONFLICT (device_ip, employee_no, event_time, major, minor) DO NOTHING`.
- Serial-number **cursor** + `fetch_watermark` (inclusive window start, advance-only-on-success) per `REQUIREMENTS.md` §5.3.
- Role tagging IN/OUT from the pair; time normalization to UTC (§9.4).
- `AttendanceJob` on a `PeriodicTimer`; per-device error isolation.
- **Exit criteria:** punches from IN and OUT land in `attendance_events` with correct role; re-running captures **zero** duplicates; killing mid-cycle and restarting loses nothing and duplicates nothing; a device-log clear does not drop new punches (reset-immunity verified).
- **Risks:** event-struct field mapping; time-zone/clock-drift handling; large first-run backfill window sizing.

### Phase 3 — User & fingerprint sync (IN → OUT)
**Goal:** OUT terminal mirrors the IN terminal's users and fingerprints.
- Read from IN: `NET_DVR_GET_USERINFO` and `NET_DVR_GET_FINGERPRINT_CFG` (search-mode) loops.
- Diff engine keyed on `employee_no` (+ finger index); per-user hash in `sync_user_map` to skip unchanged and detect deletes.
- Write to OUT: `NET_DVR_SET_USERINFO` then `NET_DVR_SET_FINGERPRINT_CFG` (user before fingerprints); binary template copy (no re-enrollment).
- Configurable delete policy (`sync.deleteRemovedUsers`, default off); update `sync_state`.
- `SyncJob` on its own `PeriodicTimer`; pagination/throttling for large populations.
- **Exit criteria:** a user + fingerprints enrolled only on IN appear on OUT after a sync cycle and verify successfully on OUT; unchanged users are skipped on re-run; delete policy behaves per config; model/firmware mismatch is warned.
- **Risks:** template compatibility between terminals; device buffer limits on bulk writes; create-before-fingerprint ordering.

### Phase 4 — Hardening & operability
**Goal:** production reliability.
- Reconnect/backoff (resilience pipelines) on device errors; circuit-break flapping devices.
- Concurrency caps (`maxConcurrentDevices`); per-pair/per-device fault isolation so one bad device never aborts a cycle.
- Health signals: last-success timestamps per job/pair; heartbeat + optional `/health`.
- Alerting hooks (device offline > threshold, sync failure); structured per-cycle summary logs.
- Load/soak test against the full population and pair count; verify cycles fit inside their intervals.
- **Exit criteria:** service survives device reboots, network drops, and Postgres blips without data loss or duplication; soak test stable; operational logs/health are actionable.
- **Risks:** SDK thread-safety under concurrency; resource/handle leaks over long runs.

### Phase 5 — Push to central Postgres (LAST)
**Goal:** push locally-stored, not-yet-pushed punches into the central (API-owned) Postgres.
- Confirm with the API owner (open item #1): central connection string, target table name/schema (`REQUIREMENTS.md` §8.1 proposed contract), INSERT-only credential, TLS.
- Implement `IAttendancePusher` default impl: `NpgsqlAttendancePusher` — batched `INSERT ... ON CONFLICT (idempotency_key) DO NOTHING` in a **central transaction**, wrapped in a Polly resilience pipeline.
- `PushJob`: select local `upload_status='pending'`, batch, commit centrally, then mark local rows `uploaded`; per-row `upload_attempts` → `dead_letter` + alert; on a poison row, fall back to per-row inserts to isolate it (partial-failure handling per `REQUIREMENTS.md` §7.3).
- Idempotency key from the reset-immune identity, stored as the central row's `UNIQUE`/PK.
- **Exit criteria:** pending rows appear in the central table and flip to `uploaded` locally; central-DB-unreachable, ambiguous-commit, and poison-row cases all behave per §7.3; no duplicates under retry (verified against the central `UNIQUE`); local buffer survives a central-DB outage and drains on recovery.
- **Risks:** central schema/credential coordination with the API owner; long central outages growing the local buffer (monitor backlog depth).

### Cross-phase: documentation & handover (continuous)
Runbook (install, config, credentials rotation, DLL deployment), troubleshooting guide (SDK error codes), and schema/migration notes maintained alongside each phase.

---

## Part 3 — Sequencing, critical path & prerequisites

**Critical path:** Phase 1 (SDK interop) gates Phases 2–3. De-risk it first with a spike. Phases 2 and 3 are largely independent after Phase 1 and could parallelize if staffed. Phase 5 depends only on Phase 2's data (not on the central DB being ready earlier).

```
P0 ──▶ P1 ──▶ P2 ──▶ P4 ──▶ P5(central push)
              └▶ P3 ──▶ ┘
```

**Prerequisites to start:**
- .NET 8 SDK; Visual Studio 2022 or VS Code + C# Dev Kit.
- A reachable **local** PostgreSQL instance (dev + target edge host).
- Access details for the **central** API-owned Postgres (needed only by Phase 5): host, INSERT-only credential, target table/schema.
- The **correct x64 HCNetSDK build** for DS-K1A8503MF-B + its DLL set.
- At least one physical/test **DS-K1A8503MF-B pair** (IN + OUT) on the network, with admin credentials and the SDK port (default 8000) reachable.
- Decision on open items `REQUIREMENTS.md` §11 #2 (credentials model) and #3 (backfill) before Phase 2; #4 (delete policy) before Phase 3. Central push target (#1) deferred to Phase 5.

**Suggested demo checkpoints:** end of each phase has a concrete, observable exit criterion above — use those as sign-off gates.
