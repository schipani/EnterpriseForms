# Enterprise Forms Platform — Pre-Build Readiness Audit

**Reviewed:** 2026-10-17 specification baseline  
**Verdict:** **GO** for a limited Phase 0 repository baseline and security-first vertical-slice prototype. **NO-GO** for a one-shot instruction to build and deploy the full enterprise system unattended.  
**Nature of review:** Static architectural/specification review, not source-code security testing, penetration testing, deployment validation, or compliance certification.

## Reviewed evidence

- `CLAUDE.md` (1,041 lines), including §§4A–4U, the phases, release tests and ADR-ARCH-001.
- `CLAUDE-DESIGN.MD` (1,386 lines), including the 69 PG-### screens, per-feature ownership, architecture, global worker queue, uploads and accessible theming.
- `SECURITY_GAP_ASSESSMENT.md`, `SECURITY_THREAT_MODEL.md` and the Level 2 independent authorization, consistency, Merkle, HSM, database privilege and disaster-recovery protocols.
- Security simplification standard and plan, related-form collection design, SQL/OpenAPI/C# reference files, release notes and the most recent handoff ZIP.

The page IDs PG-001 through PG-069 are all present. Their inventory and feature ownership mappings are not a blocker. Default black primary buttons, per-tenant branding/color validation, per-section refresh, SPA-persistent uploads, and replacement-safe .NET contracts are adequately described as requirements; none is implemented or tested.

## Findings prioritized for action

| ID | Priority | Finding | Consequence if skipped | Required next action |
|---|---|---|---|---|
| PB-01 | **BLOCKER (handoff)** | **The latest `EnterpriseForms_Feature_Based_Architecture_Update.zip` holds just `CLAUDE.md` and `CLAUDE-DESIGN.MD`.** Both refer to independent security, trust authority, related-form, and migration documents. In the current extracted working directory, `security_hardening/SECURITY_CONTRACTS.reference.cs` and `security_hardening/audit-consolidation.reference.sql` are missing, though present in the older security-simplification ZIP. | Claude may implement contradictory partial contracts or invent missing security behavior. | Assemble a **single versioned canonical source package** containing both primary specs, normative dependencies, reference contracts, a README/manifest, SHA-256 checksums, precedence and file completeness validation. Keep historic ZIPs out of the active build root. |
| PB-02 | **P0 (delivery)** | **No explicitly prioritized first production release/MVP boundary.** A 69-screen UI, many security subsystems, multilingual forms, highly scoped sharing, and trusted search are all described as mandatory without a practical intermediate deployable slice. | Months of partially working scaffolding and security-critical workflows left unfinished. | Define milestones with one independently testable **golden path**: authenticated org bootstrap → create/publish simple form → create a record → authorized view/edit → attach/resume/scan/encrypt document → audit → revoke/share → hostile-DB test. Defer optional breadth, **not** required safety checks. Differentiate internal demo from Level 2 release. |
| PB-03 | **P0 (security feasibility)** | **Trust-authority protocol is specified but not validated running end to end.** External authorization journal, SpiceDB, authority database, HSM, ledger, Merkle, reconciliation, and revocation fencing create a distributed state machine. | An apparently functioning UI may conceal race conditions, grants after revocation, stuck pending changes, or incomplete recovery. | Make the first engineering spike a runnable reference slice with separate credentials/databases and failure injection for DB tampering, authority lag, crash/retry, revocation, signing outage and restore. Gate protected routes on real results. |
| PB-04 | **P0 (conflicting product promises)** | **The core spec advertises rich typed-value search; the current protection standard encrypts new free-text fields by default and forbids plaintext indexing for Confidential/Restricted data.** The distinction is stated but not converted to an explicit product support matrix. | Users will assume searches work for sensitive values; Claude may silently index plaintext or fabricate search completeness. | Decide field-classification × operator support (equals, contains, ranges, sort, facets, aggregate, exports). Define UI messaging for unsearchable protected values, bounded verified V1 search limit, and conditions for optional blind-index/V2 verified indexing. |
| PB-05 | **P0 (deployment architecture)** | **No complete tested full-stack Compose/IaC baseline.** There is a narrow `docker-compose.signing-dev.yml` for Vault, not a demonstrated reproducible BusinessDb + AuthorityDb + SpiceDB + Vault + API + Worker + Web + blob emulator + test IdP/scanner + audit/ledger test doubles, with isolated credentials. IaC/cloud networking and deployment promotion are not pinned down. | 'Works on my machine'; developer shortcut accidentally becomes production authority; inability to run realistic security tests. | Add complete local Docker Compose and environment profiles, migration/seed scripts, least-privilege roles, ephemeral secrets, health checks; select Bicep/Terraform for production with separate identities/networks and explicit non-production mocks. |
| PB-06 | **P0 (API contract)** | **Many APIs are described in prose, but the current project is not delivered with one authoritative versioned OpenAPI contract for the whole product.** Authority/related-form partial OpenAPI contracts exist. | Frontend and backend drift, incompatible endpoint assumptions, unsafe automatic retries, inconsistent error/authorization semantics. | Establish a full `/api/v1` contract inventory, shared ProblemDetails codes, pagination, typed projection/field masking, ETags, Idempotency-Key semantics, uploads/streaming/status, date/time serializations, and contract-generation CI. Own each operation by a feature. |
| PB-07 | **P1, spike before UI scale** | **The universal Web Worker request coordinator is an intentional complexity center.** The spec handles in-app navigation correctly and honestly disclaims closed-browser continuation, but the runtime/browser compatibility and integration with TanStack Query, BFF cookies and CSRF have not been proven. | Broken authentication, worker restarts, inaccessible progress, rerender/race bugs, request starvation, abandoned uploads. | Select supported browsers/versions; build a 2-GB or policy-limit chunked upload spike with reload/reselect/interrupt tests, navigation during upload, logout/tenant-switch revocation, worker fallback, cancel, per-card refresh and browser DevTools throttling. |
| PB-08 | **P1 (identity)** | **Production identity provider and step-up verification contract need a concrete selection.** Generic OIDC and FIDO2 are specified, but an application cannot infer phishing-resistant assurance just from an `mfa` claim. First-owner provisioning, account recovery and multi-organization switching are security critical. | Account lockouts or a false claim that passkey assurance was proven. | Choose initial provider (e.g., Microsoft Entra), exact tenant/subject binding, assurance verification, session length, dual-owner/break-glass and user recovery process. Use a separate mock provider for automated testing. |
| PB-09 | **P1 (product operations)** | **Quantified non-security operational targets remain unset.** DR doc deliberately leaves business RTO/RPO to measured targets. Upload size, tenant storage and record quotas, queue budgets, search ceilings, p95 page latency, and expected concurrency lack agreed defaults. | Arbitrary limit choices and unpredictable costs, latency and failure behaviour. | Approve initial budget/scale envelope (tenants, users/tenant, records/tenant, fields/record, files/record, max file size, uploads/user, queries/second); define p95/p99 SLOs and RTO/RPO for business data, then benchmark the HSM and ledger paths. |
| PB-10 | **P1 (privacy/compliance)** | **Data residency and classification obligations need organizational choices.** The system stores highly sensitive identity and possibly justice-related content; Azure Confidential Ledger can replicate to paired regions. Existing retention/legal-hold screens do not equal a documented regulatory decision. | Undisclosed cross-region replication, inconsistent retention/deletion, unauthorized telemetry/backup data movement. | Record geography and residency requirements for PostgreSQL, blobs, backups, ledger, analytics, keys and incident archives; select applicable privacy regime and retention/hold authority; review data processing agreements and tenant-level policy constraints. |
| PB-11 | **P1 (operations/security)** | **CI/CD and supply-chain security are required in general terms, but an enforceable implementation contract is not yet chosen.** | An insecure/unreproducible image or drifted migration may reach production. | Select container registry, signed immutable image digest promotion, SBOM, dependency and secret scanning, SAST/DAST, pinned build tooling, migration approvals, rollback and staged release/feature flags. Define deployment ownership and environment identities. |
| PB-12 | **P1 (assurance governance)** | **Hundreds of required test cases exist as text, but there is no live evidence register or team-approved risk owner matrix.** No actual app repository/source, deployed DB, scanner, IdP or cloud integration was supplied in this review. | Specifications mistaken for validated security. | Create a machine-readable security release matrix with requirement IDs, feature owner, test ID, evidence file, stage, result, exception and independent approver. Block claims of Level 2 readiness until hostile-DB, search omission, recovery and privilege attacks pass. |
| PB-13 | **P2 (product completeness)** | Bulk import/migration from external systems, tenant data portability, duplicate detection, business-level data archival and tenant offboarding are not fully productized. The current spec covers exports/retention but not a complete customer offboarding/migration journey. | Costly bespoke work when the first tenant migrates or leaves. | Decide whether first release needs CSV/API import, dry-run validation/mapping, safe export of metadata/form definitions and records, full tenant deletion/hold handling and key lifecycle. Plan feature modules if needed. |
| PB-14 | **P2 (design governance)** | UI tests and accessibility targets are specified, but there is no approved component showcase with final screen compositions or real user validation. | Claude may produce internally consistent but poor enterprise workflows for high-density forms and relationship lists. | Build and approve an interactive design-system gallery and three representative flows (dashboard, record with addresses/docs, designer) at 320/768/1440 px and large text; run human WCAG/AODA review before building all 69 screens. |

## Keep; do not redesign or drop

1. **Independent Level 2 Authorization Authority:** its administrative boundary, authoritative grant/revocation state and signed journal are the core hostile-DBA controls.
2. **Business/Authority PostgreSQL separation, enforced RLS and role isolation:** complementary, not duplicate controls.
3. **Vault Transit locally, Azure Managed HSM in production, provider abstraction:** Azure Managed HSM supports symmetric `oct-HSM` signing algorithms such as `HS256`; test provider-specific canonical-message expectations, key setup and disaster recovery.
4. **Merkle content completeness + anti-rollback + HMAC** until the explicitly required equivalence tests prove a narrower cryptographic scheme is safe.
5. **Queryable audit projection, independent replayable security evidence, technical telemetry and compact Confidential Ledger checkpoints:** different roles, not redundant copies.
6. **Feature-based vertical slices + one replaceable API contract + separately deployed authority.**
7. **The 69-page catalog and accessible branding rules** as long-term backlog; do not create all routes as empty placeholders at project start.
8. **Client-side global queue and server-side durable processing** as separate abstractions; file transfers survive SPA route changes, not guaranteed last-tab closure.

## Suggested pre-build decisions (record as ADRs)

| ADR | Required choice / default for a first implementation |
|---|---|
| ADR-001 | Launch boundary: internal Level 2 golden-path prototype first; full production readiness only after all mandatory gates pass. |
| ADR-002 | Identity: select one primary hosted OIDC/FIDO2 IdP, explicit assurance validation and recovery procedure. |
| ADR-003 | Search: initially support a **bounded authority-enumerated verified search**, with explicit rejected query limits; do not promise full-text search over default-encrypted content. |
| ADR-004 | Upload: choose server-proxied chunk streaming as a safe first mode or specify an audited private-Blob direct-upload protocol; never assume both. |
| ADR-005 | Browsers: select at least Chrome/Edge/Firefox/Safari versions supported by the upload coordinator and accessibility tests. |
| ADR-006 | Environments: local Docker Compose, isolated staging with real cloud identities/HSM/ledger, production promotion model and infrastructure-as-code tool. |
| ADR-007 | Quantitative envelope: agree initial storage, data, user, throughput, p95 and recovery targets; use limits at every layer. |
| ADR-008 | Residency/privacy: approve regions, disaster-recovery paired regions, backup retention, incident archives and sensitive-data policy. |
| ADR-009 | API: versioned OpenAPI + generated clients, ProblemDetails/ETags/idempotency and pagination conventions. |
| ADR-010 | Supply chain: select CI system, immutable signed image promotion, automated scans and migration approvals. |

## Phase-by-phase build gate — recommended Claude order

**Gate 0: source package and repo bootstrapping**
- Build one canonical bundle with both Claude specs and all normative dependencies; include manifest, checksums, precedence and missing-file CI. Include the two missing security reference files from the older bundle after reviewing their currency.
- Scaffold or inspect repository; pin SDK, npm/package manager and dependencies; implement directory/API naming and architectural tests; create `docs/architecture/FEATURE_OWNERSHIP_MATRIX.md`.
- Establish minimal CI running `dotnet build/test`, frontend lint/typecheck/tests, OpenAPI generation, Docker image build and schema-name rules.

**Gate 1: security-first thin vertical slice**
- Deploy local isolated BusinessDb/AuthorityDb, SpiceDB, Vault Transit and a minimal Authority/API/web trio with real service authentication and least-privilege DB credentials.
- Exercise authenticated org bootstrap, create/publish a simple form and record, authorize a record read, HMAC+Merkle integrity verification, independent evidence/journal, and revoked access.
- Inject forged owner/share rows, out-of-order revisions and outage; prove denial and idempotent reconciliation.

**Gate 2: document and UI viability**
- Implement global app shell, independently refreshing dashboard cards, accessible validated branding and a record screen with child related forms.
- Implement one resumable encrypted file upload through the coordinator, durable processing, malware scanner, signed manifest, authorized download and audit. Test route changes, re-selection and tenant switch.

**Gate 3: search, governance, recovery**
- Implement documented bounded verified search, restricted data classification and proof that default Confidential values aren't accidentally indexed in plaintext.
- Add canonical audited commands, multi-principal sharing/step-up and end-to-end disaster-recovery drill with frozen authoritative security state.
- Prove privilege isolation, ledger/archive reconstruction, revoked-member behavior and key/version restoration.

**Gate 4: breadth and production hardening**
- Complete remaining capability-approved pages by feature and release wave; measure HSM/ledger throughput and queue latency; add separate controlled operator UX.
- Test required adversarial cases, accessibility (automated + manual), backups/region behaviour, supply-chain approvals and concurrency at the agreed scale; complete approved release risk register.

**STOP / GO criteria**
- **GO for prototype:** package complete, basic Compose stack starts, contracts validate, auth bootstrap + authoritative read/write work under realistic test identities.
- **GO for pilot:** cross-system revocation, upload security, data classification/search behavior and basic recovery tested; restricted set of synthetic or approved pilot data; formal risk approval.
- **GO for production Level 2:** all required independent authority/journal/HSM/Merkle/Search/DR/privilege/evidence/identity/tenant and accessibility controls actually demonstrated and signed off. No speculative waivers of the defined hostile-BusinessDb threat guarantees.

## Verified current service feasibility (documentation review only)

- Azure Database for PostgreSQL Flexible Server currently supports PostgreSQL 18 (Microsoft Learn `concepts-supported-versions`). Check availability in the selected target region, required extensions and deployment options.
- Azure Managed HSM supports `oct-HSM` symmetric HMAC `HS256`/`HS384`/`HS512` operations (Microsoft Learn `about-keys-details`). Local Vault Transit is a transport/flow simulator, not a hardware security equivalent.
- W3C WCAG 2.2 AA specifies ≥4.5:1 contrast for normal text and ≥3:1 for relevant large text and essential non-text boundaries. Actual interactive states and accessibility tests remain required.
- Azure Confidential Ledger replicates to paired regions, which matters for data residency (Microsoft Learn `data-residency`).

## Bottom line

**The system is architecturally ready for a security-first, incremental build; it is not yet evidence-ready for production, and the handoff is not self-contained.** First fix packaging and operational choices, then have Claude implement and test one complete secure vertical slice before expanding to the 69-screen product.
