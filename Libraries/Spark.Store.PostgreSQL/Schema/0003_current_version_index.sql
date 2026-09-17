-- Spark PostgreSQL store: index for searching and counting a resource type.
-- Every statement is idempotent so the script can be re-run safely.

-- The current, not deleted versions of a resource type, newest first. It covers the columns a search
-- returns, so that searching or counting a resource type is answered from the index alone. Without it
-- PostgreSQL reads the whole resources table and sorts the result.
CREATE INDEX IF NOT EXISTS resources_current_type_idx
    ON resources (type, updated_at DESC, id DESC)
    INCLUDE (resource_id, version_id)
    WHERE state = 'current' AND method <> 'DELETE';
