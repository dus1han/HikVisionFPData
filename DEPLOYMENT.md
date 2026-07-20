# HikSync — Deployment & Run Guide

Step-by-step to run the service in production. It captures attendance from Hikvision terminals,
saves to a local Postgres, pushes to your API, and syncs fingerprints IN→OUT.

> **This deployment uses ISAPI (HTTP)** — the terminals reject the HCNetSDK/Private protocol, so
> `Sdk:Transport` is `isapi` by default. That means **no native DLLs and no Visual C++ redistributable
> are needed.** (The `native\` folder in the publish is unused when Transport=isapi.)

---

## Deliverables — what to deploy where

Two independent artifacts. Both are **self-contained x64 Windows builds — no .NET install required**.

| Artifact | Publish folder | Purpose | Needs |
|---|---|---|---|
| **HikSync.Service** | `C:\Users\Saboor.a\Desktop\Personal\HikSync\` | The 24/7 service: capture attendance → local Postgres → push to your API, plus the two-way fingerprint sync. Runs as a Windows service. | Postgres + network to the terminals + `appsettings.json` |
| **HikSync.DeviceCheck** | `C:\Users\Saboor.a\Desktop\Personal\HikSync-DeviceCheck\` | Diagnostics & maintenance CLI: verify a terminal, list users/fingerprints/attendance, delete users, one-off union sync, raw ISAPI probe. | Network to the terminals only — **no database, no config file** |

**Deploy the service** on a machine that stays on (the one with/near Postgres, reachable to the
terminals). **Deploy the tool** anywhere convenient — a technician's laptop is fine; just copy the
whole folder and run it from a command prompt.

The service folder also contains `scripts\` (DB + install scripts), `DEPLOYMENT.md` and `README.md`
so it's a complete, self-contained deployment package.

---

## 0. Prerequisites

- A **Windows x64** host to run the service, on the **same network** as the terminals.
- **PostgreSQL** (local on that host, or reachable over the network).
- The terminals set to the **correct timezone (Sri Lanka, UTC+05:30) with NTP enabled** — do this on
  each device (web UI / iVMS-4200 → Time). Times are only correct if the device clock is correct.
- The published service folder: **`C:\Users\Saboor.a\Desktop\Personal\HikSync\`** (or publish it yourself —
  see §7). It's self-contained, so **no .NET install is required** on the host.

---

## 1. Set up the local database

Install PostgreSQL, then (as the `postgres` superuser) create the DB + a login role and load the schema:

```sql
-- in psql, as postgres:
CREATE ROLE hiksync LOGIN PASSWORD 'ChooseAStrongPassword';
CREATE DATABASE hiksync OWNER hiksync;
```
Then, connected to the **hiksync** database, run the schema script:
```
psql -U hiksync -d hiksync -f scripts\local_db_setup.sql
```
(Or paste `scripts/local_db_setup.sql` into pgAdmin. The service also auto-applies the schema on
startup, so this step is optional — but doing it once up front is cleaner.)

---

## 2. Seed your device pairs

Each row is a location with an IN (enrollment master) and an OUT terminal. Passwords are plain text.
Example matching the tested devices (adjust IPs/passwords/locations):

```sql
INSERT INTO device_pairs (location, in_ip, in_username, in_password, out_ip, out_username, out_password)
VALUES ('Main Gate', '192.168.1.220', 'admin', '123456bio', '192.168.1.219', 'admin', 'Asd@1234');
```
- Add one row per physical door/pair.
- **Sync is a two-way union**: whichever users/fingerprints are missing on one device are copied from
  the other, so **both terminals in a couple end up holding the complete set**. It's additive only —
  it never overwrites an existing record or deletes anything. You can enroll on *either* terminal.
- Disable a pair without deleting: `UPDATE device_pairs SET enabled=false WHERE id=…;`

---

## 3. Configure `appsettings.json`

Edit `C:\Users\Saboor.a\Desktop\Personal\HikSync\appsettings.json`:

```jsonc
"LocalDatabase": {
  "ConnectionString": "Host=localhost;Port=5432;Database=hiksync;Username=hiksync;Password=ChooseAStrongPassword"
},
"Sdk": {
  "Transport": "isapi",     // keep isapi for these terminals
  "IsapiPort": 80,          // device web port (443 + IsapiHttps=true if HTTPS)
  "UseFakeDevice": false
},
"Attendance": {
  "IntervalSeconds": 60,    // how often to pull new punches
  "BackfillStartUtc": null  // null = start from now (skips old pre-fix events)
},
"Sync": {
  "Enabled": true,
  "IntervalSeconds": 300,            // run the sync every 5 min
  "Bidirectional": true,             // union: each device gets what the OTHER has and it lacks
  "OnlyUsersWithFingerprints": true, // ignore users with no fingerprint enrolled
  "DeleteRemovedUsers": false        // one-way mode only; ignored when Bidirectional
},
"Push": {
  "Enabled": true,
  "Endpoint": "http://YOUR_API_HOST:PORT/api/attendanceraw/insertB",
  "CompanyId": 1,                        // your fixed company id
  "TimeZone": "Sri Lanka Standard Time", // checkTime is sent in this zone
  "BatchSize": 200,
  "IntervalSeconds": 60
},
"Log": {
  "RetentionEnabled": true,
  "RetentionDays": 30       // nightly cleanup of operation_log
}
```

**Push payload** sent to your API (bare JSON array batch):
```json
[
  { "companyId": 1, "employeeId": 56, "checkTime": "2026-07-17 14:53:13", "checkType": "IN", "source": "Main Gate(192.168.1.220)" }
]
```

---

## 4. Run it (console first, to watch logs)

```powershell
cd C:\Users\Saboor.a\Desktop\Personal\HikSync
.\HikSync.Service.exe
```
You should see: DB migration on startup, then each cycle logging "Attendance cycle done: N new row(s)…".
Make a punch on a terminal and watch a new row appear. `Ctrl+C` to stop.

---

## 5. Install as a Windows service (runs 24/7, auto-restart)

```powershell
.\scripts\install-service.ps1 -BinPath C:\Users\Saboor.a\Desktop\Personal\HikSync\HikSync.Service.exe
```
Manage it: `Get-Service HikSync`, `Stop-Service HikSync`, `Start-Service HikSync`.
Uninstall: `.\scripts\uninstall-service.ps1`.

Logs are written to `logs\hiksync-*.log` next to the exe (and to `operation_log` in the DB).

---

## 6. Verify it's working

```sql
SET TIME ZONE 'Asia/Colombo';   -- so timestamps show in Sri Lanka time

-- attendance captured:
SELECT employee_no, event_time, role, upload_status FROM attendance_events ORDER BY event_time DESC LIMIT 20;

-- device sessions (connect -> operations -> disconnect), per device:
SELECT logged_at, device_ip, role, operation, status, message FROM operation_log ORDER BY logged_at DESC LIMIT 30;

-- anything stuck (should be rare):
SELECT count(*) FROM attendance_events WHERE upload_status = 'dead_letter';
```
- `upload_status` flips `pending` → `uploaded` once your API 2xx-acknowledges the batch.
- Confirm records arrived in your API.

---

## 7. (Optional) Re-publish from source

Clone the repo, then build both artifacts (requires the .NET SDK on the *build* machine only — the
outputs are self-contained):

```powershell
git clone git@github.com:dus1han/HikVisionFPData.git
cd HikVisionFPData

# service
dotnet publish src\HikSync.Service -c Release -r win-x64 --self-contained true -o C:\Users\Saboor.a\Desktop\Personal\HikSync
# tool
dotnet publish src\HikSync.DeviceCheck -c Release -r win-x64 --self-contained true -o C:\Users\Saboor.a\Desktop\Personal\HikSync-DeviceCheck

# bundle the helper scripts + guides with the service (optional but handy)
Copy-Item scripts C:\Users\Saboor.a\Desktop\Personal\HikSync\scripts -Recurse -Force
Copy-Item DEPLOYMENT.md,README.md C:\Users\Saboor.a\Desktop\Personal\HikSync -Force
```
Republishing **overwrites `appsettings.json` with the placeholder template** — back up your
configured copy first, or re-apply §3 afterwards.

Then copy the `HikSync` folder to the service host and `HikSync-DeviceCheck` wherever you need the tool.
Run the tests with `dotnet test` (17 should pass).

---

## 8. Deploy & use the verification tool (HikSync.DeviceCheck)

**Deploy:** copy the whole **`HikSync-DeviceCheck\`** folder to wherever you need it (technician
laptop, the service host, a USB stick). No install, no database, no config — it takes everything from
the command line. It must be able to reach the terminals on the network (HTTP port 80 by default).

```powershell
cd C:\Users\Saboor.a\Desktop\Personal\HikSync-DeviceCheck
.\HikSync.DeviceCheck.exe --help
```

Exit code is **0** if all checks pass, **1** if any fail — so it can be scripted across many devices.

**Commands** (defaults to ISAPI):
```powershell
# read everything (device info, attendance, users, fingerprints):
HikSync.DeviceCheck.exe --ip 192.168.1.220 --user admin --pass 123456bio

# delete users:
HikSync.DeviceCheck.exe --ip 192.168.1.220 --user admin --pass 123456bio --delete 25
HikSync.DeviceCheck.exe --ip 192.168.1.220 --user admin --pass 123456bio --delete all
HikSync.DeviceCheck.exe --ip 192.168.1.220 --user admin --pass 123456bio --delete-others 56

# one-off TWO-WAY union sync between a couple (each device gets what it's missing):
HikSync.DeviceCheck.exe --ip 192.168.1.220 --user admin --pass 123456bio --sync-to 192.168.1.219 --to-user admin --to-pass "Asd@1234"

# raw ISAPI diagnostics for one employee:
HikSync.DeviceCheck.exe --ip 192.168.1.220 --user admin --pass 123456bio --probe 56

# full option list:
HikSync.DeviceCheck.exe --help
```

---

## 9. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `login failed: error 1` (SDK) | Private protocol rejected — keep `Sdk:Transport=isapi` (default). |
| `Unable to load DLL 'HCNetSDK.dll'` | Only affects `Transport=sdk`. Not used in ISAPI mode; ignore. |
| "application control policy has blocked" | Target has WDAC/AppLocker — sign the binaries or have IT allow-list the folder. |
| Attendance times off by hours | Terminal timezone/clock wrong — set device to **Sri Lanka (UTC+05:30) + NTP**. |
| `read 0 event(s)` | No punches in the window, or `BackfillStartUtc` after the events — widen `Attendance:IntervalSeconds`/make a punch. |
| Push rows stay `pending` | `Push:Enabled=false`, wrong `Endpoint`, or API unreachable — check `operation_log` and service logs. |
| Push rows go `dead_letter` | API returned 4xx repeatedly — check the payload/contract against your API. |
| Fingerprint sync `FAIL` | Check the device password on the OUT device and that the user exists; the tool prints the raw HTTP error. |

---

## What's proven vs. what to watch

- **Validated on real hardware (ISAPI):** device login, attendance read, user read/write, fingerprint
  read/write, one-off sync, delete.
- **Watch on first live run:** the local Postgres connection, your API accepting the batch (2xx), and
  device clocks staying correct. Everything else is exercised.
