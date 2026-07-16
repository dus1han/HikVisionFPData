-- Audit log: every device transaction (connect -> operations -> disconnect) and service event,
-- tagged with device IP and IN/OUT role.
CREATE TABLE IF NOT EXISTS operation_log (
    id          bigserial   PRIMARY KEY,
    logged_at   timestamptz NOT NULL DEFAULT now(),
    device_ip   text,
    role        text        CHECK (role IN ('IN','OUT')),
    pair_id     bigint,
    operation   text        NOT NULL,   -- connect | disconnect | attendance | sync | push | error
    status      text        NOT NULL,   -- ok | info | error
    message     text,
    duration_ms int
);

CREATE INDEX IF NOT EXISTS ix_operation_log_time   ON operation_log (logged_at);
CREATE INDEX IF NOT EXISTS ix_operation_log_device ON operation_log (device_ip, logged_at);
