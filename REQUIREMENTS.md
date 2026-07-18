# Hikvision Attendance & Device-Sync Windows Service — Detailed Requirements

**Document status:** Draft v1.0
**Date:** 2026-07-16
**Target device:** Hikvision DS-K1A8503MF-B (fingerprint + card + face access terminal)
**Integration:** HCNetSDK (Device Network SDK), native x86/x64 DLLs via P/Invoke
**Runtime:** C# / .NET 8 Windows Service (Worker Service / `BackgroundService`)
**Local store:** PostgreSQL

---

## 1. Purpose & Scope

Build a always-on Windows service that, for a configurable set of **device pairs**, performs two independent jobs:

1. **Attendance collection & upload** — periodically pull *new* access/attendance events from every device (both IN and OUT), persist them locally without duplicates, then push not-yet-uploaded rows to an external API and mark them uploaded.
2. **Biometric/user sync** — treat the **IN** device of each pair as the *enrollment master*. Read its **user records** and **fingerprint templates** and write them to the paired **OUT** device so both terminals recognize the same people.

A "device pair" is one physical access point with two terminals: the **IN** terminal records clock-ins, the **OUT** terminal records clock-outs. The pairing is defined by a Postgres table keyed on `Location | IN IP | OUT IP`.

### 1.1 In scope
- Polling both terminals of every pair for access events.
- Incremental (delta) event retrieval — never re-fetch already-captured events.
- Persistence into a **local edge** Postgres with a durable dedup key and a push watermark (offline-tolerant buffer).
- Batched **transactional push into the central (API-owned) Postgres** — DB→DB, not HTTP (§7–§8).
- One-way user + fingerprint sync IN → OUT with add/update/delete reconciliation.
- Resilience to device offline / network failure / service restart.
- Structured logging and operational health signals.

### 1.2 Out of scope (this version)
- Face and card credential sync (fingerprint + user info only — confirmed). Design leaves hooks to add these later (§6.5).
- Bi-directional or OUT → IN sync.
- Real-time event push from device (alarm callback). Polling only in v1; §5.5 notes the upgrade path.
- Enrollment UI. Enrollment happens on the physical IN device.
- Device firmware/config management beyond what sync requires.

---

## 2. Key Design Decisions (confirmed)

| Decision | Choice | Consequence |
|---|---|---|
| Device API | **HCNetSDK** (native) | Ship x86 *or* x64 DLL set; process bit-width **must** match the DLLs. See §3.1. |
| Language/host | **C# / .NET 8 Worker Service** | P/Invoke wrappers around `HCNetSDK.dll`; `Npgsql` for Postgres; hosted as a Windows service. |
| Sync scope | **Fingerprint + user info** | Face/card/password deferred; structures selected to allow adding them without schema rework. |
| Data topology | **Local edge PG only** | Service captures into a **local** Postgres (offline-tolerant buffer next to the devices). This is the **only** DB the service sets up. |
| Push target | **Remote HTTP API** | The service POSTs not-yet-uploaded rows (with device IP + IN/OUT direction) to an API the customer provides (`Push:Endpoint`, may be an IP:port URL). The API owns its own storage. Behind the pluggable `IAttendancePusher` interface (§8). |

---

## 3. Architecture Overview

```
┌──────────────────────── Windows Service (.NET 8) ────────────────────────┐
│                                                                           │
│  Host (BackgroundService)                                                 │
│   ├── PairConfigProvider   ← reads device_pairs from Postgres             │
│   ├── SdkManager           ← NET_DVR_Init / login handle cache / cleanup  │
│   │                                                                       │
│   ├── AttendanceJob (timer)                                               │
│   │     for each device (IN & OUT):                                       │
│   │       fetch events since watermark → dedup → insert → advance mark    │
│   │                                                                       │
│   ├── PushJob (timer)                                                     │
│   │     select pending → batch → POST to remote API → mark uploaded       │
│   │                                                                       │
│   └── SyncJob (timer)                                                     │
│         for each pair:                                                     │
│           read users+fingerprints from IN → diff → write to OUT           │
│                                                                           │
│  Cross-cutting: Serilog logging, health, retry/backoff, config           │
└───────────────────────────────────────────────────────────────────────────┘
        │ Npgsql (local)            │ HCNetSDK P/Invoke     │ HttpClient
        ▼                           ▼                       ▼
   Local edge PostgreSQL      Hikvision terminals     Remote API (customer-owned)
   (capture + offline buffer)                          POST /api/attendance
```

Three independent scheduled jobs share one SDK login cache and one DB. Each runs on its own interval so a slow sync never blocks attendance collection.

### 3.1 HCNetSDK deployment (critical)
- Obtain the SDK matching the service process architecture. **x64 process → x64 DLLs; x86 process → x86 DLLs. Mixing throws `BadImageFormatException` or silent init failure.** Pin the build to a single architecture (recommend **x64**).
- Required binaries typically include: `HCNetSDK.dll`, `HCCore.dll`, `PlayCtrl.dll`, the `HCNetSDKCom\` plugin folder (e.g. `HCCoreDevCfg.dll`, `AudioRender.dll`, `SystemTransform.dll`), and OpenSSL/other dependency DLLs shipped with the SDK.
- Deploy these next to the service executable (or a known folder added via `NET_DVR_SetSDKInitCfg` / `NET_DVR_SetSDKLocalCfg` path config). Missing plugin DLLs cause login or config calls to fail with obscure error codes.
- Call `NET_DVR_Init()` once at startup and `NET_DVR_Cleanup()` once at shutdown. Optionally `NET_DVR_SetLogToFile()` during troubleshooting.

---

## 4. Data Model (PostgreSQL)

> **This section is the LOCAL edge database** (capture + offline buffer, co-located with the service). The **central** API-owned Postgres has a single push-target table, defined separately in §8.1. All timestamps `timestamptz`; event times normalized to UTC with raw device-local time + offset kept for audit (§9.4). Names are indicative; adapt to house conventions.

### 4.1 `device_pairs` (extends the existing Location | IN IP | OUT IP table)
| Column | Type | Notes |
|---|---|---|
| id | bigint PK | |
| location | text | Human label. |
| in_ip | inet/text | Enrollment master. |
| out_ip | inet/text | Slave for sync. |
| sdk_port | int | Default 8000 (HCNetSDK). Per-device override if needed. |
| username | text | Device admin user. |
| password | text | Plain text (per deployment decision). Actual schema uses per-role `in_password`/`out_password`. |
| enabled | bool | Skip disabled pairs. |
| created_at / updated_at | timestamptz | |

> If IN and OUT can have different credentials/ports, split into per-device columns or a separate `devices` table (`device_pairs` then references two `devices` rows). Recommended: a `devices` table + a `device_pairs` linking table for cleanliness.

### 4.2 `attendance_events` (raw captured punches)
| Column | Type | Notes |
|---|---|---|
| id | bigint PK | |
| device_ip | text | Which terminal produced it. |
| pair_id | bigint FK | |
| role | text `IN`/`OUT` | Derived from which terminal (in_ip → IN, out_ip → OUT). **This is how clock-in vs clock-out is determined** — the device pair role, not a device attendance-status field. |
| employee_no | text | Hikvision `employeeNo` / user ID. |
| card_no | text null | If verification used a card. |
| event_time | timestamptz | Device event time → UTC. |
| device_serial_no | bigint | Per-device incremental event serial (`dwSerialNo`) — primary dedup key. |
| major / minor | int | Event major/minor type (verify success, fingerprint, card, face…). |
| verify_mode | text | Fingerprint / card / face / password / combination. |
| raw_json | jsonb | Full decoded event for audit/replay. |
| fetched_at | timestamptz | When the service captured it. |
| upload_status | text default `pending` | `pending` / `uploaded` / `dead_letter`. Replaces a plain bool so poison rows can be parked. |
| uploaded_at | timestamptz null | Set only on positive acknowledgment. |
| upload_attempts | int default 0 | Incremented per failed attempt; drives dead-lettering. |
| last_upload_error | text null | Reason from the API or transport for the last failure. |
| **UNIQUE** | (device_ip, employee_no, event_time, major, minor) | Durable event identity — **reset-immune** (survives device event-log/factory reset, unlike `dwSerialNo`). Also the basis of the upload **idempotency key**. `device_serial_no` is kept for ordering/audit only, not identity. |

Indexes: `(upload_status) WHERE upload_status='pending'`, `(device_ip, event_time)`, `(employee_no, event_time)`.

### 4.3 `fetch_watermark` (per device, attendance delta cursor)
| Column | Type | Notes |
|---|---|---|
| device_ip | text PK | |
| last_event_time | timestamptz | High-water mark for the next query window. |
| last_serial_no | bigint | Secondary cursor to break ties within the same second. |
| last_run_at | timestamptz | |
| last_status | text | ok / offline / error. |
| last_error | text null | |

### 4.4 `sync_state` (per pair user/fingerprint sync)
| Column | Type | Notes |
|---|---|---|
| pair_id | bigint PK/FK | |
| last_sync_at | timestamptz | |
| in_user_count / out_user_count | int | For reconciliation reporting. |
| last_status / last_error | text | |

### 4.5 `sync_user_map` (optional, delta tracking)
Tracks per-employee sync fingerprint (hash of user record + template) so the sync job can skip unchanged users and detect deletes. Columns: `pair_id, employee_no, in_hash, synced_hash, deleted_on_in bool, last_synced_at`.

### 4.6 `operation_log` (device/service audit log)
Records **every device transaction from connect to disconnect**, plus service events, each tagged with the device IP and IN/OUT role.
| Column | Type | Notes |
|---|---|---|
| id | bigserial PK | |
| logged_at | timestamptz | default now(). |
| device_ip | text null | Terminal IP (null for non-device events). |
| role | text null `IN`/`OUT` | Which machine. |
| pair_id | bigint null | |
| operation | text | `connect` / `disconnect` / `attendance` / `sync` / `push` / `error`. |
| status | text | `ok` / `info` / `error`. |
| message | text | The log line (e.g. "connected", "read 12, new 3", error text). |
| duration_ms | int null | Operation duration where applicable. |

Indexes: `(logged_at)`, `(device_ip, logged_at)`. Writes are **best-effort** — a logging failure is itself logged (Serilog) but never breaks the work flow. **Retention:** a nightly job (`LogRetentionWorker`, runs once/day after `Log:CleanupHourLocal`, default 02:00 local) deletes rows older than `Log:RetentionDays` (default **30**) — `DELETE FROM operation_log WHERE logged_at < now() - N days`. The cleanup itself is recorded as an `operation=cleanup` row.

---

## 5. Functional Requirement A — Attendance Collection

### 5.1 Trigger
`AttendanceJob` runs on a configurable interval (default **60 s**; per-deployment tunable). Each run iterates every enabled pair and, within it, both the IN and OUT terminals.

### 5.2 Retrieval (HCNetSDK)
Access/attendance events are pulled via the **remote-config search** flow:

1. `NET_DVR_Login_V40()` → cached login handle (`lUserID`) per device (reuse across runs; re-login on failure).
2. `NET_DVR_StartRemoteConfig(lUserID, NET_DVR_GET_ACS_EVENT, &cond, sizeof(cond), null, null)` where `cond` is `NET_DVR_ACS_EVENT_COND`:
   - `dwMajor` / `dwMinor` = event filter. **Config knob `attendance.countedVerifyModes`** decides what qualifies as a punch on this multi-mode terminal: default = **any successful verification** (fingerprint / card / face / PIN); can be narrowed to fingerprint-only. Filtering is applied here (device-side minor filter) and re-checked on decode so non-attendance events — door-forced, tamper, invalid-attempt, etc. — are excluded.
   - `struStartTime` / `struEndTime` = **[watermark, now]** window (device-local time).
   - `byPicEnable` = 0 (no picture) for v1 to keep payload small.
3. Loop `NET_DVR_GetNextRemoteConfig()` → each record is `NET_DVR_ACS_EVENT_CFG` containing `struAcsEventInfo` with: `dwMajor/dwMinor`, `struTime`, `byCardNo`, `byEmployeeNo`/`dwEmployeeNo`, `dwSerialNo`, `byVerifyMode`, door/reader IDs.
   - Return codes: `NET_DVR_GET_NEXTMATCH` (this record valid, keep going), `NET_DVR_LIST_READY`/no-more (stop), `NET_DVR_GET_ABILITY` etc. Handle end-of-list and errors distinctly.
4. `NET_DVR_StopRemoteConfig(handle)`.

> Exact struct/enum names vary slightly by SDK version (e.g. `NET_DVR_ACS_EVENT_COND`, `NET_DVR_ACS_EVENT_CFG`). Bind against the header/`.cs` wrapper of the **SDK build you deploy** and verify field offsets. Employee number is a fixed-length byte array — trim nulls.

### 5.3 Delta / "only records not previously taken"

**Requirement:** only events never captured before may be processed, stored, and uploaded. HCNetSDK's event search is *time-range* based — there is no wire-level "unread only" filter — so the guarantee is enforced end-to-end via a **serial-number cursor + DB unique key**, not by trusting time alone.

**Identity key:** the durable "have I seen this" identity is the composite `(device_ip, employee_no, event_time, major, minor)`. `dwSerialNo` (per-device monotonic serial) is used as the **ordering cursor** to shrink the query window — but NOT as the identity, because it restarts from a low number on device event-log/factory reset and would then false-collide with old rows and silently drop new punches. The composite key keeps moving forward regardless and is reset-immune.

**Per-device algorithm, each cycle:**
1. Read cursor `(last_event_time, last_serial_no)` from `fetch_watermark`. First run: use a configurable backfill start (explicit date or today 00:00).
2. Query the window `[last_event_time, now]` — **start inclusive** (never `last_event_time + 1s`; that would drop same-second punches).
3. For each returned record: `INSERT ... ON CONFLICT (device_ip, employee_no, event_time, major, minor) DO NOTHING`. Already-seen records collide and are dropped — they never re-enter the pipeline and never get re-uploaded.
4. Track the max `(event_time, serial_no)` actually persisted this window.
5. **Only if the window completed without error**, advance the cursor to that max. A mid-window failure leaves the cursor put, so the next cycle re-queries the same start and the unique key absorbs the overlap.

**Guarantees this yields:**
- *Never twice* — unique key drops duplicates at the DB (correctness does not depend on the cursor being exact).
- *Never skipped* — inclusive window start + advance-only-on-success.
- *Minimal re-fetch* — advancing the cursor keeps each window thin; the device sends only a small overlap, not the full log.

> The cursor is an **optimization** to shrink the query; the **unique key is the correctness guarantee.** This is what makes restarts, retries, and overlapping windows all safe.

**Clock-in vs clock-out** is assigned by **role** = whether the event came from `in_ip` (IN) or `out_ip` (OUT). No reliance on the device's own attendance-status setting.

### 5.4 Failure handling
- Device offline / login fail → mark `fetch_watermark.last_status='offline'`, log, **do not advance** watermark, continue to next device. Retry next cycle.
- Partial page then error → rows already inserted stay (idempotent); watermark advances only to the last *safely persisted* event.

### 5.5 Future: real-time capture (noted, not built)
HCNetSDK supports alarm/event upload via `NET_DVR_SetupAlarmChan_V41` + message callback for near-real-time punches. v1 uses polling; the event-decode/persist code should be factored so a callback path can reuse it.

---

## 6. Functional Requirement B — User & Fingerprint Sync (two-way union)

### 6.1 Principle
**Both terminals in a couple must end up holding the complete set of users + fingerprints for that couple.** The sync compares the two devices and copies to each whatever the *other* has that it is missing — so a person can be enrolled on **either** terminal and will be present on both.

- **Additive only.** It copies missing records; it never overwrites a record that already exists on the target (a record present on both, but differing, is left alone — otherwise the two devices would overwrite each other on every cycle) and never deletes.
- **Only users with a fingerprint** are synced (`Sync:OnlyUsersWithFingerprints`, default on).
- Set `Sync:Bidirectional=false` for the legacy one-way behaviour where **IN** is the master and **OUT** mirrors it (that mode still supports `DeleteRemovedUsers`).

### 6.2 Trigger
`SyncJob` on a configurable interval (default **5 min**, or on-demand via a control signal). Per pair.

### 6.3 Read from IN device
1. **Users:** `NET_DVR_StartRemoteConfig(lUserID_IN, NET_DVR_GET_USERINFO, &cond, …)` → iterate `NET_DVR_USER_INFO_CFG_V50` records: `byEmployeeNo`, `byName`, `struValid` (validity period), `byUserType`, door-right/`byDoorRight`, `byUserVerifyMode`, etc. Loop with `NET_DVR_GetNextRemoteConfig` until end.
2. **Fingerprints:** `NET_DVR_StartRemoteConfig(lUserID_IN, NET_DVR_GET_FINGERPRINT_CFG, &cond, …)` (search-mode variant, e.g. `NET_DVR_GET_FINGERPRINT_CFG_V50`) → `NET_DVR_FINGERPRINT_INFO_CFG_V50`: `byEmployeeNo`, `dwFingerPrintID`/`byFingerPrintID` (finger index 1–10), `byFingerData` (template blob) + length. A user may have multiple fingers → multiple records.

### 6.4 Diff & write to OUT device
- Build the IN set keyed by `employee_no` (+ finger index for prints). Compute a hash per user (user fields + all its templates).
- Compare against OUT (read OUT users/prints the same way, or use `sync_user_map.synced_hash`).
- **Add/Update:** for each IN user missing or changed on OUT:
  - `NET_DVR_SetUserInfo` / remote-config `NET_DVR_SET_USERINFO` with `NET_DVR_USER_INFO_CFG_V50` (opType = add/modify).
  - For each finger: `NET_DVR_SetFingerPrint` / remote-config `NET_DVR_SET_FINGERPRINT_CFG_V50` with the template bytes read from IN. **Templates are copied binary — no re-enrollment.**
- **Delete:** users present on OUT but absent on IN → delete on OUT (`NET_DVR_DEL_USERINFO` / user-info delete op by employeeNo, or clear). Make delete behavior **configurable** (`sync.deleteRemovedUsers = true/false`) — some sites prefer never auto-deleting.
- Update `sync_user_map` and `sync_state` after each pair.

### 6.5 Extensibility hooks (deferred features)
Face (`NET_DVR_GET/SET_FACE`), card, and password copy follow the identical read-diff-write shape. Keep the sync engine generic over a `ICredentialChannel` so adding them is additive.

### 6.6 Constraints
- **Template compatibility:** IN and OUT must be the same/compatible model & firmware family for fingerprint templates to transfer. DS-K1A8503MF-B ↔ DS-K1A8503MF-B is the assumption. Flag a warning if device models/firmware differ (read via `NET_DVR_GetDVRConfig` device info).
- **Ordering:** create the user record before writing its fingerprints (fingerprint references employeeNo).
- **Rate/size:** large user bases → paginate and throttle to avoid overrunning device buffers; respect SDK per-call record limits.

---

## 7. Functional Requirement C — Push to Remote API

Rows are captured into the **local** edge Postgres (§4), then POSTed to a **remote HTTP API the customer provides** (`Push:Endpoint`, may be an IP:port URL). The customer owns the API and whatever storage sits behind it; **the service sets up only the local DB.** Each pushed record carries its **device IP**, **IN/OUT direction**, and a stable **idempotency key** so the API can dedup.

### 7.1 Trigger
`PushJob` on a configurable interval (default **60 s**).

### 7.2 Behavior
1. Select a batch from local: `attendance_events WHERE upload_status='pending' ORDER BY event_time LIMIT N` (config `push.batchSize`, default 200).
2. POST the batch as JSON (§8) to `push.endpoint` with the configured auth.
3. On **2xx**: mark those rows `upload_status='uploaded', uploaded_at=now()`.
4. On **4xx** (API rejected the batch): increment `upload_attempts`, set `last_upload_error`; after `push.maxAttempts` → `upload_status='dead_letter'` + alert.
5. On **5xx / timeout / network error**: leave rows `pending`, retry next cycle (no attempt burn) — the local edge DB buffers during the outage.
6. **Idempotency key** per row from the reset-immune identity — `{device_ip}:{employee_no}:{unix(event_time)}:{major}:{minor}` — sent so the API can make the push effectively-once.

### 7.3 Failure handling (core requirement)
Rows are marked `uploaded` **only on a 2xx**; everything else stays `pending`/retries or dead-letters.

| Mode | What happened | Handling |
|---|---|---|
| Transient (5xx / timeout / unreachable) | API down or network drop | Nothing marked. Rows stay `pending`, retried next cycle; local buffer absorbs the outage. |
| Ambiguous (2xx lost after commit) | API stored it but response lost | Retried; the API dedups on the idempotency key, so no double-count. |
| Batch rejected (4xx) | API refused the payload | Rows counted against `upload_attempts` → `dead_letter` after `maxAttempts` + alert, so a bad batch doesn't block forever. |

**API contract note:** the default treats **2xx = whole batch accepted**. If your API returns **per-record results keyed by idempotency key**, the pusher can be extended to mark exactly the accepted rows and reject only the bad ones (finer-grained than whole-batch). Recommended if feasible on the API side.

---

## 8. Push Target — Remote HTTP API

The push destination is the customer-provided API. The service writes through a pluggable interface (`HttpAttendancePusher` is the default implementation; the target could change without touching capture):

```csharp
public interface IAttendancePusher {
    bool Enabled { get; }
    Task<PushResult> PushAsync(IReadOnlyList<AttendanceRecord> batch, CancellationToken ct);
}
```

Configuration (`appsettings.json`):
```jsonc
"Push": {
  "Enabled": false,
  "Endpoint": "http://REMOTE_API_IP:PORT/api/attendance",  // may be an IP:port URL
  "AuthType": "None",        // None | Bearer | ApiKey | Basic
  "AuthValue": null,         // token / key / base64 basic creds
  "ApiKeyHeader": "X-API-Key",
  "BatchSize": 200,
  "MaxAttempts": 8,
  "IntervalSeconds": 60,
  "TimeoutSeconds": 30
}
```

### 8.1 Request payload (default — adjust to your API)
```json
{
  "records": [
    {
      "idempotencyKey": "192.168.1.10:1042:1752652282:5:75",
      "deviceIp": "192.168.1.10",
      "direction": "IN",
      "location": "Main Gate",
      "employeeNo": "1042",
      "eventTime": "2026-07-16T08:31:22Z",
      "verifyMode": "Fingerprint",
      "cardNo": null
    }
  ]
}
```

### 8.2 Response contract
- **2xx** → the service marks the whole batch `uploaded`.
- **4xx** → batch rejected (rows → `dead_letter` after `MaxAttempts`).
- **5xx / timeout / network** → transient; rows stay `pending` and retry (local buffer absorbs it).
- Optional finer-grained mode: if the API returns **per-record results keyed by `idempotencyKey`**, the pusher can mark exactly the accepted rows and reject only the bad ones. `idempotencyKey` is the stable dedupe identity the API should key on.

Only the `IAttendancePusher` implementation + `Push` config depend on the API contract; local capture and dedup are untouched.

---

## 9. Non-Functional Requirements

### 9.1 Service lifecycle
- Runs as a Windows Service (auto-start, restart-on-failure via SC recovery). Installable with `sc.exe create` / `New-Service` / an installer.
- Graceful shutdown: stop timers, finish in-flight batch, `NET_DVR_Cleanup()`.

### 9.2 Configuration
- `appsettings.json` + environment overrides: DB connection, job intervals, SDK path, backfill start date, upload config, sync delete toggle, log level.
- Device credentials live in Postgres (`device_pairs`), encrypted at rest.

### 9.3 Concurrency & SDK safety
- Reuse one login handle per device across runs; guard SDK calls per device (HCNetSDK is broadly thread-safe across handles but serialize per-device config sessions).
- Isolate failures per device/pair — one bad device must not abort a whole cycle.
- Cap parallelism (config `maxConcurrentDevices`) to protect device and host.

### 9.4 Time handling
- Devices report **local** time. Read device timezone (`NET_DVR_GetDVRConfig` time cfg) or configure per pair. Persist `event_time` as UTC + keep original local time + offset in `raw_json`. Watermark comparisons use a single consistent basis.

### 9.5 Security
- Device passwords are stored **plain text** in `device_pairs` (per deployment decision) — protect the local DB with normal Postgres access controls; never log passwords.
- Least-privilege service account for the local DB.
- Use HTTPS (and an auth token/key via `Push:AuthType`) for the remote push API when it leaves the trusted network.

### 9.6 Observability
- **Serilog** to rolling file (+ optional DB sink): per-cycle summary (devices polled, events captured, uploaded, sync add/update/delete counts), and per-device error detail with `NET_DVR_GetLastError` codes decoded.
- Health signals: last successful attendance cycle, last successful central push, last successful sync per pair, and **central-DB reachability + local push backlog depth**; surface as a heartbeat file / Windows perf counter / optional `/health`.
- Alerting hook on: device offline > threshold, central DB unreachable / push backlog growing, push dead-letter, sync failure.

### 9.7 Resilience
- Reconnect/re-login with backoff on device errors.
- All DB writes idempotent; safe to kill and restart at any point with no data loss or duplication.
- Bounded retry with dead-lettering for uploads.

### 9.8 Performance targets (initial, tune later)
- Attendance cycle for N pairs (2N devices) completes within the poll interval.
- Sync handles the full enrolled population per pair within its interval without saturating the device.

---

## 10. Error Codes & Edge Cases to Handle
- Login failures: wrong password / user locked / max sessions reached / device unreachable — decode `NET_DVR_GetLastError`.
- Empty result windows (no new events) — normal, not an error.
- Serial number wrap / device factory-reset (serial restarts low) — **not a problem**: identity is the reset-immune composite key (§4.2/§5.3), not the serial, so post-reset events never false-collide with old rows. Serial is used only as the ordering cursor; on a detected regression, fall back to pure time-window advancement.
- Clock drift between device and server — bound query window with slack (e.g. re-query last few minutes each cycle; dedup absorbs overlap).
- Fingerprint template rejected by OUT device (incompatible/corrupt) — log per-employee, continue, report in sync summary.
- Duplicate employeeNo enrolled on both devices independently — IN wins (master).
- Partial sync interruption — resumable via `sync_user_map` hashes.

---

## 11. Open Items to Confirm Before Build
1. **Central push target** — ✅ **RESOLVED: push to the API's central Postgres** (not HTTP). Still to confirm with the API owner: central connection string, target table name/schema (§8.1 is a proposed contract), and INSERT-only credential.
2. **Credentials model** — are IN and OUT credentials/ports ever different? (drives §4.1 shape).
3. **Backfill** — first-run start date / how much history to import on a brand-new install.
4. **Delete policy** — should sync delete OUT users removed from IN? (default: configurable, off).
5. **SDK build & architecture** — confirm x64, obtain the matching DLL set + the exact struct/`.cs` wrapper version to bind against.
6. **Face/card/password** — confirmed out of scope now; confirm they're wanted later so hooks are shaped correctly.
7. **Deployment** — target Windows version(s), service account, where DLLs and config live.
8. **What counts as a punch** — ✅ **RESOLVED: any successful verification** (fingerprint/card/face/PIN) counts. `attendance.countedVerifyModes` defaults to all; still overridable per deployment.

---

## 12. Build Milestones

See **`DEVELOPMENT_PLAN.md`** for the full tech stack, phase tasks, exit criteria, and critical path. Summary (API is built **last** — local capture is separable from upload):

1. **P0 – Foundations:** .NET 8 worker, config, Postgres schema + migrations, Serilog, install script.
2. **P1 – SDK bring-up:** P/Invoke interop, init/login/cleanup, read device info (critical path — spike first).
3. **P2 – Attendance:** event search + decode + reset-immune dedup + cursor for all pairs with IN/OUT roles.
4. **P3 – Sync:** read users+prints from IN, diff, write to OUT, delete policy, sync_state.
5. **P4 – Hardening:** resilience, concurrency, alerting, health, load test, docs.
6. **P5 – Push to central PG (last):** `IAttendancePusher` + Npgsql transactional insert into the central DB + partial-failure/retry/dead-letter.
