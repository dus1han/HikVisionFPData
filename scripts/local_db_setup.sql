-- ============================================================================
-- HikSync LOCAL edge database — full schema.
--
-- NOTE: the service applies this automatically on startup (DbUp migrations
-- 0001/0002) when LocalDatabase:MigrateOnStartup = true. This consolidated
-- script is for manual setup or review; it mirrors the embedded migrations and
-- is safe to run first (CREATE ... IF NOT EXISTS — DbUp then no-ops).
-- ============================================================================

-- --- 1. Database + least-privilege role (run these as a superuser, e.g. postgres) ---
-- CREATE ROLE hiksync LOGIN PASSWORD 'CHANGE_ME';
-- CREATE DATABASE hiksync OWNER hiksync;
-- \c hiksync
-- (then run the rest of this file connected to the hiksync database)

-- --- 2. Schema ---------------------------------------------------------------

-- Device pairs: one location, an IN (enrollment master) and an OUT terminal.
-- Per-role credentials so IN and OUT may differ. Passwords are plain text.
CREATE TABLE IF NOT EXISTS device_pairs (
    id            bigserial   PRIMARY KEY,
    location      text        NOT NULL,
    in_ip         text        NOT NULL,
    in_port       int         NOT NULL DEFAULT 8000,
    in_username   text        NOT NULL DEFAULT 'admin',
    in_password   text        NOT NULL DEFAULT '',
    out_ip        text        NOT NULL,
    out_port      int         NOT NULL DEFAULT 8000,
    out_username  text        NOT NULL DEFAULT 'admin',
    out_password  text        NOT NULL DEFAULT '',
    enabled       boolean     NOT NULL DEFAULT true,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);

-- Captured attendance events. Identity = reset-immune composite, surfaced as idempotency_key.
CREATE TABLE IF NOT EXISTS attendance_events (
    id                bigserial   PRIMARY KEY,
    pair_id           bigint      NOT NULL REFERENCES device_pairs(id),
    device_ip         text        NOT NULL,
    role              text        NOT NULL CHECK (role IN ('IN','OUT')),
    location          text        NOT NULL,
    employee_no       text        NOT NULL,
    card_no           text,
    event_time        timestamptz NOT NULL,
    device_serial_no  bigint,
    major             int         NOT NULL,
    minor             int         NOT NULL,
    verify_mode       text,
    raw_json          jsonb,
    fetched_at        timestamptz NOT NULL DEFAULT now(),
    idempotency_key   text        NOT NULL,
    upload_status     text        NOT NULL DEFAULT 'pending'
                                  CHECK (upload_status IN ('pending','uploaded','dead_letter')),
    upload_attempts   int         NOT NULL DEFAULT 0,
    last_upload_error text,
    uploaded_at       timestamptz,
    CONSTRAINT uq_attendance_idempotency UNIQUE (idempotency_key)
);

CREATE INDEX IF NOT EXISTS ix_attendance_pending
    ON attendance_events (event_time) WHERE upload_status = 'pending';
CREATE INDEX IF NOT EXISTS ix_attendance_device_time
    ON attendance_events (device_ip, event_time);
CREATE INDEX IF NOT EXISTS ix_attendance_employee_time
    ON attendance_events (employee_no, event_time);

-- Per-device delta cursor for attendance capture.
CREATE TABLE IF NOT EXISTS fetch_watermark (
    device_ip        text        PRIMARY KEY,
    last_event_time  timestamptz,
    last_serial_no   bigint,
    last_run_at      timestamptz,
    last_status      text,
    last_error       text
);

-- Per-pair sync bookkeeping.
CREATE TABLE IF NOT EXISTS sync_state (
    pair_id          bigint      PRIMARY KEY REFERENCES device_pairs(id),
    last_sync_at     timestamptz,
    in_user_count    int         NOT NULL DEFAULT 0,
    out_user_count   int         NOT NULL DEFAULT 0,
    last_status      text,
    last_error       text
);

-- Optional per-user delta tracking for sync.
CREATE TABLE IF NOT EXISTS sync_user_map (
    pair_id          bigint      NOT NULL REFERENCES device_pairs(id),
    employee_no      text        NOT NULL,
    in_hash          text,
    synced_hash      text,
    last_synced_at   timestamptz,
    PRIMARY KEY (pair_id, employee_no)
);

-- Audit log: every device transaction (connect -> operations -> disconnect) + service events.
CREATE TABLE IF NOT EXISTS operation_log (
    id          bigserial   PRIMARY KEY,
    logged_at   timestamptz NOT NULL DEFAULT now(),
    device_ip   text,
    role        text        CHECK (role IN ('IN','OUT')),
    pair_id     bigint,
    operation   text        NOT NULL,   -- connect | disconnect | attendance | sync | push | error | cleanup
    status      text        NOT NULL,   -- ok | info | error
    message     text,
    duration_ms int
);

CREATE INDEX IF NOT EXISTS ix_operation_log_time   ON operation_log (logged_at);
CREATE INDEX IF NOT EXISTS ix_operation_log_device ON operation_log (device_ip, logged_at);

-- --- 3. Grants (if using the least-privilege role above) ---------------------
-- GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO hiksync;
-- GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO hiksync;
