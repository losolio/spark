-- Spark PostgreSQL store: initial schema.
-- Every statement is idempotent so the script can be re-run safely.

-- Logical resources, one row per type and id. The id column is the surrogate key that the
-- resources table and the search index tables refer to, and last_version is the counter that
-- version ids are allocated from.
CREATE TABLE IF NOT EXISTS resource_keys (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    type          text   NOT NULL,
    resource_id   text   NOT NULL,
    last_version  bigint NOT NULL,
    CONSTRAINT resource_keys_logical_key UNIQUE (type, resource_id)
);

-- FHIR resources, one row per version. The current version of a resource has
-- state = 'current', older versions are 'superseded'. Deleted versions have a
-- NULL body and method = 'DELETE'. type and resource_id are copies of the
-- resource_keys columns so that reads and history do not need a join.
CREATE TABLE IF NOT EXISTS resources (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    resource_key  bigint      NOT NULL REFERENCES resource_keys (id),
    type          text        NOT NULL,
    resource_id   text        NOT NULL,
    version_id    text        NOT NULL,
    state         text        NOT NULL,
    method        text        NOT NULL,
    updated_at    timestamptz NOT NULL,
    body          jsonb,
    CONSTRAINT resources_version_key UNIQUE (resource_key, version_id)
);

-- At most one current version per logical resource.
CREATE UNIQUE INDEX IF NOT EXISTS resources_current_idx
    ON resources (resource_key)
    WHERE state = 'current';

-- History: system-wide and per resource type, newest first.
CREATE INDEX IF NOT EXISTS resources_updated_at_idx
    ON resources (updated_at DESC, id DESC);

CREATE INDEX IF NOT EXISTS resources_type_updated_at_idx
    ON resources (type, updated_at DESC, id DESC);

-- Search and history result snapshots used for paging.
CREATE TABLE IF NOT EXISTS snapshots (
    id                text        PRIMARY KEY,
    type              text        NOT NULL,
    feed_self_link    text,
    keys              text[]      NOT NULL,
    count             integer     NOT NULL,
    count_param       integer,
    is_count_only     boolean     NOT NULL,
    sort_by           text,
    includes          text[],
    reverse_includes  text[],
    elements          text[],
    created_at        timestamptz NOT NULL
);

-- Durable outbox queue for background indexing.
CREATE TABLE IF NOT EXISTS index_queue (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    type              text        NOT NULL,
    resource_id       text        NOT NULL,
    version_id        text        NOT NULL,
    method            text        NOT NULL,
    updated_at        timestamptz NOT NULL,
    body              jsonb,
    status            text        NOT NULL,
    worker_id         text,
    claimed_at        timestamptz,
    lease_expires_at  timestamptz,
    attempts          integer     NOT NULL DEFAULT 0,
    last_error        text,
    enqueued_at       timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS index_queue_claim_idx
    ON index_queue (status, enqueued_at, id);

CREATE INDEX IF NOT EXISTS index_queue_lease_idx
    ON index_queue (status, lease_expires_at);

-- Data migrations tracked by Spark.Engine (IDatabaseMigrationService).
CREATE TABLE IF NOT EXISTS database_migrations (
    version       integer     PRIMARY KEY,
    name          text        NOT NULL,
    completed_at  timestamptz NOT NULL DEFAULT now()
);
