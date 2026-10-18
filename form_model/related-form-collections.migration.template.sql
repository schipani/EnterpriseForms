-- RELATED FORM COLLECTIONS — PostgreSQL 18 migration TEMPLATE
-- Requires existing form_definitions(tenant_id, form_definition_id),
-- form_records(tenant_id, form_record_id, form_definition_id).
-- Test on an isolated DB first; reconcile with EF Core migrations and actual schema.
-- Does not implement trusted-authority writes, Merkle-v2 manifests, or audit pipeline.
BEGIN;

-- Composite targets make the tenant and expected form type part of every FK.
-- Check for existing equivalent constraints before applying to an existing database.
ALTER TABLE form_definitions
    ADD CONSTRAINT uq_form_definitions_tenant_identity
    UNIQUE (tenant_id, form_definition_id);

ALTER TABLE form_records
    ADD CONSTRAINT uq_form_records_tenant_identity_and_type
    UNIQUE (tenant_id, form_record_id, form_definition_id);

CREATE TABLE form_relationship_definitions (
    form_relationship_definition_id uuid PRIMARY KEY DEFAULT uuidv7(),
    tenant_id uuid NOT NULL,
    parent_form_definition_id uuid NOT NULL,
    child_form_definition_id uuid NOT NULL,
    relationship_definition_key varchar(120) NOT NULL,
    relationship_display_name varchar(200) NOT NULL,
    relationship_lifecycle_status varchar(24) NOT NULL DEFAULT 'Available',
    relationship_revision bigint NOT NULL DEFAULT 1,
    relationship_created_on timestamptz NOT NULL DEFAULT now(),
    relationship_created_by_user_id uuid NOT NULL,
    CONSTRAINT ck_form_relationship_definitions_distinct_forms
        CHECK (parent_form_definition_id <> child_form_definition_id),
    CONSTRAINT ck_form_relationship_definitions_status
        CHECK (relationship_lifecycle_status IN ('Available', 'Retired')),
    CONSTRAINT ck_form_relationship_definitions_revision
        CHECK (relationship_revision > 0),
    CONSTRAINT uq_form_relationship_definitions_parent_key
        UNIQUE (tenant_id, parent_form_definition_id, relationship_definition_key),
    CONSTRAINT uq_form_relationship_definitions_identity_and_types
        UNIQUE (tenant_id, form_relationship_definition_id,
                parent_form_definition_id, child_form_definition_id),
    CONSTRAINT fk_form_relationship_definitions_parent
        FOREIGN KEY (tenant_id, parent_form_definition_id)
        REFERENCES form_definitions(tenant_id, form_definition_id),
    CONSTRAINT fk_form_relationship_definitions_child
        FOREIGN KEY (tenant_id, child_form_definition_id)
        REFERENCES form_definitions(tenant_id, form_definition_id)
);

CREATE TABLE form_record_relationships (
    form_record_relationship_id uuid PRIMARY KEY DEFAULT uuidv7(),
    tenant_id uuid NOT NULL,
    form_relationship_definition_id uuid NOT NULL,
    parent_form_definition_id uuid NOT NULL,
    child_form_definition_id uuid NOT NULL,
    parent_form_record_id uuid NOT NULL,
    child_form_record_id uuid NOT NULL,
    child_record_sort_order integer NOT NULL DEFAULT 0,
    relationship_lifecycle_status varchar(24) NOT NULL DEFAULT 'Pending',
    relationship_revision bigint NOT NULL DEFAULT 1,
    relationship_created_on timestamptz NOT NULL DEFAULT now(),
    relationship_created_by_user_id uuid NOT NULL,
    relationship_removed_on timestamptz NULL,
    relationship_removed_by_user_id uuid NULL,
    relationship_removal_reason text NULL,
    accepted_authority_operation_id uuid NULL,
    row_signature bytea NULL,
    row_signature_key_version integer NULL,
    row_signature_schema_version integer NULL,
    CONSTRAINT ck_form_record_relationships_not_self
        CHECK (parent_form_record_id <> child_form_record_id),
    CONSTRAINT ck_form_record_relationships_status
        CHECK (relationship_lifecycle_status IN ('Pending', 'Active', 'Removed')),
    CONSTRAINT ck_form_record_relationships_revision
        CHECK (relationship_revision > 0),
    CONSTRAINT ck_form_record_relationships_removed_metadata
        CHECK (
            relationship_lifecycle_status <> 'Removed'
            OR relationship_removed_on IS NOT NULL
        ),
    CONSTRAINT ck_form_record_relationships_signature_length
        CHECK (row_signature IS NULL OR octet_length(row_signature) = 32),
    CONSTRAINT fk_form_record_relationships_definition
        FOREIGN KEY (tenant_id, form_relationship_definition_id,
                     parent_form_definition_id, child_form_definition_id)
        REFERENCES form_relationship_definitions
          (tenant_id, form_relationship_definition_id,
           parent_form_definition_id, child_form_definition_id),
    CONSTRAINT fk_form_record_relationships_parent
        FOREIGN KEY (tenant_id, parent_form_record_id, parent_form_definition_id)
        REFERENCES form_records(tenant_id, form_record_id, form_definition_id),
    CONSTRAINT fk_form_record_relationships_child
        FOREIGN KEY (tenant_id, child_form_record_id, child_form_definition_id)
        REFERENCES form_records(tenant_id, form_record_id, form_definition_id),
    CONSTRAINT uq_form_record_relationships_tenant_identity
        UNIQUE (tenant_id, form_record_relationship_id)
);

-- One current composition parent; include Pending to reserve a child during creation.
CREATE UNIQUE INDEX uq_form_record_relationships_current_child
    ON form_record_relationships (tenant_id, child_form_record_id)
    WHERE relationship_lifecycle_status IN ('Pending', 'Active');

CREATE UNIQUE INDEX uq_form_record_relationships_current_edge
    ON form_record_relationships
       (tenant_id, parent_form_record_id,
        form_relationship_definition_id, child_form_record_id)
    WHERE relationship_lifecycle_status IN ('Pending', 'Active');

CREATE INDEX ix_form_record_relationships_parent_page
    ON form_record_relationships
       (tenant_id, parent_form_record_id,
        form_relationship_definition_id, relationship_lifecycle_status,
        child_record_sort_order, form_record_relationship_id);

CREATE INDEX ix_form_record_relationships_child_lookup
    ON form_record_relationships
       (tenant_id, child_form_record_id, relationship_lifecycle_status);

-- Defense-in-depth graph-cycle check for accepted/pending composition links.
-- Serializes graph writes by tenant to avoid two concurrent operations forming
-- a cycle. This is NOT authoritative against a DBA; the external authority
-- must independently approve the exact accepted graph revision.
CREATE FUNCTION assert_form_record_relationship_acyclic()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.relationship_lifecycle_status NOT IN ('Pending', 'Active') THEN
        RETURN NEW;
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.tenant_id::text, 0));

    IF EXISTS (
        WITH RECURSIVE ancestors(form_record_id) AS (
            SELECT NEW.parent_form_record_id
            UNION
            SELECT link.parent_form_record_id
            FROM form_record_relationships AS link
            JOIN ancestors AS a ON a.form_record_id = link.child_form_record_id
            WHERE link.tenant_id = NEW.tenant_id
              AND link.relationship_lifecycle_status IN ('Pending', 'Active')
              AND link.form_record_relationship_id <> NEW.form_record_relationship_id
        )
        SELECT 1 FROM ancestors
        WHERE form_record_id = NEW.child_form_record_id
    ) THEN
        RAISE EXCEPTION 'Related form composition cycle detected'
            USING ERRCODE = '23514';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_form_record_relationships_acyclic
    BEFORE INSERT OR UPDATE OF
        parent_form_record_id, child_form_record_id, relationship_lifecycle_status
    ON form_record_relationships
    FOR EACH ROW EXECUTE FUNCTION assert_form_record_relationship_acyclic();

-- RLS is defense in depth; never trust current_setting() supplied by a stolen
-- SQL credential as sufficient proof of tenant authorization.
ALTER TABLE form_relationship_definitions ENABLE ROW LEVEL SECURITY;
ALTER TABLE form_relationship_definitions FORCE ROW LEVEL SECURITY;
ALTER TABLE form_record_relationships ENABLE ROW LEVEL SECURITY;
ALTER TABLE form_record_relationships FORCE ROW LEVEL SECURITY;

-- Policies, ownership of functions, least-privilege grants, signature commit
-- workflows, migration role, authority projection role, and audit triggers
-- MUST be configured per PRIVILEGED_POSTGRESQL_ACCESS.md before production.
-- Deliberately grant no access here. A missing policy denies ordinary roles.
COMMIT;
