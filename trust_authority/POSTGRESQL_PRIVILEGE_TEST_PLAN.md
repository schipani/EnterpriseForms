# G08 — PostgreSQL Role, RLS and DBA Compromise Validation Plan

**Status:** Tests specified, not executed. Integrates with `PRIVILEGED_POSTGRESQL_ACCESS.md`. Postgres privilege inventory is a **read-only report**, not a self-sufficient compliance verdict. Azure policies, Entra role mappings and service networking require external validation.

## Test environments

1. **Local Docker**, isolated business PostgreSQL 18 and authority PostgreSQL 18 containers with entirely different credentials and no shared role administration. Use Vault Transit dev mode for local MAC integration. Purpose: repeatable negative SQL privilege and RLS checks, not a proof of production cloud identity isolation.
2. **Azure staging**, two independent PostgreSQL Flexible Servers, independent Entra administrators, separate workload managed identities, restricted private networking, independent SpiceDB, and test HSM and ledger. This is the **required Level 2 assurance test**.
3. **Production pre-release**, run non-destructive privilege inventory, effective Azure IAM/group permissions inventory, and RLS/policy checks. Do not perform destructive hostile-DBA tests on production.

## Automated CI/release gates

- Use `postgres-privilege-inventory.sql` on both servers via approved audit principal; compare against a reviewed per-environment allowlist. Unrecognized role memberships, BYPASSRLS, schema owner, broad predefined role grant, PUBLIC execution on protected functions, unapproved CREATE privileges, missing FORCE RLS, or unreviewed view/function owners fail deployment.
- Run role-negative SQL tests with the actual runtime/migration/projection identities, never just introspection SQL as a DBA. Ensure permission failure (`SQLSTATE 42501`) on unauthorized operations.
- Create a protected fixture in Organization A; compare the authority's current owner, delegated shares, membership and content manifest against BusinessDb projections before and after malicious direct SQL edits using a business DBA identity.
- For compromised-SQL-credential tests, acknowledge that an attacker can spoof a custom tenant GUC and read any plaintext it can SELECT directly; assert only that app-mediated access still requires authority and manifest verification. If any required sensitive field is plaintext, mark the confidentiality requirement **failed** until encryption or equivalent protections are provided.
- Simulate `BEGIN; SET LOCAL app.verified_tenant_id='org-a'; SELECT ...; ROLLBACK;` then reuse the same pooled Npgsql connection under Organization B; ensure context never leaks and protected API flow uses independently verified subject/tenant.
- Check table ownership, function SECURITY DEFINER `search_path`, untrusted schema CREATE, PUBLIC function EXECUTE and `security_invoker`/view-owner semantics against version-specific rules.
- Simulate delayed AuthorityDb/SpiceDB/HSM/network errors; protected access must fail closed without switching to local business ownership GUIDs or unsigned ACL rows.
- Verify business runtime identity cannot access the *authority* network, database, HSM, or SpiceDB mutation API, even when it holds valid business DB credentials.
- Verify JIT migration identity is absent from API containers, background workers and CI jobs outside deployment approvals. Verify no password/credential logged or retained in backups/images.
- Independently collect audit evidence from DB/Entra/Azure/HSM; prove suspicious DDL/role grants do not rely on attacker-controlled BusinessDb audit tables.

## Suggested SQL probes as the actual runtime identity

These statements are deliberately **not** bundled in an auto-executable destructive migration. Run them in a disposable test database using test fixtures, with expected failure assertions in a test harness.

```sql
-- EXPECT DENIED (or operation impossible in the runtime role):
CREATE ROLE forbidden_elevated_role LOGIN;
ALTER TABLE app_data.form_records DISABLE ROW LEVEL SECURITY;
TRUNCATE app_data.form_records;
SET ROLE df_business_schema_owner;
UPDATE app_data.form_records
SET owner_user_id = '11111111-1111-4111-8111-111111111111'
WHERE form_record_id = '22222222-2222-4222-8222-222222222222';
```

This cannot protect against a **privileged DBA**. Repeat the unauthorized UPDATE using the business DBA identity in staging and verify that the independent authority **rejects the forged ownership** and raises an integrity incident. The DBA may successfully alter the physical row: that is an expected *tampering* result, not a failed security test, as long as no unauthorized application-mediated access is granted.

## Evidence required per test

Record the test ID (PG-01..PG-20), exact tested PostgreSQL version/hosting SKU, Entra principal/object ID, database role, SQLSTATE, timestamp, source workload, expected result, observed result, authority revision/root, HSM/ledger involvement (when relevant), and independent audit reference. Redact secrets, field data and protected search predicates.

## Exit criteria

Only mark G08 `CLOSED` after approved privilege migrations, Azure resource/identity separation, privilege-inventory policy checks, real managed-service role and network tests, the PG-01..PG-20 adversarial suite, fail-closed end-to-end authorization checks and an independent reviewer sign-off. A document, SQL template or local Docker passing test is not sufficient evidence.
