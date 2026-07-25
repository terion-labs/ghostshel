namespace GhostShell.Infrastructure;

internal static class SqliteSchema
{
    public static IReadOnlyList<SqliteMigration> Migrations { get; } =
    [
        new(
            1,
            "durable-definitions-and-recovery",
            """
            CREATE TABLE definitions (
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                schema_version INTEGER NOT NULL CHECK (schema_version > 0),
                revision INTEGER NOT NULL CHECK (revision > 0),
                name TEXT NOT NULL,
                payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (kind, id)
            ) WITHOUT ROWID;

            CREATE INDEX definitions_name_idx
                ON definitions(kind, name COLLATE NOCASE);

            CREATE TABLE definition_references (
                owner_kind TEXT NOT NULL,
                owner_id TEXT NOT NULL,
                target_kind TEXT NOT NULL,
                target_id TEXT NOT NULL,
                role TEXT NOT NULL,
                PRIMARY KEY (owner_kind, owner_id, target_kind, target_id, role),
                FOREIGN KEY (owner_kind, owner_id)
                    REFERENCES definitions(kind, id)
                    ON DELETE CASCADE
                    DEFERRABLE INITIALLY DEFERRED
            ) WITHOUT ROWID;

            CREATE INDEX definition_references_target_idx
                ON definition_references(target_kind, target_id);

            CREATE TABLE app_lifecycle (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                clean_shutdown INTEGER NOT NULL CHECK (clean_shutdown IN (0, 1)),
                current_run_id TEXT,
                started_utc TEXT,
                last_clean_utc TEXT,
                CHECK (
                    (clean_shutdown = 1 AND current_run_id IS NULL AND started_utc IS NULL)
                    OR
                    (clean_shutdown = 0 AND current_run_id IS NOT NULL AND started_utc IS NOT NULL)
                )
            );

            INSERT INTO app_lifecycle(
                singleton_id,
                clean_shutdown,
                current_run_id,
                started_utc,
                last_clean_utc)
            VALUES (1, 1, NULL, NULL, NULL);

            CREATE TABLE runtime_snapshots (
                run_id TEXT NOT NULL,
                snapshot_key TEXT NOT NULL,
                schema_version INTEGER NOT NULL CHECK (schema_version > 0),
                payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (run_id, snapshot_key)
            ) WITHOUT ROWID;

            CREATE INDEX runtime_snapshots_updated_idx
                ON runtime_snapshots(updated_utc);

            CREATE TABLE audit_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                correlation_id TEXT NOT NULL,
                actor_kind TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                action TEXT NOT NULL,
                target_kind TEXT,
                target_id TEXT,
                outcome TEXT NOT NULL,
                details_json TEXT NOT NULL CHECK (json_valid(details_json)),
                occurred_utc TEXT NOT NULL
            );

            CREATE INDEX audit_events_correlation_idx
                ON audit_events(correlation_id, sequence);
            """),
        new(
            2,
            "bounded-recent-session-history",
            """
            CREATE TABLE recent_sessions (
                session_id TEXT PRIMARY KEY CHECK (length(session_id) > 0),
                definition_kind TEXT NOT NULL CHECK (length(definition_kind) > 0),
                definition_id TEXT NOT NULL CHECK (length(definition_id) > 0),
                panel_kind TEXT NOT NULL CHECK (
                    panel_kind IN ('Terminal', 'Browser', 'FileViewer', 'Statistics', 'ProcessMonitor')),
                title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
                started_utc TEXT NOT NULL,
                ended_utc TEXT,
                outcome TEXT NOT NULL CHECK (
                    outcome IN (
                        'Active',
                        'GracefullyClosed',
                        'ForceTerminated',
                        'Failed',
                        'Cancelled',
                        'Interrupted')),
                CHECK (
                    (outcome = 'Active' AND ended_utc IS NULL)
                    OR
                    (outcome <> 'Active' AND ended_utc IS NOT NULL)),
                CHECK (ended_utc IS NULL OR ended_utc >= started_utc)
            ) WITHOUT ROWID;

            CREATE INDEX recent_sessions_recency_idx
                ON recent_sessions(
                    COALESCE(ended_utc, started_utc) DESC,
                    started_utc DESC,
                    session_id);
            """),
        new(
            3,
            "revisioned-recent-session-retention",
            """
            CREATE TABLE recent_session_retention (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                revision INTEGER NOT NULL CHECK (revision > 0),
                maximum_entries INTEGER NOT NULL CHECK (
                    maximum_entries BETWEEN 0 AND 1000),
                maximum_age_ticks INTEGER NOT NULL CHECK (
                    maximum_age_ticks BETWEEN 1 AND 315360000000000)
            );

            INSERT INTO recent_session_retention(
                singleton_id,
                revision,
                maximum_entries,
                maximum_age_ticks)
            VALUES (1, 1, 100, 25920000000000);
            """),
        new(
            4,
            "versioned-first-run-progress",
            """
            CREATE TABLE onboarding_progress (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                completed_version INTEGER NOT NULL CHECK (completed_version >= 0),
                revision INTEGER NOT NULL CHECK (revision > 0)
            );

            INSERT INTO onboarding_progress(
                singleton_id,
                completed_version,
                revision)
            SELECT
                1,
                CASE
                    WHEN EXISTS (SELECT 1 FROM definitions)
                        OR EXISTS (SELECT 1 FROM recent_sessions)
                        OR EXISTS (SELECT 1 FROM audit_events)
                        OR EXISTS (
                            SELECT 1
                            FROM app_lifecycle
                            WHERE singleton_id = 1
                                AND last_clean_utc IS NOT NULL)
                    THEN 1
                    ELSE 0
                END,
                1;
            """),
        new(
            5,
            "durable-agent-action-audit-state",
            """
            CREATE TABLE agent_action_audit_state (
                action_id TEXT PRIMARY KEY CHECK (length(action_id) BETWEEN 1 AND 256),
                phase TEXT NOT NULL CHECK (
                    phase IN (
                        'Requested',
                        'Approved',
                        'Started',
                        'Succeeded',
                        'Denied',
                        'Failed',
                        'Cancelled')),
                last_event_id TEXT NOT NULL UNIQUE,
                updated_utc TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE INDEX agent_action_audit_state_phase_idx
                ON agent_action_audit_state(phase, updated_utc, action_id);

            INSERT INTO agent_action_audit_state(
                action_id, phase, last_event_id, updated_utc)
            SELECT
                event.correlation_id,
                event.outcome,
                event.event_id,
                event.occurred_utc
            FROM audit_events AS event
            WHERE json_extract(event.details_json, '$.kind') = 'agent-action'
              AND event.sequence = (
                    SELECT MAX(candidate.sequence)
                    FROM audit_events AS candidate
                    WHERE candidate.correlation_id = event.correlation_id
                      AND json_extract(candidate.details_json, '$.kind') = 'agent-action');
            """),
        new(
            6,
            "indexed-agent-run-audit-reading",
            """
            CREATE INDEX audit_events_agent_run_idx
                ON audit_events(
                    json_extract(details_json, '$.runId'),
                    sequence DESC)
                WHERE json_extract(details_json, '$.kind') IN (
                    'agent-action',
                    'agent-run-policy-transition');
            """),
    ];
}
