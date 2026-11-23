ALTER TABLE outbox ADD COLUMN next_attempt_ms INTEGER NOT NULL DEFAULT 0;
CREATE INDEX IF NOT EXISTS ix_outbox_due ON outbox(sent,destination,next_attempt_ms);
INSERT INTO schema_version VALUES(2);
