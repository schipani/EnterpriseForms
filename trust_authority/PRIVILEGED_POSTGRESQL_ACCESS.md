# Level 2 G08 — PostgreSQL Privileged Access and Database Isolation

**Status:** Normative design and deployment/test requirements; NOT deployed, validated, or production-approved.  
**Baseline:** PostgreSQL 18, .NET 10, EF Core/Npgsql, independent Authorization Authority, SpiceDB, Vault Transit for local integration, Azure Managed HSM for production.  
**Threat model:** An attacker may gain arbitrary SQL read/write access to **business PostgreSQL**, including business-database administration credentials. The attacker does **not** control the independently administered Authorization Authority, its PostgreSQL store, SpiceDB administration, HSM, Confidential Ledger, service deployment identities, or the identity provider. An attacker in possession of *additional* control-plane credentials violates this Level 2 assumption and requires a new threat assessment.

## 1. Security objective and nonclaims

Protect against illegitimate access via forged ownership, roles, user/team membership, form/sharing metadata, encryption policies and signed content written directly in business PostgreSQL.

**What G08 can do:** minimize business runtime permissions, prevent mistakes and many compromised low-privilege credentials from modifying critical tables, isolate independently authoritative permissions and keys, enforce tenant-scoped query filtering as defense in depth, detect anomalous DB activity and block unauthorized content release with current authority decisions + independently accepted signatures/manifests.

**What G08 cannot do:** prevent a full business DBA from reading **plaintext** business columns, changing or deleting ordinary data, running direct SQL outside app workflows, disrupting service, sabotaging indexes/search results, manipulating local logs or backups, or circumventing RLS. RLS and ordinary GRANT/REVOKE are **not** a boundary against privileged business DB administrators. HMAC/Merkle verification detects many modifications/replays; it does not restore erased bytes or prove complete ordinary SQL searches. Encryption must be mandatory on fields/documents whose confidentiality against DB-read compromise is required. Service access must be independently authorized and content verified before decryption/release.

Security outcomes must be stated precisely: **hostile business DB writes cannot manufacture app-mediated authorization or accepted signed content**, assuming the separately administered authority/HSM are intact and every protected data path enforces their decisions. This is not a guarantee of business data availability or confidentiality for non-encrypted values.

## 2. Two completely separate PostgreSQL administrative domains

### Business database

- Host: isolated business PostgreSQL 18 instance/cluster (`BusinessDb`) with business-only identities, separate private networking, credentials, keys, administrators, backup policies and logs.
- Stores `FormDefinitions`, `FormVersions`, `FormRecords`, typed value tables, metadata, queued immutable content candidates and *untrusted local* ownership/share/search projections.
- No business PostgreSQL credential—including migration, DBA, backup or monitoring—can log in to the authority cluster or invoke SpiceDB administrative APIs, Managed HSM Sign, or authority security-command internals.
- DBA direct modification may corrupt the projection, but MUST NOT update accepted ownership, security revision, signed manifest root, authorization journal or key state.

### Authority database

- Host: distinct PostgreSQL 18 cluster/server (`AuthorityDb`), separately administered, ideally in another subscription or equivalent independently governed security boundary. Merely using another database **in the same cluster** with the same administrator does not satisfy Level 2.
- Holds independently authoritative identity bindings, ownership, grants, scopes, delegation lineage, tenant security floors, current revisions, accepted record content heads, complete leaf registries and security-command journal.
- Only the authority workflow identity can authorize commands. A dedicated authority-store projector can materialize SpiceDB relationships; SpiceDB authenticates using its own *separate data-store* credential. Do not give business API direct SpiceDB write privileges.
- Authority database **owners, migrators and administrators** are not shared with BusinessDb and are not accessible using application workloads or business support identities.
- Azure Managed HSM and Confidential Ledger are administered independently of BOTH PostgreSQL estates. A business DBA must not be able to reset key policies or remove accepted checkpoints.

**Operational separation requirement:** inventory Azure resource RBAC, managed identities, network routes, credential vault access, backup export/restore roles, deployment pipeline permissions, CI secrets, database-administrator group memberships, SSH/Kubernetes secrets and diagnostics access. Reject any path whereby possessing a BusinessDb DBA credential grants AuthorityDb or HSM administration. Guard against shared subscription `Owner` or resource administrators who can reset both: either split administration or document as an explicit escalation beyond the Level 2 boundary.

## 3. PostgreSQL role matrix

Roles below are examples; create real Entra-managed workload/database principals and map them to least-privilege NOLOGIN groups. Use unique identities per environment and per workload. No shared superuser password in applications.

| Role / principal | Database | Allow | Must deny |
|---|---|---|---|
| `df_business_schema_owner` (NOLOGIN) | Business | Own application schemas/tables/functions; execute migrations via explicitly authorized migration identity | Ordinary API use; credentials; routine direct login |
| `df_business_api_priv` (NOLOGIN group) | Business | Minimal SELECT of needed business tables and INSERT of immutable staged candidates where explicitly permitted | DDL, `BYPASSRLS`, CREATE/ALTER ROLE, writes to owner/team/ACL/security-policy signed published rows, TRUNCATE, blanket table UPDATE |
| `df_business_projection_priv` (NOLOGIN group) | Business | Limited reconciliation of *untrusted* local projections and verified committed-content materialization | Authority-store access, granting security relationships, signing keys, DDL |
| `df_business_readonly_priv` (NOLOGIN group) | Business | Narrow sanitized reporting views where required | Raw encrypted payload exports, protected metadata bypass, DML, security writes |
| `df_business_migration_identity` (JIT LOGIN / Entra principal) | Business | Temporary controlled `SET ROLE df_business_schema_owner` during approved release | Always-on API usage, standing access, authority cluster access |
| `df_business_monitor_identity` | Business | Read approved monitoring statistics/logs | DML, unneeded sensitive contents, privileged functions |
| `df_authority_schema_owner` (NOLOGIN) | Authority | Own authority schema/tables | Business principal membership or normal API execution |
| `df_authority_runtime_priv` (NOLOGIN group) | Authority | Exact command workflow SQL needed under separate managed identity | Raw unrestricted write APIs to arbitrary relationships exposed to business API, superuser/DDL, key management |
| `df_authority_projector_priv` | Authority | Read immutable command/outbox and acknowledge fenced projections | Direct security-policy authorization; revision rewinds; business DB admin |
| `df_spicedb_store_identity` | SpiceDB datastore | Only what SpiceDB needs on its own isolated datastore | Authority journal writes, business DB, HSM |
| Emergency operator | Appropriate boundary | Time-limited approved recovery / migration | Permanent shared privileged account, silently disabling integrity/security |

Do not grant predefined `pg_read_all_data`, `pg_write_all_data`, `pg_execute_server_program`, `pg_read_server_files`, `pg_write_server_files`, `pg_create_subscription`, `pg_maintain`, `pg_monitor` or `BYPASSRLS` casually. `pg_read_all_data` and `pg_write_all_data` do **not** themselves bypass RLS, but they are still broader than needed. Document every exception. `pg_monitor` can expose operational information; treat separately from sensitive record content.

## 4. PostgreSQL hardening baseline

1. PostgreSQL runtime roles: `NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS`. Ordinary API and projection identities must NOT own protected schemas, tables, policies or SECURITY DEFINER functions; must not be a member of the owner or migration roles.
2. Revoke PUBLIC database `CONNECT`/`TEMPORARY` where feasible, grant narrowly to provisioned roles; revoke public-schema `CREATE` for untrusted users. Restrict `search_path` and schema `USAGE`, `CREATE`, function `EXECUTE`, sequence access, `TRIGGER`, `TRUNCATE`, `REFERENCES`, `MAINTAIN` and default privileges. Explicitly review all grants after each migration.
3. On every tenant/record-sensitive business table, enable **and FORCE** RLS. Explicit `USING` and `WITH CHECK` policies restrict tenant and operation for intended runtime roles. A policy cannot authorize access beyond independently verified authority decisions. RLS is a defense-in-depth filter only.
4. Untrusted runtime cannot invoke a function/view that uses owner privileges to bypass RLS unexpectedly. Prefer `SECURITY INVOKER` views where appropriate; audit default owner-rights views. Restrict `SECURITY DEFINER` functions to reviewed code; set safe fixed `search_path` with `pg_temp` last; fully qualify names; revoke `EXECUTE` from PUBLIC *in the same transaction as creation*. Prohibit dynamic SQL built from caller-controlled object names or data. Treat extension installation and unsafe languages as migration-only.
5. No blanket `GRANT ALL ON ALL TABLES`, blanket default grants to application, table ownership by application login, `SET ROLE` to owners, or access to raw authority tables from business DB. Explicit table-and-column privileges derived from a reviewed matrix. In particular, legitimate owner/share/policy changes go **through authority commands**; directly changing local projection columns has no effect on authorization.
6. Prefer independent named managed identity for each API, projector, migration deployment and monitoring workflow. Provision Entra principals with *nonadmin* role where supported. Never use the server administrator or `azure_pg_admin` as the application connection.
7. Protect PostgreSQL connection strings, tokens, TLS settings and connection-pool scope. Require encrypted transport, server certificate validation, private networking, firewalls, restricted egress, no general internet PostgreSQL access, short token lifetime and timely revocation. No passwords committed into code/CI configs.
8. PostgreSQL `ALTER ROLE`, default privileges and RLS settings are schema/security changes, not application user preferences. Security-sensitive migrations require reviewed CI gates, an approved separate identity and independent audit.
9. Business DB maintenance/restore utilities, logical replication, backups, extensions, ETL, BI tools, admin consoles and debugging sessions must not become unintended production-write or unrestricted read accounts. Segregate support access with just-in-time approval and audited read-only workflows; operational fixes require incident response.
10. For Azure Database for PostgreSQL Flexible Server: distinguish pseudo-superuser `azure_pg_admin` from real PostgreSQL superuser; server/Entra administrators have broad authority even if not superuser. Avoid assuming you can customize `pg_hba.conf`. Configure private access / disable public access, Entra-based database identities and monitored Azure RBAC. Review admin group membership across both clusters. Test RLS and privilege behavior on the **actual managed service**.

### Crucial RLS identity caveat

PostgreSQL custom settings such as `current_setting('app.verified_tenant_id', true)` are caller-settable for a role that can execute arbitrary SQL. Using `SET LOCAL` after validating the web request is useful for ordinary multi-tenant API safety, but a compromised SQL credential or SQL injection can impersonate another tenant by setting the same value. **Never advertise this as secure tenant proof against the Level 2 attacker.** An authority decision, signed record/context binding and application API checks are still required. Role separation alone does not prevent a privileged DBA from bypassing RLS. For stronger protection from compromised runtime SQL credentials, avoid giving them raw plaintext access or make all sensitive content application-encrypted and require independently approved release.

## 5. Safe tenant-query pattern (defense in depth only)

**Execution:** authenticate OIDC; verify user's organization and role through authority; begin DB transaction; set verified tenant and request correlation via parameterized `set_config(..., true)`; execute tenant-scoped SQL and return only independently authorized/manifest-verified projection; commit/rollback; avoid pooled-session `SET` leakage. Never take tenant ID directly from a URL/header as proof. Never use SECURITY DEFINER functions that trust spoofable session GUCs without independent authorization.

The reference SQL includes a tenant-read RLS policy with `NULLIF(current_setting(...), '')::uuid`, making missing tenant context reject access. A malicious SQL client can still forge the setting, so the policy does not replace the independent authority.

Because the authority must approve discovery, field predicates and document reads, **do not use only owner/share columns from BusinessDb** to grant access; they are local projections. Full content verification includes a current independently accepted manifest and permitted field set; querying by form record ID does not skip these checks.

## 6. Azure production infrastructure settings

- Two PostgreSQL instances in separate administrative domains. Private VNet access or correctly restricted private endpoints; DNS resolution and network deny rules validated from every workload. No `Allow public access from Azure services` broad exception.
- Restrict business API egress to business DB and the **authorization API**, never directly to AuthorityDb, SpiceDB datastore, or Managed HSM. Authority must not expose a general SQL/SpiceDB/HMAC admin API to the business network.
- Workload identity: use a separate user-assigned managed identity per production workload where practical; provision minimal PostgreSQL grants and Key Vault permissions. Explicitly review group-based admin grants, inherited Azure RBAC, service connection roles, backup restore and identity impersonation rights.
- DBA human access: privileged identity management, MFA/FIDO2, approval, short-lived access, recorded incident/change ticket. No standing pooled/shared admin account. Keep Dev/Test/Staging/Prod identities, secrets, databases and HSM keys separate.
- Database audit: enable PostgreSQL/pgAudit where supported, connection auditing, schema and privilege changes, role grants, RLS modifications, sensitive function execution, suspicious SELECT/UPDATE, and unexpected data export; redact query parameter/content where necessary. Stream promptly to separately administered append-only audit sink. Monitor loss/delay of audit pipeline.
- Harden backups, replicas, point-in-time restores, logical exports, blob/object store and test artifacts as data-bearing security assets. A DBA may be able to copy business plaintext using backups even if API authorization is protected. Recover independently anchored authority revisions and keys during restores.

## 7. Connection and migration contract

- Use **separate connection pools and connection strings per role**. Runtime app uses business API identity only, never migration or DBA identity. Projector uses its own credentials; authority uses only its own authority-store identity. App background jobs use dedicated least-privilege roles, not the migration identity.
- Read-only records: avoid direct raw SQL exposure in admin tooling; authorization layer must filter/verify before returning data. Migration scripts run as the NOLOGIN owner via a JIT-approved migration role. Validate schema privileges before and after migrations; no implicit grants on newly created functions/tables.
- All protection-bearing tables/columns have a documented *owner, approved writer, reader, RLS policy, signature/manifest rule and audit rule*. This inventory is generated from catalog queries and compared to an approved allowlist in CI. Newly introduced tables fail the release gate until classified.
- Unsigned legacy rows, an unknown schema version, failed owner/state verification or missing RLS policy => quarantine/deny, not "repair" or automatic resign. Security changes must never be applied solely by SQL update in business DB, even using a privileged migration identity.

## 8. Adversarial tests and release gates

Run the following tests **against the real production-equivalent managed PostgreSQL configuration**, with business DB admin privileges simulated in staging and authority/HSM in separate identities. A passing RLS test alone does not satisfy hostile-DB-write protection.

| ID | Attack / verification | Expected result |
|---|---|---|
| PG-01 | Runtime account tries CREATE TABLE, ALTER TABLE, ALTER POLICY, CREATE ROLE, GRANT, DISABLE TRIGGER, TRUNCATE, `SET ROLE` owner | Permission denied |
| PG-02 | Runtime account invokes `SET row_security = off`, attempts owner/BYPASSRLS role or view bypass | No unauthorized release; no RLS bypass privilege |
| PG-03 | Missing tenant context, wrong tenant context, reused connection with stale tenant | RLS denies/isolates; application authority denies incorrect tenant |
| PG-04 | Runtime SQL credential deliberately executes `SET LOCAL app.verified_tenant_id` to another tenant | Documented RLS spoof succeeds where policy permits, **but independent authority and signed checks still deny app-mediated access**; plaintext directly selectable by SQL credential is explicitly classified as residual confidentiality risk |
| PG-05 | Business DBA sets `owner_user_id` to attacker's GUID | App-mediated ownership denied; independent authority retains actual owner; tamper alarm |
| PG-06 | DBA inserts a forged team membership/share scope/tenant grant | Does not become independently authorized; revocation/graph revision unaffected |
| PG-07 | DBA replays valid old signed rows, deletes fields, replaces attachment references | Trusted manifest/revision check rejects altered/missing contents |
| PG-08 | DBA disables RLS or alters a local policy | Protected release still checks authority; independent audit alerts; DB data itself may be exposed/disrupted |
| PG-09 | DB runtime role attempts authority-cluster login, HSM Sign, SpiceDB write or ledger reset | Denied by network, workload identity and independent grants |
| PG-10 | Business DBA restores old local database and modifies a prior valid row | Accepted revision/root unchanged; old rows rejected |
| PG-11 | Migration identity inadvertently reused by API, unexpected role membership, elevated predefined roles | CI/startup inventory fails; runtime not deployed |
| PG-12 | New table/view/function created with unintended PUBLIC/default privileges or weak RLS | Migration privilege diff blocks release |
| PG-13 | Unsafe SECURITY DEFINER function with mutable search_path or exposed EXECUTE | Static review and database probe fail build |
| PG-14 | Export/admin read of ordinary unencrypted values or backups | Residual plaintext confidentiality risk demonstrated; tenant encryption floor documented and enforced |
| PG-15 | Compromise of ONLY business DBA credentials; production-equivalent Key Vault/authority boundaries | No way to grant a new authorized read, sign arbitrary authorization transition or change committed security revision |
| PG-16 | DB log sink cannot receive events; suspicious DDL or role grants | Alert/operational escalation; independently collected infrastructure events remain available |
| PG-17 | Connection pooling with rollback/crash, stale `SET` session state | Transaction-scoped context never leaks to another request |
| PG-18 | RLS enabled but FORCE missing, function-owner/table-owner bypass, unexpected `pg_read_all_data`/`pg_write_all_data`/`BYPASSRLS` | Build/deployment audit fails |
| PG-19 | Authority DB compromise simulation (separate higher-tier exercise) | Report Level 2 assurance boundary exceeded; initiate separate incident controls, no false guarantee |
| PG-20 | Full-content search result omission via hostile DBA data/index manipulation | Do not claim completeness until G06 trusted search service/projection is implemented and verified |

**G08 completion criterion:** architecture + vetted migrations and IAM policy + actual Azure PostgreSQL and identity setup + automated privilege-inventory checks + adversarial PG-01..PG-20 with preserved evidence + independent reviewer sign-off. Until then G08 remains **P0/open**.

## 9. References

- PostgreSQL 18 row-level security and bypass rules: https://www.postgresql.org/docs/18/ddl-rowsecurity.html
- PostgreSQL 18 predefined database privileges: https://www.postgresql.org/docs/18/predefined-roles.html
- PostgreSQL privileges and default grants: https://www.postgresql.org/docs/18/ddl-priv.html and https://www.postgresql.org/docs/18/sql-alterdefaultprivileges.html
- PostgreSQL safe SECURITY DEFINER functions: https://www.postgresql.org/docs/18/sql-createfunction.html
- Azure PostgreSQL access management: https://learn.microsoft.com/en-us/azure/postgresql/security/security-access-control
- Azure PostgreSQL Microsoft Entra authentication: https://learn.microsoft.com/en-us/azure/postgresql/security/security-entra-concepts
- Azure PostgreSQL private networking: https://learn.microsoft.com/en-us/azure/postgresql/network/concepts-networking-private

## 10. Dependencies and cross-references

This G08 policy **does not** replace `TRUST_AUTHORITY_ARCHITECTURE.md`, `CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`, `MERKLE_RECORD_MANIFEST_PROTOCOL.md` or `CRYPTOGRAPHIC_SIGNING_AUTHORITY.md`. It further constrains their deployment IAM and business/authority PostgreSQL account privileges. Local signing remains Vault Transit, production Azure Managed HSM. Next critical cross-cutting milestone after G08 is coordinated backup, restore and key/manifest recovery (G14).


## G14 recovery boundary

Restored BusinessDb/AuthorityDb servers and recovery automation need freshly audited identities, least privilege, RLS and independent operator roles; the business DBA must still be unable to reach authority security state, HSM keys, journal archives or recovery activation. SQL backups are not transactionally coordinated across clusters. A restored SQL session GUC or database owner does not establish identity. Recovery control plane uses independent immutable epoch fencing and dual approval; reference `COORDINATED_DISASTER_RECOVERY.md`.
