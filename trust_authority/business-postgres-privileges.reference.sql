-- LEVEL 2 G08 — Business PostgreSQL 18 example privilege baseline.
-- REFERENCE ONLY. DO NOT execute unchanged against existing production databases.
-- Review migrations, actual schema/table names, existing grants, Entra identities,
-- deployed Azure PostgreSQL privileges, and ownership before running.
-- This script demonstrates privilege groups; cloud Entra identities are provisioned
-- through provider-appropriate procedures and then granted the group roles.
-- Execute as approved provisioning identity, not as the normal application login.
-- Never use this script in the independent AuthorityDb.

BEGIN;

CREATE ROLE df_business_schema_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE df_business_api_priv NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE df_business_projection_priv NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE ROLE df_business_readonly_priv NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

-- app_data is a REFERENCE schema name. Existing tables may be in public;
-- migrate their names/permissions explicitly. Never simply assume a schema rename.
CREATE SCHEMA app_data AUTHORIZATION df_business_schema_owner;

-- Lock down the ordinary public schema and default broad grants.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA app_data FROM PUBLIC;
GRANT USAGE ON SCHEMA app_data TO df_business_api_priv,
    df_business_projection_priv, df_business_readonly_priv;

-- NOTE: globally revoke default PUBLIC EXECUTE on functions before migrations.
-- Run as (or SET ROLE to) the exact role that will CREATE objects.
-- ALTER DEFAULT PRIVILEGES acts on the CREATING ROLE; per-schema REVOKE
-- cannot undo its global default PUBLIC EXECUTE grant.
ALTER DEFAULT PRIVILEGES FOR ROLE df_business_schema_owner
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

-- Tables have no PUBLIC data grants by default, but explicitly clear existing
-- grants where tables have already been created (not shown in this bootstrap).
ALTER DEFAULT PRIVILEGES FOR ROLE df_business_schema_owner IN SCHEMA app_data
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE df_business_schema_owner IN SCHEMA app_data
    REVOKE ALL ON SEQUENCES FROM PUBLIC;

COMMIT;

-- PRIVILEGE GRANTS TO APPLY AFTER REVIEWED TABLE MIGRATIONS:
-- Run in a separate approved deployment transaction using actual table names.
-- These show an intentionally narrow policy for protected published rows.
--
--   REVOKE ALL ON app_data.form_records FROM PUBLIC;
--   REVOKE ALL ON app_data.form_records FROM df_business_api_priv;
--   GRANT SELECT ON app_data.form_records TO df_business_api_priv;
--   -- NO UPDATE/DELETE of owner_user_id / owner_team_id / row_signature by API.
--
-- Staged immutable candidates must use an audited INSERT-only staging table,
-- keyed by a trusted operation ID with explicit tenant/record/field binding:
--   GRANT INSERT, SELECT ON app_data.record_content_staging
--       TO df_business_api_priv;
--   -- Never grant UPDATE/DELETE/TRUNCATE on accepted immutable candidates.
--
-- Projection materializer has only explicit table/column write privileges;
-- it never becomes authoritative. Every local row remains HMAC/manifest checked:
--   GRANT SELECT ON app_data.form_records TO df_business_projection_priv;
--   GRANT UPDATE (owner_user_id, owner_team_id, row_signature,
--                 row_signature_key_version, row_revision)
--      ON app_data.form_records TO df_business_projection_priv;
--   -- The projection role MUST NOT have AUTHORITY database access.
--
-- Reporters get explicit sanitized views, not SELECT ALL on sensitive tables.
-- For Postgres 18 views with underlying RLS, prefer SECURITY INVOKER where
-- appropriate and verify actual definer/invoker semantics in integration tests.
--
-- Recommended reviewed migration-only actions (owner role):
--   ALTER TABLE app_data.form_records ENABLE ROW LEVEL SECURITY;
--   ALTER TABLE app_data.form_records FORCE ROW LEVEL SECURITY;
--   CREATE POLICY form_records_runtime_tenant_select
--       ON app_data.form_records FOR SELECT TO df_business_api_priv
--       USING (
--           tenant_id = NULLIF(current_setting('app.verified_tenant_id', true), '')::uuid
--       );
-- IMPORTANT: custom GUC is spoofable by a SQL-capable caller. This is ONLY
-- defense-in-depth, NEVER proof of identity or Level-2 tenant authorization.
--
--   -- Similar per-table policies for typed values, repeating groups, and
--   -- attachments must additionally restrict record membership and writes.
--
-- Do not grant CREATE, TRUNCATE, TRIGGER, REFERENCES, MAINTAIN, ALTER or
-- any built-in pg_*_all_data or BYPASSRLS capability to runtime identities.
--
-- DB CONNECT/TEMPORARY control is database-specific and must be performed by
-- the proper database owner/provisioner *after ensuring admin access remains*:
--   REVOKE CONNECT, TEMPORARY ON DATABASE your_business_database FROM PUBLIC;
--   GRANT CONNECT ON DATABASE your_business_database TO
--       df_business_api_priv, df_business_projection_priv,
--       df_business_readonly_priv;
-- Entra workload database role mappings and narrowly approved migrator role
-- membership should be provisioned via infrastructure/change management.
