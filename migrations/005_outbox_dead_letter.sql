-- Outbox dead-letter columns are added idempotently by Store.EnsureDeadLetterColumns.
-- Keep this feature marker separate from ordered schema_version migrations: the
-- legacy v4 game-key migration must be allowed to fail and roll back independently.
CREATE TABLE IF NOT EXISTS feature_migrations(name TEXT PRIMARY KEY);
INSERT OR IGNORE INTO feature_migrations(name) VALUES('outbox_dead_letter');
CREATE INDEX IF NOT EXISTS ix_outbox_dead_letter
ON outbox(dead_lettered,dead_lettered_ms);
