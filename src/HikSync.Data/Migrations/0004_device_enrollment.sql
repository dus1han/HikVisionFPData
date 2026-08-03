-- A queryable mirror of who is enrolled on each device. Refreshed wholesale per device every sync
-- cycle from what the service already reads, so a user removed from a device drops out of the table.
-- One row per (device, employee); fingerprint_count = 0 means the user has no finger enrolled there.

CREATE TABLE IF NOT EXISTS device_enrollment (
    device_ip         text        NOT NULL,
    employee_no       text        NOT NULL,
    pair_id           bigint      REFERENCES device_pairs(id),
    location          text,
    role              text        CHECK (role IN ('IN','OUT')),
    name              text,
    enabled           boolean     NOT NULL DEFAULT true,
    fingerprint_count int         NOT NULL DEFAULT 0,
    finger_ids        int[]       NOT NULL DEFAULT '{}',
    last_synced_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (device_ip, employee_no)
);

CREATE INDEX IF NOT EXISTS ix_device_enrollment_emp  ON device_enrollment (employee_no);
CREATE INDEX IF NOT EXISTS ix_device_enrollment_pair ON device_enrollment (pair_id);
