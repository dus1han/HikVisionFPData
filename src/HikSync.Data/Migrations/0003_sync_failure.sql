-- Per-item sync failures, so "which employee failed to sync to which device, and why" is queryable
-- instead of living only in the log file. Upserted, not append-only: one row per distinct failing
-- (pair, target device, employee, finger, operation), with a running attempt count and last_seen_at.
-- A row whose last_seen_at has stopped advancing has resolved — filter on it to see current problems.

CREATE TABLE IF NOT EXISTS sync_failure (
    id            bigserial   PRIMARY KEY,
    pair_id       bigint      REFERENCES device_pairs(id),
    source_ip     text,                                   -- device the record came FROM
    target_ip     text        NOT NULL,                   -- device the write FAILED on
    employee_no   text        NOT NULL,
    finger_index  int         NOT NULL DEFAULT 0,         -- 1..10 for fingerprints, 0 = not applicable
    operation     text        NOT NULL,                   -- user | fingerprint | delete
    error         text,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at  timestamptz NOT NULL DEFAULT now(),
    attempts      int         NOT NULL DEFAULT 1,
    CONSTRAINT uq_sync_failure UNIQUE (pair_id, target_ip, employee_no, finger_index, operation)
);

CREATE INDEX IF NOT EXISTS ix_sync_failure_last_seen ON sync_failure (last_seen_at);
CREATE INDEX IF NOT EXISTS ix_sync_failure_pair      ON sync_failure (pair_id);
