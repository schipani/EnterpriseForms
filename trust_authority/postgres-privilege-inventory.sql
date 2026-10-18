-- G08 READ-ONLY privilege/configuration inventory — PostgreSQL 18.
-- Run with an approved audit/admin identity against EACH PostgreSQL server.
-- Results are EVIDENCE to compare against an approved policy allowlist; do
-- not interpret this as a self-enforcing security policy or as a full audit.

-- Roles, privilege attributes, and unrestricted built-in role memberships.
SELECT
    database_roles.rolname AS database_role_name,
    database_roles.rolcanlogin AS login_enabled,
    database_roles.rolsuper AS superuser_enabled,
    database_roles.rolcreatedb AS create_database_enabled,
    database_roles.rolcreaterole AS create_role_enabled,
    database_roles.rolreplication AS replication_enabled,
    database_roles.rolbypassrls AS bypass_row_security_enabled,
    database_roles.rolinherit AS inherits_role_privileges
FROM pg_catalog.pg_roles AS database_roles
ORDER BY database_roles.rolname;

SELECT
    membership_roles.rolname AS member_role_name,
    granted_roles.rolname AS granted_role_name,
    database_memberships.admin_option AS membership_admin_option,
    database_memberships.inherit_option AS membership_inherit_option,
    database_memberships.set_option AS membership_set_option
FROM pg_catalog.pg_auth_members AS database_memberships
JOIN pg_catalog.pg_roles AS membership_roles
    ON membership_roles.oid = database_memberships.member
JOIN pg_catalog.pg_roles AS granted_roles
    ON granted_roles.oid = database_memberships.roleid
ORDER BY membership_roles.rolname, granted_roles.rolname;

-- RLS inventory for ordinary and partitioned tables.
SELECT
    table_namespace.nspname AS schema_name,
    database_tables.relname AS table_name,
    database_tables.relrowsecurity AS row_security_enabled,
    database_tables.relforcerowsecurity AS force_row_security_enabled,
    table_owners.rolname AS table_owner_role_name
FROM pg_catalog.pg_class AS database_tables
JOIN pg_catalog.pg_namespace AS table_namespace
    ON table_namespace.oid = database_tables.relnamespace
JOIN pg_catalog.pg_roles AS table_owners
    ON table_owners.oid = database_tables.relowner
WHERE database_tables.relkind IN ('r','p')
  AND table_namespace.nspname NOT IN ('pg_catalog','information_schema')
  AND table_namespace.nspname NOT LIKE 'pg_toast%'
ORDER BY table_namespace.nspname, database_tables.relname;

SELECT
    policy_schema.schemaname AS schema_name,
    policy_schema.tablename AS table_name,
    policy_schema.policyname AS policy_name,
    policy_schema.permissive AS policy_is_permissive,
    policy_schema.roles AS policy_role_names,
    policy_schema.cmd AS policy_command_name,
    policy_schema.qual AS policy_using_expression,
    policy_schema.with_check AS policy_check_expression
FROM pg_catalog.pg_policies AS policy_schema
ORDER BY policy_schema.schemaname, policy_schema.tablename, policy_schema.policyname;

-- Catalog ACLs, not only information_schema.role_table_grants: the latter
-- filters by current-role visibility and is NOT a complete admin inventory.
-- Effective privileges also include role inheritance, ownership and PUBLIC.
SELECT
    object_schema.nspname AS schema_name,
    database_objects.relname AS object_name,
    database_objects.relkind AS object_kind_code,
    CASE WHEN object_grants.grantee = 0 THEN 'PUBLIC'
         ELSE pg_catalog.pg_get_userbyid(object_grants.grantee) END AS grantee_role_name,
    pg_catalog.pg_get_userbyid(object_grants.grantor) AS grantor_role_name,
    object_grants.privilege_type AS privilege_type_name,
    object_grants.is_grantable AS grant_option_enabled
FROM pg_catalog.pg_class AS database_objects
JOIN pg_catalog.pg_namespace AS object_schema
    ON object_schema.oid = database_objects.relnamespace
CROSS JOIN LATERAL pg_catalog.aclexplode(
    COALESCE(database_objects.relacl,
             pg_catalog.acldefault('r', database_objects.relowner))
) AS object_grants
WHERE database_objects.relkind IN ('r','p','v','m','f')
  AND object_schema.nspname NOT IN ('pg_catalog','information_schema')
ORDER BY schema_name, object_name, grantee_role_name, privilege_type_name;

SELECT
    object_schema.nspname AS schema_name,
    database_objects.relname AS table_name,
    column_attributes.attname AS column_name,
    CASE WHEN column_grants.grantee = 0 THEN 'PUBLIC'
         ELSE pg_catalog.pg_get_userbyid(column_grants.grantee) END AS grantee_role_name,
    column_grants.privilege_type AS privilege_type_name,
    column_grants.is_grantable AS grant_option_enabled
FROM pg_catalog.pg_class AS database_objects
JOIN pg_catalog.pg_namespace AS object_schema
    ON object_schema.oid = database_objects.relnamespace
JOIN pg_catalog.pg_attribute AS column_attributes
    ON column_attributes.attrelid = database_objects.oid
   AND column_attributes.attnum > 0
   AND NOT column_attributes.attisdropped
CROSS JOIN LATERAL pg_catalog.aclexplode(
    COALESCE(column_attributes.attacl, ARRAY[]::aclitem[])
) AS column_grants
WHERE database_objects.relkind IN ('r','p','v','m','f')
  AND object_schema.nspname NOT IN ('pg_catalog','information_schema')
ORDER BY schema_name, table_name, column_name, grantee_role_name;

SELECT
    schema_permissions.nspname AS schema_name,
    pg_catalog.pg_get_userbyid(schema_permissions.nspowner) AS schema_owner_role_name,
    schema_permissions.nspacl::text AS explicit_schema_acl_text
FROM pg_catalog.pg_namespace AS schema_permissions
WHERE schema_permissions.nspname NOT IN ('pg_catalog','information_schema')
ORDER BY schema_permissions.nspname;

SELECT
    function_namespace.nspname AS schema_name,
    database_functions.proname AS function_name,
    database_functions.prosecdef AS security_definer_enabled,
    pg_catalog.pg_get_userbyid(database_functions.proowner) AS function_owner_role_name,
    pg_catalog.pg_get_function_identity_arguments(database_functions.oid) AS function_argument_description,
    database_functions.proconfig::text AS function_configuration_text,
    database_functions.proacl::text AS function_acl_text
FROM pg_catalog.pg_proc AS database_functions
JOIN pg_catalog.pg_namespace AS function_namespace
    ON function_namespace.oid = database_functions.pronamespace
WHERE function_namespace.nspname NOT IN ('pg_catalog','information_schema')
ORDER BY function_namespace.nspname, database_functions.proname;

-- Views without SECURITY INVOKER may use owner rights on underlying tables.
SELECT
    view_namespace.nspname AS schema_name,
    database_views.relname AS view_name,
    pg_catalog.pg_get_userbyid(database_views.relowner) AS view_owner_role_name,
    database_views.reloptions::text AS view_configuration_text
FROM pg_catalog.pg_class AS database_views
JOIN pg_catalog.pg_namespace AS view_namespace
    ON view_namespace.oid = database_views.relnamespace
WHERE database_views.relkind = 'v'
  AND view_namespace.nspname NOT IN ('pg_catalog','information_schema')
ORDER BY view_namespace.nspname, database_views.relname;

-- Default privileges: absence of rows does NOT imply PUBLIC has no defaults;
-- PostgreSQL normally grants EXECUTE on new functions to PUBLIC.
SELECT
    owner_roles.rolname AS object_creator_role_name,
    COALESCE(privilege_namespace.nspname, '(global)') AS schema_scope_name,
    default_privileges.defaclobjtype AS object_type_code,
    default_privileges.defaclacl::text AS default_grants_text
FROM pg_catalog.pg_default_acl AS default_privileges
JOIN pg_catalog.pg_roles AS owner_roles
    ON owner_roles.oid = default_privileges.defaclrole
LEFT JOIN pg_catalog.pg_namespace AS privilege_namespace
    ON privilege_namespace.oid = default_privileges.defaclnamespace
ORDER BY owner_roles.rolname, schema_scope_name, object_type_code;

-- Database-level ACL and current configuration.
SELECT
    database_records.datname AS database_name,
    pg_catalog.pg_get_userbyid(database_records.datdba) AS database_owner_role_name,
    database_records.datacl::text AS database_acl_text
FROM pg_catalog.pg_database AS database_records
WHERE database_records.datname = current_database();

SELECT current_setting('row_security') AS current_row_security_setting,
       current_setting('search_path') AS current_search_path_setting,
       current_setting('ssl') AS transport_ssl_enabled_setting;

-- Must also externally inventory Azure control-plane grants, Entra admin roles,
-- group membership, networking, backup/restore, HSM and ledger IAM. SQL cannot
-- prove that two services are independently administered.
