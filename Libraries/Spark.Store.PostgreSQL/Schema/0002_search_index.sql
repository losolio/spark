-- Spark PostgreSQL store: search index.
-- Every statement is idempotent so the script can be re-run safely.

-- Needed for GiST indexes that combine a scalar column with a range, see search_date.
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- Search parameters, one row per resource type and code. The search index tables refer to a
-- parameter by its smallint id, so they need neither the resource type nor the code as text.
CREATE TABLE IF NOT EXISTS search_params (
    id             smallint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    resource_type  text NOT NULL,
    code           text NOT NULL,
    CONSTRAINT search_params_code_key UNIQUE (resource_type, code)
);

-- Search index, one table per search parameter type and one row per indexed value of the
-- current version of a resource. The rows are derived from the resources table and can be
-- rebuilt from it, so they have no foreign keys, which keeps writes cheap. Every value index
-- includes resource_key so that searches can be answered from the index alone, and every table
-- has an index on resource_key for replacing the rows of a resource when it is reindexed.
--
-- TODO: Contained resources are not indexed yet. They need a way to tell their rows apart from
--       the rows of the containing resource before they can be searched.
-- TODO: Composite and special (Location near) search parameters are not indexed yet.

-- string: value_normalized is lower-cased without diacritics and is what searches and prefix
-- matches run against; value_exact is the original value for the :exact modifier.
CREATE TABLE IF NOT EXISTS search_string (
    resource_key      bigint   NOT NULL,
    param_id          smallint NOT NULL,
    value_normalized  text     NOT NULL,
    value_exact       text     NOT NULL
);

CREATE INDEX IF NOT EXISTS search_string_value_idx
    ON search_string (param_id, value_normalized text_pattern_ops) INCLUDE (resource_key);

CREATE INDEX IF NOT EXISTS search_string_resource_idx
    ON search_string (resource_key);

-- token: codes, identifiers, booleans and enums. A CodeableConcept.text is its own row with only
-- text set.
CREATE TABLE IF NOT EXISTS search_token (
    resource_key  bigint   NOT NULL,
    param_id      smallint NOT NULL,
    system        text,
    code          text,
    text          text
);

CREATE INDEX IF NOT EXISTS search_token_code_idx
    ON search_token (param_id, code, system) INCLUDE (resource_key);

CREATE INDEX IF NOT EXISTS search_token_resource_idx
    ON search_token (resource_key);

-- date: the instant or period a value covers, with the precision of the value taken into account.
-- A period without a start or an end is unbounded on that side.
CREATE TABLE IF NOT EXISTS search_date (
    resource_key  bigint    NOT NULL,
    param_id      smallint  NOT NULL,
    period        tstzrange NOT NULL
);

CREATE INDEX IF NOT EXISTS search_date_period_idx
    ON search_date USING gist (param_id, period) INCLUDE (resource_key);

CREATE INDEX IF NOT EXISTS search_date_resource_idx
    ON search_date (resource_key);

CREATE TABLE IF NOT EXISTS search_number (
    resource_key  bigint   NOT NULL,
    param_id      smallint NOT NULL,
    value         numeric  NOT NULL
);

CREATE INDEX IF NOT EXISTS search_number_value_idx
    ON search_number (param_id, value) INCLUDE (resource_key);

CREATE INDEX IF NOT EXISTS search_number_resource_idx
    ON search_number (resource_key);

-- quantity: UCUM quantities are stored in their canonical unit, other quantities as given.
CREATE TABLE IF NOT EXISTS search_quantity (
    resource_key  bigint   NOT NULL,
    param_id      smallint NOT NULL,
    system        text,
    code          text,
    value         numeric  NOT NULL
);

CREATE INDEX IF NOT EXISTS search_quantity_value_idx
    ON search_quantity (param_id, code, value) INCLUDE (resource_key, system);

CREATE INDEX IF NOT EXISTS search_quantity_resource_idx
    ON search_quantity (resource_key);

-- reference: a reference to a resource on this server has target_key, a placeholder row in
-- resource_keys when the target does not exist yet. An absolute reference to another server
-- has target_url, and a reference by identifier has identifier_system and identifier_value.
CREATE TABLE IF NOT EXISTS search_reference (
    resource_key       bigint   NOT NULL,
    param_id           smallint NOT NULL,
    target_key         bigint,
    target_url         text,
    identifier_system  text,
    identifier_value   text,
    CONSTRAINT search_reference_target_check
        CHECK (num_nonnulls(target_key, target_url, identifier_system, identifier_value) > 0)
);

-- Serves reference searches, chained searches and _revinclude, all of which start at the target.
CREATE INDEX IF NOT EXISTS search_reference_target_idx
    ON search_reference (target_key, param_id) INCLUDE (resource_key)
    WHERE target_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS search_reference_url_idx
    ON search_reference (param_id, target_url) INCLUDE (resource_key)
    WHERE target_url IS NOT NULL;

CREATE INDEX IF NOT EXISTS search_reference_identifier_idx
    ON search_reference (param_id, identifier_value, identifier_system) INCLUDE (resource_key)
    WHERE identifier_value IS NOT NULL;

-- Also serves _include, which starts at the referencing resource.
CREATE INDEX IF NOT EXISTS search_reference_resource_idx
    ON search_reference (resource_key);

-- uri: text_pattern_ops supports prefix matches for the :below modifier.
CREATE TABLE IF NOT EXISTS search_uri (
    resource_key  bigint   NOT NULL,
    param_id      smallint NOT NULL,
    value         text     NOT NULL
);

CREATE INDEX IF NOT EXISTS search_uri_value_idx
    ON search_uri (param_id, value text_pattern_ops) INCLUDE (resource_key);

CREATE INDEX IF NOT EXISTS search_uri_resource_idx
    ON search_uri (resource_key);
