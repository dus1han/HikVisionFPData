-- Seed device pairs into the LOCAL database.
-- Passwords are stored as plain text. Use '' for a passwordless terminal or Sdk:UseFakeDevice.

INSERT INTO device_pairs
    (location,     in_ip,          in_port, in_username, in_password,
                   out_ip,         out_port, out_username, out_password, enabled)
VALUES
    ('Main Gate',  '192.168.1.10', 8000,    'admin',     'device_password',
                   '192.168.1.11', 8000,    'admin',      'device_password', true);

-- Add more pairs as needed:
-- INSERT INTO device_pairs (location, in_ip, in_username, in_password, out_ip, out_username, out_password)
-- VALUES ('Rear Door', '192.168.1.20', 'admin', 'pw', '192.168.1.21', 'admin', 'pw');

-- Disable a pair without deleting it:
-- UPDATE device_pairs SET enabled = false WHERE id = 1;
