using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WinDayFlow.Infrastructure.Persistence;

public sealed class SqliteDatabaseInitializer
{
    private const int LatestSchemaVersion = 13;

    private const string CreateMigrationTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER NOT NULL PRIMARY KEY,
            applied_at_utc TEXT NOT NULL
        );
        """;

    private const string MigrationVersion1Sql = """
        CREATE TABLE timeline_entries (
            id TEXT NOT NULL PRIMARY KEY,
            local_date TEXT NOT NULL,
            start_utc_ticks INTEGER NOT NULL,
            start_offset_minutes INTEGER NOT NULL,
            end_utc_ticks INTEGER NOT NULL,
            end_offset_minutes INTEGER NOT NULL,
            title TEXT NOT NULL CHECK (length(trim(title)) > 0),
            summary TEXT NOT NULL,
            category INTEGER NOT NULL,
            productivity INTEGER NOT NULL,
            origin INTEGER NOT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 0),
            confidence REAL NULL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
            evidence_capture_chunk_id TEXT NULL,
            evidence_artifact_path TEXT NULL,
            analysis_version TEXT NULL CHECK (
                analysis_version IS NULL OR length(trim(analysis_version)) > 0
            ),
            range_edited_at TEXT NULL,
            title_edited_at TEXT NULL,
            summary_edited_at TEXT NULL,
            category_edited_at TEXT NULL,
            productivity_edited_at TEXT NULL,
            tags_edited_at TEXT NULL,
            CHECK (end_utc_ticks > start_utc_ticks),
            CHECK (category BETWEEN 0 AND 9),
            CHECK (productivity BETWEEN 0 AND 4),
            CHECK (origin IN (0, 1)),
            CHECK (
                (evidence_capture_chunk_id IS NULL AND evidence_artifact_path IS NULL)
                OR
                (evidence_capture_chunk_id IS NOT NULL AND evidence_artifact_path IS NOT NULL)
            ),
            CHECK (
                (origin = 0
                    AND confidence IS NOT NULL
                    AND evidence_capture_chunk_id IS NOT NULL
                    AND analysis_version IS NOT NULL)
                OR
                (origin = 1
                    AND confidence IS NULL
                    AND evidence_capture_chunk_id IS NULL
                    AND analysis_version IS NULL)
            )
        );

        CREATE INDEX ix_timeline_entries_local_date_start
            ON timeline_entries(local_date, start_utc_ticks, end_utc_ticks, id);

        CREATE TABLE timeline_entry_apps (
            timeline_entry_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            application_id TEXT NOT NULL CHECK (length(trim(application_id)) > 0),
            display_name TEXT NOT NULL CHECK (length(trim(display_name)) > 0),
            duration_ticks INTEGER NOT NULL CHECK (duration_ticks >= 0),
            PRIMARY KEY (timeline_entry_id, ordinal),
            FOREIGN KEY (timeline_entry_id) REFERENCES timeline_entries(id) ON DELETE CASCADE
        );

        CREATE TABLE timeline_entry_tags (
            timeline_entry_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            value TEXT NOT NULL CHECK (length(trim(value)) > 0),
            PRIMARY KEY (timeline_entry_id, ordinal),
            FOREIGN KEY (timeline_entry_id) REFERENCES timeline_entries(id) ON DELETE CASCADE
        );
        """;

    private const string MigrationVersion2Sql = """
        CREATE TABLE app_settings (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            theme INTEGER NOT NULL CHECK (theme BETWEEN 0 AND 2),
            capture_enabled INTEGER NOT NULL CHECK (capture_enabled IN (0, 1)),
            cloud_analysis_enabled INTEGER NOT NULL CHECK (cloud_analysis_enabled IN (0, 1)),
            capture_consent_version INTEGER NULL CHECK (
                capture_consent_version IS NULL OR capture_consent_version > 0
            ),
            capture_consent_granted_at_utc TEXT NULL CHECK (
                capture_consent_granted_at_utc IS NULL
                OR length(trim(capture_consent_granted_at_utc)) > 0
            ),
            CHECK (
                (capture_consent_version IS NULL AND capture_consent_granted_at_utc IS NULL)
                OR
                (capture_consent_version IS NOT NULL AND capture_consent_granted_at_utc IS NOT NULL)
            ),
            CHECK (
                capture_enabled = 0
                OR (
                    capture_consent_version IS NOT NULL
                    AND capture_consent_granted_at_utc IS NOT NULL
                )
            )
        );

        INSERT INTO app_settings(
            id,
            theme,
            capture_enabled,
            cloud_analysis_enabled,
            capture_consent_version,
            capture_consent_granted_at_utc)
        VALUES (1, 0, 0, 0, NULL, NULL);
        """;

    private const string MigrationVersion3Sql = """
        ALTER TABLE app_settings RENAME TO app_settings_v2;

        CREATE TABLE app_settings (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            theme INTEGER NOT NULL CHECK (theme BETWEEN 0 AND 2),
            capture_enabled INTEGER NOT NULL CHECK (capture_enabled IN (0, 1)),
            cloud_analysis_enabled INTEGER NOT NULL CHECK (cloud_analysis_enabled IN (0, 1)),
            capture_consent_version INTEGER NULL CHECK (
                capture_consent_version IS NULL OR capture_consent_version > 0
            ),
            capture_consent_granted_at_utc TEXT NULL CHECK (
                capture_consent_granted_at_utc IS NULL
                OR length(trim(capture_consent_granted_at_utc)) > 0
            ),
            capture_consent_privacy_revision INTEGER NULL CHECK (
                capture_consent_privacy_revision IS NULL
                OR capture_consent_privacy_revision > 0
            ),
            evidence_retention_days INTEGER NOT NULL CHECK (
                evidence_retention_days BETWEEN 1 AND 365
            ),
            exclude_sensitive_applications INTEGER NOT NULL CHECK (
                exclude_sensitive_applications IN (0, 1)
            ),
            pause_in_remote_sessions INTEGER NOT NULL CHECK (
                pause_in_remote_sessions IN (0, 1)
            ),
            pause_during_screen_sharing INTEGER NOT NULL CHECK (
                pause_during_screen_sharing IN (0, 1)
            ),
            capture_privacy_revision INTEGER NOT NULL CHECK (
                capture_privacy_revision > 0
            ),
            CHECK (
                (capture_consent_version IS NULL
                    AND capture_consent_granted_at_utc IS NULL
                    AND capture_consent_privacy_revision IS NULL)
                OR
                (capture_consent_version IS NOT NULL
                    AND capture_consent_granted_at_utc IS NOT NULL)
            ),
            CHECK (
                capture_enabled = 0
                OR (
                    capture_consent_version = 2
                    AND capture_consent_granted_at_utc IS NOT NULL
                    AND capture_consent_privacy_revision = capture_privacy_revision
                )
            )
        );

        INSERT INTO app_settings(
            id,
            theme,
            capture_enabled,
            cloud_analysis_enabled,
            capture_consent_version,
            capture_consent_granted_at_utc,
            capture_consent_privacy_revision,
            evidence_retention_days,
            exclude_sensitive_applications,
            pause_in_remote_sessions,
            pause_during_screen_sharing,
            capture_privacy_revision)
        SELECT
            id,
            theme,
            0,
            cloud_analysis_enabled,
            capture_consent_version,
            capture_consent_granted_at_utc,
            NULL,
            30,
            1,
            1,
            1,
            1
        FROM app_settings_v2;

        DROP TABLE app_settings_v2;
        """;

    private const string MigrationVersion4Sql = """
        CREATE TABLE capture_exclusion_rules (
            settings_id INTEGER NOT NULL CHECK (settings_id = 1),
            rule_id TEXT NOT NULL CHECK (
                length(rule_id) = 36
                AND rule_id = lower(rule_id)
            ),
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            name TEXT NOT NULL CHECK (
                length(name) BETWEEN 1 AND 80
                AND name = trim(name)
            ),
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            scope INTEGER NOT NULL CHECK (scope IN (0, 1)),
            application_identity_kind INTEGER NOT NULL CHECK (
                application_identity_kind IN (0, 1, 2)
            ),
            identity_value TEXT NOT NULL CHECK (
                length(identity_value) BETWEEN 1 AND 260
                AND identity_value = trim(identity_value)
            ),
            window_title_match_kind INTEGER NULL CHECK (
                window_title_match_kind IS NULL
                OR window_title_match_kind IN (0, 1, 2)
            ),
            pattern TEXT NULL CHECK (
                pattern IS NULL
                OR (
                    length(pattern) BETWEEN 2 AND 256
                )
            ),
            revision INTEGER NOT NULL CHECK (revision > 0),
            PRIMARY KEY (settings_id, rule_id),
            UNIQUE (settings_id, ordinal),
            FOREIGN KEY (settings_id) REFERENCES app_settings(id) ON DELETE CASCADE,
            CHECK (
                (scope = 0
                    AND window_title_match_kind IS NULL
                    AND pattern IS NULL)
                OR
                (scope = 1
                    AND window_title_match_kind IS NOT NULL
                    AND pattern IS NOT NULL)
            ),
            CHECK (
                (application_identity_kind = 0
                    AND length(identity_value) BETWEEN 5 AND 260
                    AND lower(substr(identity_value, -4)) = '.exe'
                    AND instr(identity_value, char(92)) = 0
                    AND instr(identity_value, '/') = 0
                    AND instr(identity_value, ':') = 0
                    AND instr(identity_value, '*') = 0
                    AND instr(identity_value, '?') = 0)
                OR
                (application_identity_kind = 1
                    AND length(identity_value) <= 255)
                OR
                (application_identity_kind = 2
                    AND length(identity_value) = 64
                    AND identity_value NOT GLOB '*[^0-9A-F]*')
            )
        );
        """;

    private const string MigrationVersion5Sql = """
        CREATE TABLE capture_chunks (
            id TEXT NOT NULL PRIMARY KEY CHECK (
                length(id) BETWEEN 1 AND 80
                AND id = lower(id)
                AND id NOT GLOB '*[^a-z0-9_-]*'
            ),
            video_relative_path TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (
                video_relative_path = 'chunks/' || id || '/capture.mp4'
            ),
            manifest_relative_path TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (
                manifest_relative_path = 'chunks/' || id || '/manifest.json'
            ),
            start_utc_ticks INTEGER NOT NULL CHECK (start_utc_ticks >= 0),
            start_offset_minutes INTEGER NOT NULL CHECK (
                start_offset_minutes BETWEEN -840 AND 840
            ),
            end_utc_ticks INTEGER NOT NULL CHECK (end_utc_ticks > start_utc_ticks),
            end_offset_minutes INTEGER NOT NULL CHECK (
                end_offset_minutes BETWEEN -840 AND 840
            ),
            frame_count INTEGER NOT NULL CHECK (frame_count > 0),
            video_width INTEGER NOT NULL CHECK (
                video_width >= 2 AND video_width % 2 = 0
            ),
            video_height INTEGER NOT NULL CHECK (
                video_height >= 2 AND video_height % 2 = 0
            ),
            frame_rate_numerator INTEGER NOT NULL CHECK (frame_rate_numerator > 0),
            frame_rate_denominator INTEGER NOT NULL CHECK (frame_rate_denominator > 0),
            video_byte_count INTEGER NOT NULL CHECK (
                video_byte_count BETWEEN 1 AND 67108864
            ),
            persistence_generation_hex TEXT NOT NULL CHECK (
                length(persistence_generation_hex) = 16
                AND persistence_generation_hex NOT GLOB '*[^0-9A-F]*'
                AND persistence_generation_hex <> '0000000000000000'
            ),
            target_epoch_hex TEXT NOT NULL CHECK (
                length(target_epoch_hex) = 16
                AND target_epoch_hex NOT GLOB '*[^0-9A-F]*'
                AND target_epoch_hex <> '0000000000000000'
            ),
            committed_at_utc_ticks INTEGER NOT NULL CHECK (committed_at_utc_ticks >= 0),
            ingested_at_utc_ticks INTEGER NOT NULL CHECK (ingested_at_utc_ticks >= 0),
            availability INTEGER NOT NULL CHECK (availability BETWEEN 0 AND 2)
        );

        CREATE INDEX ix_capture_chunks_range
            ON capture_chunks(start_utc_ticks, end_utc_ticks, id);

        CREATE TABLE analysis_jobs (
            id TEXT NOT NULL PRIMARY KEY CHECK (
                length(id) = 36 AND id = lower(id)
            ),
            capture_chunk_id TEXT NOT NULL,
            provider_profile_id TEXT NOT NULL CHECK (
                length(provider_profile_id) = 36
                AND provider_profile_id = lower(provider_profile_id)
            ),
            provider_profile_revision INTEGER NOT NULL CHECK (
                provider_profile_revision > 0
            ),
            analysis_version TEXT NOT NULL CHECK (
                length(analysis_version) BETWEEN 1 AND 128
                AND analysis_version = trim(analysis_version)
            ),
            input_fingerprint TEXT NOT NULL CHECK (
                length(input_fingerprint) = 64
                AND input_fingerprint NOT GLOB '*[^0-9A-F]*'
            ),
            state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 9),
            attempt INTEGER NOT NULL CHECK (attempt >= 0),
            max_attempts INTEGER NOT NULL CHECK (max_attempts BETWEEN 1 AND 100),
            not_before_utc_ticks INTEGER NULL CHECK (
                not_before_utc_ticks IS NULL OR not_before_utc_ticks >= 0
            ),
            lease_owner TEXT NULL CHECK (
                lease_owner IS NULL
                OR (
                    length(lease_owner) BETWEEN 1 AND 128
                    AND lease_owner = trim(lease_owner)
                    AND instr(lease_owner, char(0)) = 0
                )
            ),
            lease_token TEXT NULL CHECK (
                lease_token IS NULL
                OR (
                    length(lease_token) = 32
                    AND lease_token NOT GLOB '*[^0-9a-f]*'
                )
            ),
            lease_expires_at_utc_ticks INTEGER NULL CHECK (
                lease_expires_at_utc_ticks IS NULL OR lease_expires_at_utc_ticks >= 0
            ),
            error_code INTEGER NOT NULL CHECK (
                error_code IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 255)
            ),
            error_detail TEXT NULL CHECK (
                error_detail IS NULL
                OR (
                    length(error_detail) <= 1000
                    AND instr(error_detail, char(0)) = 0
                )
            ),
            created_at_utc_ticks INTEGER NOT NULL CHECK (created_at_utc_ticks >= 0),
            updated_at_utc_ticks INTEGER NOT NULL CHECK (
                updated_at_utc_ticks >= created_at_utc_ticks
            ),
            completed_at_utc_ticks INTEGER NULL CHECK (
                completed_at_utc_ticks IS NULL
                OR completed_at_utc_ticks >= created_at_utc_ticks
            ),
            FOREIGN KEY (capture_chunk_id) REFERENCES capture_chunks(id) ON DELETE RESTRICT,
            UNIQUE (
                capture_chunk_id,
                provider_profile_id,
                provider_profile_revision,
                analysis_version
            ),
            CHECK (attempt <= max_attempts),
            CHECK (
                (state = 0 AND attempt = 0)
                OR state = 9
                OR (state NOT IN (0, 9) AND attempt > 0)
            ),
            CHECK (state <> 7 OR attempt < max_attempts),
            CHECK (
                (state IN (0, 7) AND not_before_utc_ticks IS NOT NULL)
                OR (state NOT IN (0, 7) AND not_before_utc_ticks IS NULL)
            ),
            CHECK (
                (state BETWEEN 1 AND 5
                    AND lease_owner IS NOT NULL
                    AND lease_token IS NOT NULL
                    AND lease_expires_at_utc_ticks IS NOT NULL
                    AND lease_expires_at_utc_ticks > updated_at_utc_ticks)
                OR
                (state NOT BETWEEN 1 AND 5
                    AND lease_owner IS NULL
                    AND lease_token IS NULL
                    AND lease_expires_at_utc_ticks IS NULL)
            ),
            CHECK (
                (state IN (7, 8) AND error_code <> 0)
                OR
                (state NOT IN (7, 8) AND error_code = 0 AND error_detail IS NULL)
            ),
            CHECK (
                (state IN (6, 8, 9) AND completed_at_utc_ticks IS NOT NULL)
                OR (state NOT IN (6, 8, 9) AND completed_at_utc_ticks IS NULL)
            )
        );

        CREATE INDEX ix_analysis_jobs_eligible
            ON analysis_jobs(state, not_before_utc_ticks, created_at_utc_ticks, id);

        CREATE INDEX ix_analysis_jobs_expired_lease
            ON analysis_jobs(state, lease_expires_at_utc_ticks, id);
        """;

    private const string MigrationVersion6Sql = """
        CREATE TABLE ai_provider_profiles (
            id TEXT NOT NULL PRIMARY KEY CHECK (
                length(id) = 36 AND id = lower(id)
            ),
            display_name TEXT NOT NULL CHECK (
                length(display_name) BETWEEN 1 AND 80
                AND display_name = trim(display_name)
                AND instr(display_name, char(0)) = 0
            ),
            kind INTEGER NOT NULL CHECK (kind = 0),
            base_endpoint TEXT NOT NULL CHECK (
                length(base_endpoint) BETWEEN 1 AND 4096
                AND base_endpoint = trim(base_endpoint)
                AND instr(base_endpoint, char(0)) = 0
            ),
            model TEXT NOT NULL CHECK (
                length(model) BETWEEN 1 AND 200
                AND model = trim(model)
                AND instr(model, char(0)) = 0
            ),
            request_timeout_ticks INTEGER NOT NULL CHECK (
                request_timeout_ticks BETWEEN 1000000 AND 6000000000
            ),
            revision INTEGER NOT NULL CHECK (revision > 0),
            is_active INTEGER NOT NULL CHECK (is_active IN (0, 1)),
            api_key_ciphertext BLOB NULL CHECK (
                api_key_ciphertext IS NULL
                OR (
                    typeof(api_key_ciphertext) = 'blob'
                    AND length(api_key_ciphertext) BETWEEN 1 AND 65536
                )
            ),
            api_key_salt BLOB NULL CHECK (
                api_key_salt IS NULL
                OR (
                    typeof(api_key_salt) = 'blob'
                    AND length(api_key_salt) = 32
                )
            ),
            api_key_protection_version INTEGER NULL CHECK (
                api_key_protection_version IS NULL
                OR api_key_protection_version = 1
            ),
            validated_revision INTEGER NULL CHECK (
                validated_revision IS NULL
                OR (validated_revision > 0 AND validated_revision <= revision)
            ),
            validated_at_utc_ticks INTEGER NULL CHECK (
                validated_at_utc_ticks IS NULL OR validated_at_utc_ticks >= 0
            ),
            created_at_utc_ticks INTEGER NOT NULL CHECK (created_at_utc_ticks >= 0),
            updated_at_utc_ticks INTEGER NOT NULL CHECK (
                updated_at_utc_ticks >= created_at_utc_ticks
            ),
            CHECK (
                (api_key_ciphertext IS NULL
                    AND api_key_salt IS NULL
                    AND api_key_protection_version IS NULL)
                OR
                (api_key_ciphertext IS NOT NULL
                    AND api_key_salt IS NOT NULL
                    AND api_key_protection_version IS NOT NULL)
            ),
            CHECK (
                (validated_revision IS NULL AND validated_at_utc_ticks IS NULL)
                OR (validated_revision IS NOT NULL AND validated_at_utc_ticks IS NOT NULL)
            )
        );

        CREATE UNIQUE INDEX ux_ai_provider_profiles_single_active
            ON ai_provider_profiles(is_active)
            WHERE is_active = 1;

        CREATE INDEX ix_analysis_jobs_provider_revision_state
            ON analysis_jobs(provider_profile_id, provider_profile_revision, state);

        UPDATE app_settings
        SET cloud_analysis_enabled = 0
        WHERE id = 1;
        """;

    private const string MigrationVersion7Sql = """
        ALTER TABLE app_settings
        ADD COLUMN capture_application_privacy_mode INTEGER NOT NULL DEFAULT 0 CHECK (
            capture_application_privacy_mode IN (0, 1)
        );
        """;

    private const string MigrationVersion8Sql = """
        ALTER TABLE app_settings
        ADD COLUMN capture_interval_seconds INTEGER NOT NULL DEFAULT 10 CHECK (
            capture_interval_seconds IN (5, 10, 15, 30, 60)
        );
        """;

    private const string MigrationVersion9Sql = """
        CREATE TABLE timeline_entry_evidence (
            timeline_entry_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            capture_chunk_id TEXT NOT NULL,
            artifact_path TEXT NOT NULL CHECK (length(trim(artifact_path)) > 0),
            contribution_start_utc_ticks INTEGER NOT NULL,
            contribution_start_offset_minutes INTEGER NOT NULL,
            contribution_end_utc_ticks INTEGER NOT NULL,
            contribution_end_offset_minutes INTEGER NOT NULL,
            PRIMARY KEY (timeline_entry_id, ordinal),
            UNIQUE (timeline_entry_id, capture_chunk_id),
            FOREIGN KEY (timeline_entry_id)
                REFERENCES timeline_entries(id) ON DELETE CASCADE,
            CHECK (contribution_end_utc_ticks > contribution_start_utc_ticks)
        );

        CREATE INDEX ix_timeline_entry_evidence_chunk
            ON timeline_entry_evidence(capture_chunk_id, timeline_entry_id);

        INSERT INTO timeline_entry_evidence(
            timeline_entry_id,
            ordinal,
            capture_chunk_id,
            artifact_path,
            contribution_start_utc_ticks,
            contribution_start_offset_minutes,
            contribution_end_utc_ticks,
            contribution_end_offset_minutes)
        SELECT
            id,
            0,
            evidence_capture_chunk_id,
            evidence_artifact_path,
            start_utc_ticks,
            start_offset_minutes,
            end_utc_ticks,
            end_offset_minutes
        FROM timeline_entries
        WHERE evidence_capture_chunk_id IS NOT NULL;

        CREATE TABLE analysis_job_window_members (
            analysis_job_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            capture_chunk_id TEXT NOT NULL,
            source_fingerprint TEXT NOT NULL CHECK (
                length(source_fingerprint) = 64
                AND source_fingerprint NOT GLOB '*[^0-9A-F]*'
            ),
            contribution_start_utc_ticks INTEGER NOT NULL,
            contribution_start_offset_minutes INTEGER NOT NULL,
            contribution_end_utc_ticks INTEGER NOT NULL,
            contribution_end_offset_minutes INTEGER NOT NULL,
            PRIMARY KEY (analysis_job_id, ordinal),
            UNIQUE (analysis_job_id, capture_chunk_id),
            FOREIGN KEY (analysis_job_id)
                REFERENCES analysis_jobs(id) ON DELETE CASCADE,
            FOREIGN KEY (capture_chunk_id)
                REFERENCES capture_chunks(id) ON DELETE RESTRICT,
            CHECK (contribution_end_utc_ticks > contribution_start_utc_ticks)
        );

        CREATE INDEX ix_analysis_job_window_members_chunk
            ON analysis_job_window_members(capture_chunk_id, analysis_job_id);

        INSERT INTO analysis_job_window_members(
            analysis_job_id,
            ordinal,
            capture_chunk_id,
            source_fingerprint,
            contribution_start_utc_ticks,
            contribution_start_offset_minutes,
            contribution_end_utc_ticks,
            contribution_end_offset_minutes)
        SELECT
            jobs.id,
            0,
            jobs.capture_chunk_id,
            jobs.input_fingerprint,
            chunks.start_utc_ticks,
            chunks.start_offset_minutes,
            chunks.end_utc_ticks,
            chunks.end_offset_minutes
        FROM analysis_jobs AS jobs
        INNER JOIN capture_chunks AS chunks ON chunks.id = jobs.capture_chunk_id;
        """;

    private const string MigrationVersion10Sql = """
        DELETE FROM timeline_entry_evidence;
        DELETE FROM timeline_entry_apps;
        DELETE FROM timeline_entry_tags;
        DELETE FROM timeline_entries;
        DELETE FROM analysis_job_window_members;
        DELETE FROM analysis_jobs;
        DELETE FROM capture_chunks;

        DROP TABLE capture_chunks;

        CREATE TABLE capture_chunks (
            id TEXT NOT NULL PRIMARY KEY CHECK (
                length(id) BETWEEN 1 AND 80
                AND id = lower(id)
                AND id NOT GLOB '*[^a-z0-9_-]*'
            ),
            manifest_relative_path TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (
                manifest_relative_path = 'chunks/' || id || '/manifest.json'
            ),
            start_utc_ticks INTEGER NOT NULL CHECK (start_utc_ticks >= 0),
            start_offset_minutes INTEGER NOT NULL CHECK (
                start_offset_minutes BETWEEN -840 AND 840
            ),
            end_utc_ticks INTEGER NOT NULL CHECK (end_utc_ticks > start_utc_ticks),
            end_offset_minutes INTEGER NOT NULL CHECK (
                end_offset_minutes BETWEEN -840 AND 840
            ),
            captured_frame_count INTEGER NOT NULL CHECK (captured_frame_count > 0),
            frame_count INTEGER NOT NULL CHECK (
                frame_count BETWEEN 1 AND 720
                AND frame_count <= captured_frame_count
            ),
            frame_width INTEGER NOT NULL CHECK (
                frame_width >= 2 AND frame_width % 2 = 0
            ),
            frame_height INTEGER NOT NULL CHECK (
                frame_height >= 2 AND frame_height % 2 = 0
            ),
            frame_byte_count INTEGER NOT NULL CHECK (
                frame_byte_count BETWEEN 1 AND 67108864
            ),
            persistence_generation_hex TEXT NOT NULL CHECK (
                length(persistence_generation_hex) = 16
                AND persistence_generation_hex NOT GLOB '*[^0-9A-F]*'
                AND persistence_generation_hex <> '0000000000000000'
            ),
            target_epoch_hex TEXT NOT NULL CHECK (
                length(target_epoch_hex) = 16
                AND target_epoch_hex NOT GLOB '*[^0-9A-F]*'
                AND target_epoch_hex <> '0000000000000000'
            ),
            committed_at_utc_ticks INTEGER NOT NULL CHECK (committed_at_utc_ticks >= 0),
            ingested_at_utc_ticks INTEGER NOT NULL CHECK (ingested_at_utc_ticks >= 0),
            availability INTEGER NOT NULL CHECK (availability BETWEEN 0 AND 2)
        );

        CREATE INDEX ix_capture_chunks_range
            ON capture_chunks(start_utc_ticks, end_utc_ticks, id);
        """;

    private const string MigrationVersion11Sql = """
        ALTER TABLE capture_chunks
        ADD COLUMN process_name TEXT NULL CHECK (
            process_name IS NULL
            OR (
                length(process_name) BETWEEN 1 AND 260
                AND process_name = trim(process_name)
                AND instr(process_name, char(0)) = 0
            )
        );

        ALTER TABLE capture_chunks
        ADD COLUMN process_id INTEGER NULL CHECK (
            process_id IS NULL OR process_id BETWEEN 1 AND 4294967295
        );

        ALTER TABLE capture_chunks
        ADD COLUMN cpu_usage_basis_points INTEGER NULL CHECK (
            cpu_usage_basis_points IS NULL
            OR cpu_usage_basis_points BETWEEN 0 AND 10000
        );

        ALTER TABLE capture_chunks
        ADD COLUMN working_set_bytes INTEGER NULL CHECK (
            working_set_bytes IS NULL OR working_set_bytes >= 0
        );

        ALTER TABLE capture_chunks
        ADD COLUMN private_memory_bytes INTEGER NULL CHECK (
            private_memory_bytes IS NULL OR private_memory_bytes >= 0
        );
        """;

    private const string MigrationVersion12Sql = """
        INSERT INTO capture_exclusion_rules(
            settings_id,
            rule_id,
            ordinal,
            name,
            enabled,
            scope,
            application_identity_kind,
            identity_value,
            window_title_match_kind,
            pattern,
            revision)
        SELECT
            1,
            'df2c2131-bfe5-4a17-bf4c-4f3378a4b093',
            COALESCE((
                SELECT MAX(ordinal)
                FROM capture_exclusion_rules
                WHERE settings_id = 1
            ), -1) + 1,
            'WinDayFlow',
            1,
            0,
            0,
            'WinDayFlow.App.exe',
            NULL,
            NULL,
            1
        WHERE EXISTS (
            SELECT 1 FROM app_settings WHERE id = 1
        )
        AND (
            SELECT COUNT(*)
            FROM capture_exclusion_rules
            WHERE settings_id = 1
        ) < 100
        AND NOT EXISTS (
            SELECT 1
            FROM capture_exclusion_rules
            WHERE settings_id = 1
              AND scope = 0
              AND application_identity_kind = 0
              AND identity_value = 'WinDayFlow.App.exe' COLLATE NOCASE
        );
        """;

    private const string MigrationVersion13Sql = """
        DELETE FROM timeline_entry_evidence;
        DELETE FROM timeline_entry_apps;
        DELETE FROM timeline_entry_tags;
        DELETE FROM timeline_entries;
        DELETE FROM analysis_job_window_members;
        DELETE FROM analysis_jobs;
        DELETE FROM capture_chunks;

        DROP TABLE analysis_job_window_members;
        DROP TABLE analysis_jobs;

        DROP INDEX IF EXISTS ux_ai_provider_profiles_single_active;

        ALTER TABLE app_settings
        ADD COLUMN capture_intent INTEGER NOT NULL DEFAULT 0 CHECK (
            capture_intent IN (0, 1, 2)
        );

        UPDATE app_settings
        SET capture_intent = CASE capture_enabled WHEN 1 THEN 2 ELSE 0 END
        WHERE id = 1;

        DROP TABLE capture_chunks;

        CREATE TABLE capture_chunks (
            id TEXT NOT NULL PRIMARY KEY CHECK (
                length(id) BETWEEN 1 AND 80
                AND id = lower(id)
                AND id NOT GLOB '*[^a-z0-9_-]*'
            ),
            manifest_relative_path TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (
                manifest_relative_path = 'chunks/' || id || '/manifest.json'
            ),
            start_utc_ticks INTEGER NOT NULL CHECK (start_utc_ticks >= 0),
            start_offset_minutes INTEGER NOT NULL CHECK (
                start_offset_minutes BETWEEN -840 AND 840
            ),
            end_utc_ticks INTEGER NOT NULL CHECK (end_utc_ticks > start_utc_ticks),
            end_offset_minutes INTEGER NOT NULL CHECK (
                end_offset_minutes BETWEEN -840 AND 840
            ),
            captured_frame_count INTEGER NOT NULL CHECK (captured_frame_count > 0),
            frame_count INTEGER NOT NULL CHECK (
                frame_count BETWEEN 0 AND 720
                AND frame_count <= captured_frame_count
            ),
            frame_width INTEGER NOT NULL CHECK (
                frame_width >= 2 AND frame_width % 2 = 0
            ),
            frame_height INTEGER NOT NULL CHECK (
                frame_height >= 2 AND frame_height % 2 = 0
            ),
            frame_byte_count INTEGER NOT NULL CHECK (
                frame_byte_count BETWEEN 0 AND 67108864
                AND ((frame_count = 0 AND frame_byte_count = 0)
                    OR (frame_count > 0 AND frame_byte_count > 0))
            ),
            persistence_generation_hex TEXT NOT NULL CHECK (
                length(persistence_generation_hex) = 16
                AND persistence_generation_hex NOT GLOB '*[^0-9A-F]*'
                AND persistence_generation_hex <> '0000000000000000'
            ),
            target_epoch_hex TEXT NOT NULL CHECK (
                length(target_epoch_hex) = 16
                AND target_epoch_hex NOT GLOB '*[^0-9A-F]*'
                AND target_epoch_hex <> '0000000000000000'
            ),
            committed_at_utc_ticks INTEGER NOT NULL CHECK (committed_at_utc_ticks >= 0),
            ingested_at_utc_ticks INTEGER NOT NULL CHECK (ingested_at_utc_ticks >= 0),
            availability INTEGER NOT NULL CHECK (availability BETWEEN 0 AND 2),
            process_name TEXT NULL CHECK (
                process_name IS NULL
                OR (
                    length(process_name) BETWEEN 1 AND 260
                    AND process_name = trim(process_name)
                    AND instr(process_name, char(0)) = 0
                )
            ),
            process_id INTEGER NULL CHECK (
                process_id IS NULL OR process_id BETWEEN 1 AND 4294967295
            ),
            cpu_usage_basis_points INTEGER NULL CHECK (
                cpu_usage_basis_points IS NULL
                OR cpu_usage_basis_points BETWEEN 0 AND 10000
            ),
            working_set_bytes INTEGER NULL CHECK (
                working_set_bytes IS NULL OR working_set_bytes >= 0
            ),
            private_memory_bytes INTEGER NULL CHECK (
                private_memory_bytes IS NULL OR private_memory_bytes >= 0
            ),
            black_frame_count INTEGER NOT NULL CHECK (
                black_frame_count >= 0
                AND black_frame_count <= captured_frame_count
            ),
            duplicate_frame_count INTEGER NOT NULL CHECK (
                duplicate_frame_count >= 0
                AND duplicate_frame_count <= captured_frame_count
                AND black_frame_count + duplicate_frame_count + frame_count
                    = captured_frame_count
            )
        );

        CREATE INDEX ix_capture_chunks_range
            ON capture_chunks(start_utc_ticks, end_utc_ticks, id);

        CREATE TABLE analysis_jobs (
            id TEXT NOT NULL PRIMARY KEY CHECK (
                length(id) = 36 AND id = lower(id)
            ),
            capture_chunk_id TEXT NOT NULL,
            provider_profile_id TEXT NOT NULL CHECK (
                length(provider_profile_id) = 36
                AND provider_profile_id = lower(provider_profile_id)
            ),
            provider_profile_revision INTEGER NOT NULL CHECK (
                provider_profile_revision > 0
            ),
            analysis_version TEXT NOT NULL CHECK (
                length(analysis_version) BETWEEN 1 AND 128
                AND analysis_version = trim(analysis_version)
            ),
            input_fingerprint TEXT NOT NULL CHECK (
                length(input_fingerprint) = 64
                AND input_fingerprint NOT GLOB '*[^0-9A-F]*'
            ),
            state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 9),
            attempt INTEGER NOT NULL CHECK (attempt >= 0),
            max_attempts INTEGER NOT NULL CHECK (max_attempts BETWEEN 1 AND 100),
            not_before_utc_ticks INTEGER NULL CHECK (
                not_before_utc_ticks IS NULL OR not_before_utc_ticks >= 0
            ),
            lease_owner TEXT NULL CHECK (
                lease_owner IS NULL
                OR (
                    length(lease_owner) BETWEEN 1 AND 128
                    AND lease_owner = trim(lease_owner)
                    AND instr(lease_owner, char(0)) = 0
                )
            ),
            lease_token TEXT NULL CHECK (
                lease_token IS NULL
                OR (
                    length(lease_token) = 32
                    AND lease_token NOT GLOB '*[^0-9a-f]*'
                )
            ),
            lease_expires_at_utc_ticks INTEGER NULL CHECK (
                lease_expires_at_utc_ticks IS NULL OR lease_expires_at_utc_ticks >= 0
            ),
            error_code INTEGER NOT NULL CHECK (
                error_code IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 255)
            ),
            error_detail TEXT NULL CHECK (
                error_detail IS NULL
                OR (
                    length(error_detail) <= 1000
                    AND instr(error_detail, char(0)) = 0
                )
            ),
            created_at_utc_ticks INTEGER NOT NULL CHECK (created_at_utc_ticks >= 0),
            updated_at_utc_ticks INTEGER NOT NULL CHECK (
                updated_at_utc_ticks >= created_at_utc_ticks
            ),
            completed_at_utc_ticks INTEGER NULL CHECK (
                completed_at_utc_ticks IS NULL
                OR completed_at_utc_ticks >= created_at_utc_ticks
            ),
            FOREIGN KEY (capture_chunk_id) REFERENCES capture_chunks(id) ON DELETE RESTRICT,
            UNIQUE (
                capture_chunk_id,
                provider_profile_id,
                provider_profile_revision,
                analysis_version,
                input_fingerprint
            ),
            CHECK (attempt <= max_attempts),
            CHECK (
                (state = 0 AND attempt = 0)
                OR state = 9
                OR (state NOT IN (0, 9) AND attempt > 0)
            ),
            CHECK (state <> 7 OR attempt < max_attempts),
            CHECK (
                (state IN (0, 7) AND not_before_utc_ticks IS NOT NULL)
                OR (state NOT IN (0, 7) AND not_before_utc_ticks IS NULL)
            ),
            CHECK (
                (state BETWEEN 1 AND 5
                    AND lease_owner IS NOT NULL
                    AND lease_token IS NOT NULL
                    AND lease_expires_at_utc_ticks IS NOT NULL
                    AND lease_expires_at_utc_ticks > updated_at_utc_ticks)
                OR
                (state NOT BETWEEN 1 AND 5
                    AND lease_owner IS NULL
                    AND lease_token IS NULL
                    AND lease_expires_at_utc_ticks IS NULL)
            ),
            CHECK (
                (state IN (7, 8) AND error_code <> 0)
                OR
                (state NOT IN (7, 8) AND error_code = 0 AND error_detail IS NULL)
            ),
            CHECK (
                (state IN (6, 8, 9) AND completed_at_utc_ticks IS NOT NULL)
                OR (state NOT IN (6, 8, 9) AND completed_at_utc_ticks IS NULL)
            )
        );

        CREATE INDEX ix_analysis_jobs_eligible
            ON analysis_jobs(state, not_before_utc_ticks, created_at_utc_ticks, id);

        CREATE INDEX ix_analysis_jobs_expired_lease
            ON analysis_jobs(state, lease_expires_at_utc_ticks, id);

        CREATE INDEX ix_analysis_jobs_provider_revision_state
            ON analysis_jobs(provider_profile_id, provider_profile_revision, state);

        CREATE TABLE analysis_job_window_members (
            analysis_job_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            capture_chunk_id TEXT NOT NULL,
            source_fingerprint TEXT NOT NULL CHECK (
                length(source_fingerprint) = 64
                AND source_fingerprint NOT GLOB '*[^0-9A-F]*'
            ),
            contribution_start_utc_ticks INTEGER NOT NULL,
            contribution_start_offset_minutes INTEGER NOT NULL,
            contribution_end_utc_ticks INTEGER NOT NULL,
            contribution_end_offset_minutes INTEGER NOT NULL,
            PRIMARY KEY (analysis_job_id, ordinal),
            UNIQUE (analysis_job_id, capture_chunk_id),
            FOREIGN KEY (analysis_job_id)
                REFERENCES analysis_jobs(id) ON DELETE CASCADE,
            FOREIGN KEY (capture_chunk_id)
                REFERENCES capture_chunks(id) ON DELETE RESTRICT,
            CHECK (contribution_end_utc_ticks > contribution_start_utc_ticks)
        );

        CREATE INDEX ix_analysis_job_window_members_chunk
            ON analysis_job_window_members(capture_chunk_id, analysis_job_id);

        CREATE TABLE analysis_stage_bindings (
            stage INTEGER NOT NULL PRIMARY KEY CHECK (stage IN (0, 1)),
            provider_profile_id TEXT NULL CHECK (
                provider_profile_id IS NULL
                OR (length(provider_profile_id) = 36 AND provider_profile_id = lower(provider_profile_id))
            ),
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            route_revision INTEGER NOT NULL CHECK (route_revision > 0),
            options_json TEXT NOT NULL CHECK (
                length(options_json) BETWEEN 2 AND 4096
                AND json_valid(options_json)
            ),
            updated_at_utc_ticks INTEGER NOT NULL CHECK (updated_at_utc_ticks >= 0),
            FOREIGN KEY (provider_profile_id)
                REFERENCES ai_provider_profiles(id) ON DELETE RESTRICT,
            CHECK (enabled = 0 OR provider_profile_id IS NOT NULL)
        );

        INSERT INTO analysis_stage_bindings(
            stage,
            provider_profile_id,
            enabled,
            route_revision,
            options_json,
            updated_at_utc_ticks)
        VALUES (
            0,
            NULL,
            0,
            1,
            '{"onMatch":1,"onError":0}',
            (CAST(strftime('%s', 'now') AS INTEGER) * 10000000) + 621355968000000000
        );

        INSERT INTO analysis_stage_bindings(
            stage,
            provider_profile_id,
            enabled,
            route_revision,
            options_json,
            updated_at_utc_ticks)
        SELECT
            1,
            (SELECT id FROM ai_provider_profiles WHERE is_active = 1 LIMIT 1),
            CASE
                WHEN cloud_analysis_enabled = 1
                    AND EXISTS (SELECT 1 FROM ai_provider_profiles WHERE is_active = 1)
                THEN 1
                ELSE 0
            END,
            1,
            '{}',
            (CAST(strftime('%s', 'now') AS INTEGER) * 10000000) + 621355968000000000
        FROM app_settings
        WHERE id = 1;

        UPDATE ai_provider_profiles SET is_active = 0;
        UPDATE app_settings SET cloud_analysis_enabled = 0 WHERE id = 1;

        CREATE TABLE provider_profile_validations (
            provider_profile_id TEXT NOT NULL,
            provider_profile_revision INTEGER NOT NULL CHECK (provider_profile_revision > 0),
            stage INTEGER NOT NULL CHECK (stage IN (0, 1)),
            validated_at_utc_ticks INTEGER NOT NULL CHECK (validated_at_utc_ticks >= 0),
            PRIMARY KEY (provider_profile_id, provider_profile_revision, stage),
            FOREIGN KEY (provider_profile_id)
                REFERENCES ai_provider_profiles(id) ON DELETE CASCADE
        );

        INSERT INTO provider_profile_validations(
            provider_profile_id,
            provider_profile_revision,
            stage,
            validated_at_utc_ticks)
        SELECT
            id,
            revision,
            1,
            validated_at_utc_ticks
        FROM ai_provider_profiles
        WHERE validated_revision = revision
            AND validated_at_utc_ticks IS NOT NULL;

        CREATE TABLE privacy_screenings (
            id TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 36 AND id = lower(id)),
            capture_chunk_id TEXT NOT NULL,
            provider_profile_id TEXT NOT NULL,
            provider_profile_revision INTEGER NOT NULL CHECK (provider_profile_revision > 0),
            route_revision INTEGER NOT NULL CHECK (route_revision > 0),
            input_fingerprint TEXT NOT NULL CHECK (
                length(input_fingerprint) = 64
                AND input_fingerprint NOT GLOB '*[^0-9A-F]*'
            ),
            state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 7),
            verdict INTEGER NULL CHECK (verdict IS NULL OR verdict BETWEEN 0 AND 2),
            result_json TEXT NULL CHECK (result_json IS NULL OR json_valid(result_json)),
            derivative_manifest_relative_path TEXT NULL CHECK (
                derivative_manifest_relative_path IS NULL
                OR derivative_manifest_relative_path LIKE 'screenings/%/manifest.json'
            ),
            output_fingerprint TEXT NULL CHECK (
                output_fingerprint IS NULL
                OR (
                    length(output_fingerprint) = 64
                    AND output_fingerprint NOT GLOB '*[^0-9A-F]*'
                )
            ),
            attempt INTEGER NOT NULL DEFAULT 0 CHECK (attempt BETWEEN 0 AND 100),
            error_code INTEGER NULL,
            revision INTEGER NOT NULL CHECK (revision > 0),
            created_at_utc_ticks INTEGER NOT NULL CHECK (created_at_utc_ticks >= 0),
            updated_at_utc_ticks INTEGER NOT NULL CHECK (updated_at_utc_ticks >= created_at_utc_ticks),
            FOREIGN KEY (capture_chunk_id) REFERENCES capture_chunks(id) ON DELETE CASCADE,
            FOREIGN KEY (provider_profile_id) REFERENCES ai_provider_profiles(id) ON DELETE RESTRICT,
            UNIQUE (
                capture_chunk_id,
                provider_profile_id,
                provider_profile_revision,
                route_revision,
                input_fingerprint
            )
        );

        CREATE INDEX ix_privacy_screenings_state
            ON privacy_screenings(state, updated_at_utc_ticks, id);

        CREATE INDEX ix_privacy_screenings_output
            ON privacy_screenings(capture_chunk_id, output_fingerprint)
            WHERE output_fingerprint IS NOT NULL;

        CREATE TABLE provider_invocations (
            id TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 36 AND id = lower(id)),
            stage INTEGER NOT NULL CHECK (stage IN (0, 1)),
            provider_profile_id TEXT NOT NULL,
            provider_profile_revision INTEGER NOT NULL CHECK (provider_profile_revision > 0),
            route_revision INTEGER NOT NULL CHECK (route_revision > 0),
            endpoint_origin TEXT NOT NULL CHECK (
                length(endpoint_origin) BETWEEN 1 AND 512
                AND endpoint_origin = trim(endpoint_origin)
            ),
            evidence_fingerprint TEXT NOT NULL CHECK (
                length(evidence_fingerprint) = 64
                AND evidence_fingerprint NOT GLOB '*[^0-9A-F]*'
            ),
            item_count INTEGER NOT NULL CHECK (item_count BETWEEN 0 AND 256),
            byte_count INTEGER NOT NULL CHECK (byte_count >= 0),
            outcome INTEGER NOT NULL CHECK (outcome BETWEEN 0 AND 4),
            correlation_id TEXT NOT NULL CHECK (length(correlation_id) = 36),
            started_at_utc_ticks INTEGER NOT NULL CHECK (started_at_utc_ticks >= 0),
            completed_at_utc_ticks INTEGER NULL CHECK (
                completed_at_utc_ticks IS NULL OR completed_at_utc_ticks >= started_at_utc_ticks
            ),
            input_tokens INTEGER NULL CHECK (input_tokens IS NULL OR input_tokens >= 0),
            output_tokens INTEGER NULL CHECK (output_tokens IS NULL OR output_tokens >= 0),
            FOREIGN KEY (provider_profile_id) REFERENCES ai_provider_profiles(id) ON DELETE RESTRICT
        );

        CREATE INDEX ix_provider_invocations_started
            ON provider_invocations(started_at_utc_ticks, stage, outcome);

        CREATE TABLE evidence_send_overrides (
            id TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 36 AND id = lower(id)),
            capture_chunk_id TEXT NOT NULL,
            stage INTEGER NOT NULL CHECK (stage IN (0, 1)),
            provider_profile_id TEXT NOT NULL,
            provider_profile_revision INTEGER NOT NULL CHECK (provider_profile_revision > 0),
            route_revision INTEGER NOT NULL CHECK (route_revision > 0),
            evidence_fingerprint TEXT NOT NULL CHECK (length(evidence_fingerprint) = 64),
            logical_operation_id TEXT NOT NULL CHECK (length(logical_operation_id) = 36),
            remaining_uses INTEGER NOT NULL CHECK (remaining_uses BETWEEN 0 AND 20),
            created_at_utc_ticks INTEGER NOT NULL CHECK (created_at_utc_ticks >= 0),
            expires_at_utc_ticks INTEGER NOT NULL CHECK (
                expires_at_utc_ticks > created_at_utc_ticks
            ),
            last_consumed_at_utc_ticks INTEGER NULL CHECK (
                last_consumed_at_utc_ticks IS NULL
                OR last_consumed_at_utc_ticks >= created_at_utc_ticks
            ),
            FOREIGN KEY (capture_chunk_id) REFERENCES capture_chunks(id) ON DELETE CASCADE,
            FOREIGN KEY (provider_profile_id) REFERENCES ai_provider_profiles(id) ON DELETE RESTRICT
        );

        CREATE TABLE application_catalog (
            application_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(application_id) BETWEEN 1 AND 320
                AND application_id = trim(application_id)
            ),
            identity_kind INTEGER NOT NULL CHECK (identity_kind BETWEEN 0 AND 2),
            identity_value TEXT NOT NULL CHECK (
                length(identity_value) BETWEEN 1 AND 260
                AND identity_value = trim(identity_value)
            ),
            display_name TEXT NOT NULL CHECK (
                length(display_name) BETWEEN 1 AND 160
                AND display_name = trim(display_name)
            ),
            icon_cache_key TEXT NULL CHECK (
                icon_cache_key IS NULL
                OR (length(icon_cache_key) = 64 AND icon_cache_key NOT GLOB '*[^0-9A-F]*')
            ),
            first_seen_utc_ticks INTEGER NOT NULL CHECK (first_seen_utc_ticks >= 0),
            last_seen_utc_ticks INTEGER NOT NULL CHECK (last_seen_utc_ticks >= first_seen_utc_ticks)
        );

        CREATE TABLE capture_context_samples (
            capture_chunk_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            sampled_at_utc_ticks INTEGER NOT NULL CHECK (sampled_at_utc_ticks >= 0),
            application_id TEXT NULL,
            process_id INTEGER NULL CHECK (process_id IS NULL OR process_id BETWEEN 1 AND 4294967295),
            cpu_usage_basis_points INTEGER NULL CHECK (
                cpu_usage_basis_points IS NULL OR cpu_usage_basis_points BETWEEN 0 AND 10000
            ),
            working_set_bytes INTEGER NULL CHECK (working_set_bytes IS NULL OR working_set_bytes >= 0),
            private_memory_bytes INTEGER NULL CHECK (private_memory_bytes IS NULL OR private_memory_bytes >= 0),
            evaluated_rule_set_revision INTEGER NULL CHECK (
                evaluated_rule_set_revision IS NULL OR evaluated_rule_set_revision > 0
            ),
            application_context_available INTEGER NOT NULL DEFAULT 0 CHECK (
                application_context_available IN (0, 1)
            ),
            window_context_available INTEGER NOT NULL DEFAULT 0 CHECK (
                window_context_available IN (0, 1)
            ),
            CHECK (
                evaluated_rule_set_revision IS NOT NULL
                OR (application_context_available = 0 AND window_context_available = 0)
            ),
            PRIMARY KEY (capture_chunk_id, ordinal),
            FOREIGN KEY (capture_chunk_id) REFERENCES capture_chunks(id) ON DELETE CASCADE,
            FOREIGN KEY (application_id) REFERENCES application_catalog(application_id) ON DELETE SET NULL
        );

        CREATE TABLE capture_context_rule_matches (
            capture_chunk_id TEXT NOT NULL,
            sample_ordinal INTEGER NOT NULL,
            rule_id TEXT NOT NULL CHECK (length(rule_id) = 36 AND rule_id = lower(rule_id)),
            rule_revision INTEGER NOT NULL CHECK (rule_revision > 0),
            PRIMARY KEY (capture_chunk_id, sample_ordinal, rule_id),
            FOREIGN KEY (capture_chunk_id, sample_ordinal)
                REFERENCES capture_context_samples(capture_chunk_id, ordinal) ON DELETE CASCADE
        );

        CREATE TABLE app_installation (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            first_started_at_utc_ticks INTEGER NOT NULL CHECK (first_started_at_utc_ticks >= 0)
        );

        INSERT INTO app_installation(id, first_started_at_utc_ticks)
        VALUES (
            1,
            (CAST(strftime('%s', 'now') AS INTEGER) * 10000000) + 621355968000000000
        );

        CREATE TABLE development_data_resets (
            schema_version INTEGER NOT NULL PRIMARY KEY,
            cleanup_completed INTEGER NOT NULL CHECK (cleanup_completed IN (0, 1)),
            completed_at_utc_ticks INTEGER NULL CHECK (completed_at_utc_ticks IS NULL OR completed_at_utc_ticks >= 0)
        );

        INSERT INTO development_data_resets(schema_version, cleanup_completed, completed_at_utc_ticks)
        VALUES (13, 0, NULL);
        """;

    private static readonly IReadOnlyList<Migration> Migrations =
    [
        new(1, MigrationVersion1Sql),
        new(2, MigrationVersion2Sql),
        new(3, MigrationVersion3Sql),
        new(4, MigrationVersion4Sql),
        new(5, MigrationVersion5Sql),
        new(6, MigrationVersion6Sql),
        new(7, MigrationVersion7Sql),
        new(8, MigrationVersion8Sql),
        new(9, MigrationVersion9Sql),
        new(10, MigrationVersion10Sql),
        new(11, MigrationVersion11Sql),
        new(12, MigrationVersion12Sql),
        new(13, MigrationVersion13Sql),
    ];

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqliteDatabaseInitializer(
        SqliteConnectionFactory connectionFactory,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(
                connection,
                transaction: null,
                CreateMigrationTableSql,
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: false);

        var appliedVersions = await ReadAppliedVersionsAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        var unsupportedVersion = appliedVersions.FirstOrDefault(
            static version => version > LatestSchemaVersion);
        if (unsupportedVersion > 0)
        {
            throw new InvalidOperationException(
                $"Database schema version {unsupportedVersion} is newer than supported version {LatestSchemaVersion}.");
        }

        foreach (var migration in Migrations.OrderBy(static migration => migration.Version))
        {
            if (appliedVersions.Contains(migration.Version))
            {
                continue;
            }

            await ExecuteAsync(
                    connection,
                    transaction,
                    migration.Sql,
                    cancellationToken)
                .ConfigureAwait(false);

            await RecordMigrationAsync(
                    connection,
                    transaction,
                    migration.Version,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchema13CaptureContextShapeAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        await CompletePendingDevelopmentResetAsync(connection, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureSchema13CaptureContextShapeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "PRAGMA table_info(capture_context_samples);";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }
        }

        var additions = new[]
        {
            (
                Name: "evaluated_rule_set_revision",
                Sql: "ALTER TABLE capture_context_samples ADD COLUMN evaluated_rule_set_revision INTEGER NULL CHECK (evaluated_rule_set_revision IS NULL OR evaluated_rule_set_revision > 0);"),
            (
                Name: "application_context_available",
                Sql: "ALTER TABLE capture_context_samples ADD COLUMN application_context_available INTEGER NOT NULL DEFAULT 0 CHECK (application_context_available IN (0, 1));"),
            (
                Name: "window_context_available",
                Sql: "ALTER TABLE capture_context_samples ADD COLUMN window_context_available INTEGER NOT NULL DEFAULT 0 CHECK (window_context_available IN (0, 1));"),
        };
        foreach (var addition in additions)
        {
            if (!columns.Contains(addition.Name))
            {
                await ExecuteAsync(
                        connection,
                        transaction,
                        addition.Sql,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompletePendingDevelopmentResetAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT cleanup_completed
            FROM development_data_resets
            WHERE schema_version = 13;
            """;
        var result = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull || Convert.ToInt32(result, CultureInfo.InvariantCulture) != 0)
        {
            return;
        }

        var dataRoot = Path.GetDirectoryName(_connectionFactory.DatabasePath)
            ?? throw new InvalidOperationException("The database path has no data root.");
        foreach (var directoryName in new[] { ".staging", "chunks", "screenings", "cache", "exports" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Path.GetFullPath(Path.Combine(dataRoot, directoryName));
            var expected = Path.Combine(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)),
                directoryName);
            if (!string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The development reset target escaped the data root.");
            }

            try
            {
                if (Directory.Exists(candidate))
                {
                    Directory.Delete(candidate, recursive: true);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Another initializer completed the same one-time reset.
            }
        }

        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE development_data_resets
            SET cleanup_completed = 1,
                completed_at_utc_ticks = $completed_at_utc_ticks
            WHERE schema_version = 13 AND cleanup_completed = 0;
            """;
        update.Parameters.AddWithValue(
            "$completed_at_utc_ticks",
            _timeProvider.GetUtcNow().ToUniversalTime().Ticks);
        _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<int>> ReadAppliedVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";

        var versions = new HashSet<int>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private async Task RecordMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES ($version, $applied_at_utc);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue(
            "$applied_at_utc",
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record Migration(int Version, string Sql);
}
