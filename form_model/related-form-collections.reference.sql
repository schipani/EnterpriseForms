-- Reference schema only. DO NOT apply to production without integrating tenant-safe
-- composite FKs, migration sequencing, authority projections and trigger policies.
-- PostgreSQL 18 / uuidv7(); naming convention: descriptive multiword columns.

CREATE TABLE form_relationship_definitions (
    form_relationship_definition_id uuid PRIMARY KEY DEFAULT uuidv7(),
    tenant_id uuid NOT NULL,
    parent_form_definition_id uuid NOT NULL,
    child_form_definition_id uuid NOT NULL,
    relationship_definition_key text NOT NULL,
    relationship_display_name text NOT NULL,
    relationship_lifecycle_status varchar(24) NOT NULL DEFAULT 'Available',
    relationship_revision bigint NOT NULL DEFAULT 1,
    relationship_created_on timestamptz NOT NULL DEFAULT now(),
    relationship_created_by_user_id uuid NOT NULL,
    CONSTRAINT ck_frdef_lifecycle CHECK (
        relationship_lifecycle_status IN ('Available', 'Retired')
    ),
    CONSTRAINT ck_frdef_not_self CHECK (
        parent_form_definition_id <> child_form_definition_id
    ),
    CONSTRAINT uq_frdef_tenant_parent_key UNIQUE (
        tenant_id, parent_form_definition_id, relationship_definition_key
    ),
    CONSTRAINT uq_frdef_tenant_id UNIQUE (
        tenant_id, form_relationship_definition_id
    ),
    CONSTRAINT fk_frdef_parent FOREIGN KEY (parent_form_definition_id)
        REFERENCES form_definitions(form_definition_id),
    CONSTRAINT fk_frdef_child FOREIGN KEY (child_form_definition_id)
        REFERENCES form_definitions(form_definition_id)
);

CREATE TABLE form_record_relationships (
    form_record_relationship_id uuid PRIMARY KEY DEFAULT uuidv7(),
    tenant_id uuid NOT NULL,
    form_relationship_definition_id uuid NOT NULL,
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
    row_signature bytea NULL,
    row_signature_key_version integer NULL,
    row_signature_schema_version integer NULL,
    CONSTRAINT ck_frrel_not_self CHECK (
        parent_form_record_id <> child_form_record_id
    ),
    CONSTRAINT ck_frrel_lifecycle CHECK (
        relationship_lifecycle_status IN ('Pending','Active','Removed')
    ),
    CONSTRAINT ck_frrel_signature_length CHECK (
        row_signature IS NULL OR octet_length(row_signature) = 32
    ),
    CONSTRAINT fk_frrel_definition FOREIGN KEY
        (tenant_id, form_relationship_definition_id)
        REFERENCES form_relationship_definitions
        (tenant_id, form_relationship_definition_id),
    CONSTRAINT fk_frrel_parent FOREIGN KEY (parent_form_record_id)
        REFERENCES form_records(form_record_id),
    CONSTRAINT fk_frrel_child FOREIGN KEY (child_form_record_id)
        REFERENCES form_records(form_record_id)
);

-- In v1 each child has at most one active owning/composition link.
CREATE UNIQUE INDEX uq_frrel_active_child
    ON form_record_relationships (tenant_id, child_form_record_id)
    WHERE relationship_lifecycle_status = 'Active';

CREATE UNIQUE INDEX uq_frrel_active_edge
    ON form_record_relationships
    (tenant_id, parent_form_record_id, form_relationship_definition_id, child_form_record_id)
    WHERE relationship_lifecycle_status = 'Active';

CREATE INDEX ix_frrel_parent_list
    ON form_record_relationships
    (tenant_id, parent_form_record_id, form_relationship_definition_id,
     relationship_lifecycle_status, child_record_sort_order,
     form_record_relationship_id);

CREATE INDEX ix_frrel_child_lookup
    ON form_record_relationships
    (tenant_id, child_form_record_id, relationship_lifecycle_status);

-- Integration MUST add tenant-safe composite FKs for both form_definitions
-- and form_records (using UNIQUE(tenant_id, entity_id) as needed), protect
-- child-form membership and form-definition graph acyclicity, guard against
-- concurrent max-count races, and implement authority HMAC/Merkle signing.
-- No business-database row can independently confer access.
