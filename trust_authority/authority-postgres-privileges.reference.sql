-- LEVEL 2 G08 — Independent Authority PostgreSQL 18 example role setup.
-- REFERENCE ONLY; review on independently governed AUTHORITY cluster.
-- NEVER run on BusinessDb. Never reuse BusinessDb identities/administrators.
-- Actual Azure Entra workload principals must be provisioned independently.

BEGIN;

CREATE ROLE df_authority_schema_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE df_authority_runtime_priv NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE df_authority_projector_priv NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE df_authority_readonly_priv NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

-- Existing authority-store.reference.sql creates authorization schema and
-- tables. Review ownership: ensure owner is df_authority_schema_owner.
-- The real authority deployer can execute SET ROLE after a JIT grant.
-- Do not grant BusinessDb accounts membership in any of these roles.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE df_authority_schema_owner
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE df_authority_schema_owner IN SCHEMA authorization
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE df_authority_schema_owner IN SCHEMA authorization
    REVOKE ALL ON SEQUENCES FROM PUBLIC;

REVOKE ALL ON SCHEMA authorization FROM PUBLIC;
GRANT USAGE ON SCHEMA authorization TO df_authority_runtime_priv,
    df_authority_projector_priv, df_authority_readonly_priv;

COMMIT;

-- AFTER MIGRATION: explicitly whitelist commands required by each authority
-- component. Do NOT default to GRANT ALL ON ALL TABLES. Examples (adjust to
-- actual workflow and table-by-table authorization / transactional CAS):
--
-- GRANT SELECT, INSERT ON authorization.security_commands
--     TO df_authority_runtime_priv;
-- GRANT SELECT ON authorization.organization_authorization_states,
--     authorization.authority_resources,
--     authorization.authority_relationship_edges
--     TO df_authority_runtime_priv;
--
-- If the authority runtime needs to advance a revision, grant only approved
-- columns/operations for its specific verified transaction code path, not
-- generic business-API-controlled SQL. The authority's typed command service
-- must validate identity, permission, expected revision, chain and audit.
--
-- Projection worker: SELECT required immutable journal/outbox events and
-- narrowly scoped UPDATE of ACK fields only (fenced CAS protocol required).
--
-- IMPORTANT: SpiceDB owns its *own* datastore identity/schema/possibly cluster.
-- Do not expose its datastore DML to business API or generic authority clients.
-- Managed HSM and Confidential Ledger permissions are NOT granted in SQL.
