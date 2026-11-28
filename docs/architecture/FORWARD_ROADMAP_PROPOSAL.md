# EnterpriseForms — approved development roadmap (2026-11-25)

**Status: APPROVED as the development roadmap by the owner (C-193: RM-1…RM-5).** Prepared in the C9 close-out work package
from repository evidence on `main` `fbf18bc`. What is approved is the **structure, naming, sequencing and cross-track
dependency model**. Approval of the roadmap is **not** authorization to implement any milestone, merge, deploy, change a
persistent environment, activate a restricted capability or run Stage B: each milestone (and each ACT gate) still starts only
on its own explicit owner authorization, as before. Assigning a requirement or finding to a milestone does **not** close it.
(The file name keeps "PROPOSAL" for link stability; this header governs.)

This is the first forward-plan document in the repository; it does not replace any ADR, the pre-build decision register,
the security backlog (`docs/security/SECURITY_BACKLOG.md`, PP-01…PP-15) or the gap assessment (G01…G26). Where they
differ, those sources govern and this document must be corrected.

## 1. Identifiers (checked for collisions)

| Proposed | Use | Collision check on `fbf18bc` |
|---|---|---|
| **Phase C10 … Phase C19** | Functional milestones | No "Phase C1x" exists. Decision ids `C-10…C-19` exist, so always write "Phase C10", never "C-10" |
| **PRD-1 … PRD-8** | Production-readiness milestones | No `PRD-n` exists. The requested names P1…P8 are **not** used: P0/P1/P2 are priority classes throughout G01…G26 and PP-01…PP-15, "Phase P1/P2" are SEC-BL-01 D+ phases, and P1…P8 are SpiceDB probe ids in the C8 M4-1 design |
| **ACT-1 … ACT-n** | Persistent-environment activation gates (each needs its own owner authorization) | No `ACT-n` exists |
| **C9-CO** | C9 close-out and baseline health | No collision |
| **D10-n** | Phase C10 owner decisions | No collision |

Sub-milestones (M1, M2, …) are scoped by their phase ("Phase C10 M1"), as in C1…C9.

## 2. Status vocabulary

Every milestone and requirement is reported at five separate levels, never inferred from one another: **code-complete**
(Development/Test) → **merged** to `main` → **owner development acceptance** (recorded decision) → **independently
validated** (a genuinely independent reviewer or a staging test with real Azure services) → **production-ready** (PRD-8
release authorization). A merge is not acceptance; an owner-led review is never described as independent.

Blocker classes: **dev** (blocks development progress), **stg** (blocks staging), **pers** (blocks activation in a
persistent Development/Test environment), **prod** (blocks production).

## 3. Functional track

| Milestone | Scope | Depends on | Exit evidence |
|---|---|---|---|
| **C9-CO** | C9 development acceptance (**decided, C-193 C9-A**); documentation reconciliation (this package); baseline health BH-1 (benchmark environment reporting, sweep-quiet disposable stacks, hosted search-catalogue and RB-13 shard run — the hosted run needs its own authorization); P-S1-1 deferred to PRD-4 (BH-2); Dependabot alerts; the §11 #1…#82 register (`docs/testing/CLAUDE_MD_S11_REQUIREMENTS_REGISTER.csv`) | — | Close-out package integrated; BH-1 integrated; hosted run executed or explicitly deferred |
| **Phase C10** | Sharing, ownership and access completion (WP-0: `PHASE_C10_WP0_SHARING_OWNERSHIP_PROPOSAL.md`, design approved C-193 and C-194; **implementation complete and integrated into `main` `5cc4efe3` (PR #88, C-202); formal Development/Test acceptance pending; not independently validated; production readiness not approved**; outstanding obligations WP-0 §10.9 and §10.12): share expiry enforced at access time, in-place scope update with share revision and `If-Match`, delegation lineage with a bounded depth, descendant invalidation, document-level shares, journal-sourced history, ownership transfer (PG-022, PG-023, PG-053, PG-066); D9-3 resolved for new endpoints; SRC-1 page-id comment corrections with the first implementation package | C9-CO; PRD-1 for step-up in staging/production (G27) | §11 #23–#27, #56–#62; SEC-08, SEC-11; Stage B + Playwright + axe |
| **Phase C11** | Notifications, task inbox (PG-009), access requests and invitations (PG-065), welcome / first use (PG-004) (WP-0: `PHASE_C11_WP0_NOTIFICATIONS_ACCESS_REQUESTS_PROPOSAL.md`, **proposed**, awaiting owner approval and OD-C11-1/OD-C11-2, C-203; implementation not authorized) | Phase C10 (requests act on shares); in-app invitations need PRD-1 (D8-3(b)) | Request flow without existence disclosure (PG-065 rule) |
| **Phase C12** | Export Center (PG-068), personal exports and jobs (PG-026); PB-13 export rows; G10 revocation during long exports and downloads | Phase C10 (scope), durable jobs | PB13 export rows executed; G10 tests |
| **Phase C13** | Import Center: CSV/JSON mapping, dry run, idempotent durable jobs, trusted relationship and document handling (PB-13) | Phase C12 job infrastructure; Phase C10 ownership | PB13 import rows executed |
| **Phase C14** | Localization and form translations (PG-036, §4F); organization catalogs and availability policies (PG-044, §4G); dependent mappings (PG-045); D9-9a/b; **G21 design** (signed option catalogs and form-version metadata — catalogs are reworked here anyway) | Phase C10 not required | §11 #47–#55; G21 design accepted for review |
| **Phase C15** | Record challenges (§4C.5 PIN/passphrase, unlocks) and step-up UX (PG-005; PG-024 "Record security and additional access", wholly, as catalogued) | PRD-1 for real provider step-up; Development step-up only before that | §11 #34, #35 |
| **Phase C16** | §11 #13 design and disposition (global free-text and FieldConcept semantic search, within ADR-PB-004; C-194); remaining functional pages and lifecycle: form archive/retire (R3), field deprecate/retire, retention and legal hold (PG-067, G16), purge workflows (DR-C7-4, G12 functional part), FR-C6-02/05/06, AC-3, DR-C7-1, DR-C7-5, F-C7-03, download completed/failed audit, SD-1…SD-4, PG-013/014 wizard/018, PG-057/059, PG-062/063, PG-064, PG-071; designer backlog (nested repeating groups, cross-section drag, drag-to-resize, dark mode) | Phase C12 for exports before purge | §11 #12, #20 retention parts |
| **Phase C17** | Organization offboarding (PB-13: request → verify → freeze → export → hold/retention → revoke → dispose/retain → receipt) | Phases C12, C16; PRD-3 for evidence storage in staging | PB13 offboarding rows |
| **Phase C18** | Version-specific help and knowledge system (PG-011; §5) | Content from C10…C17 | Searchable help per released version; content review against the exclusion rule |
| **Phase C19** | Integrated functional acceptance: complete §11 #1…#82 register with results; PB-14 manual NVDA/VoiceOver and real zoom (all open PB14 rows); product/design sign-off (BUILD-06, PB14-32); PB-07 browser tests; hosted Stage B on the integrated code (release authorization, C-131); owner milestone acceptances still open (C8 M4-5, C9 if deferred, form rename, C8 M3) | C10…C18 | Every §11 row MET with evidence or explicitly deferred by the owner |

## 4. Production-readiness track

| Milestone | Scope | Depends on | Exit evidence |
|---|---|---|---|
| **PRD-1** | PB-08: Entra .NET BFF (SB-2), provider step-up and FIDO2 authentication strength, `acrs` mapping, session registry production key ring, tenant switching (PG-003), FIDO2 policy page (PG-047), G09, G22/G23, G26 PIM approvers, SD-5, SB-5; unblocks retained approvals (C-188 D-H5) and security commands in staging | Entra tenant (owner-provided) | PB08 identity tests executed against real Entra |
| **PRD-2** | PB-11: Bicep, Container Apps, ACR, signed digests and attestations, Entra OIDC federation with segregated identities (SB-4, BUILD-05 part, PB11 CSV, G15 supply chain, PP-03 part); BUILD-00 deferred controls; BUILD-00-ADO migration decision | Azure subscription (owner-provided) | PB11 assertions executed |
| **PRD-3** | Trust services: Managed HSM MAC and seal providers (SB-1, SB-7), managed KEK provider with per-organization purpose keys, rotation and re-wrap (SB-3), Confidential Ledger, independent immutable archive and verification stores (SB-9, PP-01, PP-02, G11), storage managed identity, blob immutability, internal TLS; custody of the C-191 abandonment attestation key; G24/AUD-C7-01 | PRD-2 | SIG/ledger tests on real services |
| **PRD-4** | Staging deployment (SB-10) and Level B validation: SEC-P01…P12 in Azure, G08 PG-01…PG-20 on real servers, PB-09 benchmark matrix, PERF-01, ST-PERF-1…7, R-02, PP-08/09/11/12, G17, UF-3/4/6/7, DR-C7-2/6, SB-6, BUILD-03/04, F-05; PB-10 residency tests; **P-S1-1 and its O-1/O-2 remediation (deferred here by BH-2, C-193; the finding, its fail-closed refusal, evidence and remediation obligations are unchanged)** | PRD-1…PRD-3 | Staging evidence bundle |
| **PRD-5** | Recovery and remaining integrity obligations: G14 coordinated recovery (DR-01…DR-36, SB-8, PP-05, UF-10), PP-04/06/07/10/13, SEC-BL-01 (M2F3-43 FAILED), PD-15 sub-items, BD-1/BD-2, F-E2, OBS-IX-1/AP-3, RB-13 datastore-restore case, G12, the C-191 `RecoveryBlocked` repair procedure | PRD-3, PRD-4 | DR tests in staging; SEC-BL-01 closed with evidence |
| **PRD-6** | PB-12 release evidence chain, release evaluator, read-only Release Security Dashboard, PB12 tests | PRD-2, PRD-4 | PB12 tests executed |
| **PRD-7** | Independent assurance: cryptographic review (BUILD-02, SIG-11), penetration test (G15), PP-14, independent reviews of AC-5…AC-9, AC-8.1–8.5, M4-1, D8-66, G06, G19, G20, G21, G25, UF-2, C-12/C-28; independent accessibility validation (WCAG 2.2 AA/AODA) and privacy sign-off (PB-10) | PRD-4, Phase C19 | Signed reports; findings dispositioned |
| **PRD-8** | Final production readiness: PP-15 formal security acceptance, Level 2 certification, reviewed organization data-protection defaults (STAGE_B_STATUS §7.1), release authorization | All | Explicit owner release authorization |

## 5. Persistent-activation gates (each separately authorized; no milestone implies them)

| Gate | Item | Prerequisite |
|---|---|---|
| ACT-1 | C-188 retained-field approvals in a persistent Development environment | Owner acceptance of C-189…C-191; BF-1 business inventory (read-only); Authority-first release; single Authority instance; migrations applied to the persistent Authority (now at `20261101221445_OwnerRecovery`) |
| ACT-2 | Indexed search rollout (OD-IX-2) | Owner decision; staging blockers SB-1…SB-10 for anything beyond development |
| ACT-3 | Envelope v2 and the BD-1 restore-resistant barrier | BD-1/BD-2 design closure |
| ACT-4 | C-74 development-incident remediation R1…R8 | Exact inventory (R8); R3 needs the Phase C16 form archive |
| ACT-5 | Contributor writes (`Content:ShareContributorWritesEnabled`) outside disposable stacks | G20 review |

## 6. Cross-track dependencies

- PRD-1 gates every high-assurance functional feature in staging/production: retained approvals, step-up for security
  commands, record challenges (Phase C15), invitations (Phase C11), exports requiring step-up (Phase C12).
- PRD-3 gates Phase C17 evidence (offboarding receipts in immutable Canadian storage) and every key-custody question raised
  by Phases C10…C13 (no new deployment secret without a PRD-3 custody design).
- PRD-4 is the only place performance claims can be made; development benchmarks are engineering signals.
- Phase C19 feeds PRD-7 (accessibility and functional evidence) and PRD-8.
- Security fixes found in functional milestones stay in the milestone that found them, as in C-188/BF-1.

## 7. Owner decisions for the roadmap (decided, C-193)

| Id | Decision | Outcome |
|---|---|---|
| RM-1 | Adopt the identifiers of §1 (`Phase C10…C19`, `PRD-1…8`, `ACT-n`) instead of P1…P8 | **Approved** |
| RM-2 | Cross-track dependency model; tracks run in parallel once Azure and Entra access exist, functional first until then | **Approved** |
| RM-3 | G21: design in Phase C14, independent review in PRD-7 | **Approved** |
| RM-4 | D9-3 concurrency convention resolved in Phase C10 | **Approved** |
| RM-5 | Create the CLAUDE.md §11 #1…#82 requirements traceability register | **Approved**; created as `docs/testing/CLAUDE_MD_S11_REQUIREMENTS_REGISTER.csv` (C9-CO); a **mandatory governance artifact** from C-194: every milestone updates its rows (§9) |

Related C-193 decisions: C9-A (C9 accepted for Development/Test only), C-192 (PR #76 merge record confirmed as code
integration), BH-1 (test-only baseline remediation), BH-2 (P-S1-1 O-1/O-2 to PRD-4), SRC-1 (page-id comment corrections with
the first C10 implementation package), Phase C10 design direction (D10-2 and the delegation depth still open).

## 8. Requirements-to-milestone traceability (consolidated)

Sources: inventory of 2026-11-25 (gap assessment, security backlog, status documents, decision log, test registers,
router). "Class" uses §2.

| Area | Items | Milestone | Class |
|---|---|---|---|
| C9 residuals | §11 #2: persistent activation (ACT-1), staging/production validation (PRD-1 step-up, PRD-4), full acceptance; PB14-44…48 manual review; D9-9 deferrals; D9-3; `expectedLayoutVersionId` exception | C9-CO (decision), C14 (D9-9a/b), C10 (D9-3, D9-9c), C19 (manual review) | dev |
| C-188 rollout | BF-1 inventory, release order, single instance, abandonment key custody, `RecoveryBlocked` repair, PB-08 step-up | ACT-1, PRD-3, PRD-5, PRD-1 | pers/prod |
| Sharing and ownership | PG-022/023/024/053/066, PG-051, per-form administrators, D8-8, C3 repeating-entry shares, C5 V1 contributor limits, G20, FR-C6-01/03/04, SEC-08, SEC-11, REL-11/12/33 | Phase C10 (G20 review: PRD-7) | dev |
| Collaboration UX | PG-009, PG-065, PG-004, D8-3(b) | Phase C11 (D8-3(b) after PRD-1) | dev/prod |
| Data lifecycle | PG-068, PG-026, BUILD-07, PB13, G10 | Phases C12, C13, C17 | dev/prod |
| Localization and catalogs | PG-036, PG-044, PG-045, §4E–§4G, G21 | Phase C14 | dev |
| Challenges and step-up | §4C.5, PG-005, S5, PG-024 challenge part | Phase C15 + PRD-1 | dev/prod |
| Lifecycle, retention, remaining pages | R3, field retire, PG-067, G16, G12, DR-C7-1/4/5, F-C7-03, FR-C6-02/05/06, AC-3, SD-1…SD-4, PG-013/014/018/057/059/062/063/064/071, designer backlog | Phase C16 | dev/prod |
| Help | PG-011, §5 of the C9-CO package | Phase C18 | dev |
| Acceptance | §11 register, PB14 manual rows, BUILD-06/PB14-32, PB-07 tests, hosted Stage B, owner milestone acceptances | Phase C19 | dev |
| Identity | SB-2, SB-5, PB08, G09, G22, G23, G26, SD-5, PG-003, PG-047, PP-03 | PRD-1 | stg/prod |
| Delivery | SB-4, BUILD-05, PB11, BUILD-00, BUILD-00-ADO, G15 supply chain | PRD-2 | stg/prod |
| Trust services | SB-1, SB-3, SB-7, SB-9, PP-01, PP-02, G11, G24/AUD-C7-01 | PRD-3 | stg/prod |
| Staging and performance | SB-6, SB-10, BUILD-03/04, F-05, PB-09, PB-10, PERF-01, ST-PERF-1…7, R-02, PP-08/09/11/12, G17, UF-3/4/6/7, DR-C7-2/6, P-S1-1 | PRD-4 | stg |
| Recovery and integrity | G14, DR-01…36, SB-8, PP-04/05/06/07/10/13, SEC-BL-01, PD-15, BD-1, BD-2, F-E2, G08, OBS-IX-1/AP-3, RB-13 (restore case) | PRD-5 | prod |
| Evidence | PB-12, Release Security Dashboard | PRD-6 | prod |
| Independent assurance | BUILD-02, SIG-11, G06, G19, G20, G21, G25, UF-2, PP-14, AC-5…AC-9, AC-8.1–8.5, M4-1, D8-66, C-12, C-28 | PRD-7 | prod |
| Release | PP-15, Level 2 certification, §7.1 data-protection defaults | PRD-8 | prod |
| Maintenance | Dependabot alerts (zod high ×3, seroval ×2, minimatch); tracker hygiene (screen matrix, S0–S6 status, SIM/REL test references, CONS/MRK/SIG/DR coverage) | C9-CO, then each milestone | dev |
| Persistent activation | OD-IX-2, Envelope v2/BD-1, C-74 R1…R8, contributor-write switch | ACT-1…ACT-5 | pers |
| Development investigations | Stack-age load (search-index sweep at the overlay's 10 s interval), database connection growth (C-177), SearchIndexBuilderRunner cancellation flake, transient-answer-after-commit for approvals (C-190 note) | C9-CO | dev |

**CLAUDE.md §11 #13 (placed by C-194):** global free-text search and `FieldConcept` semantic search across mapped fields are
a **mandatory design and disposition item of Phase C16**. No `FieldConcept` code exists, and ADR-PB-004 limits V1 search to
approved Public/Internal fields. The assignment does not authorize a search architecture that conflicts with ADR-PB-004;
any conflict between the original requirement and the approved security architecture needs an explicit owner decision. Business-table RLS
(§11 #36; G08) sits in PRD-5 as part of G08.

Items that do not fit cleanly and are placed by recommendation, not by a natural fit: G21 (security design, RM-3), D9-3
(platform convention, RM-4), the persistent-activation gates (§5), Dependabot and tracker hygiene (C9-CO), the designer
backlog (Phase C16).

## 9. Help and knowledge system (Phase C18) — requirements to confirm

No repository requirement for version-specific help exists today (PG-011 is "Help, accessibility and support" only).
Proposed requirements, for owner approval:

1. Searchable, human-readable guidance organized by functional area (forms and designer, records, sharing and ownership,
   documents, search, administration, security operations, imports/exports, offboarding).
2. Version-specific: every article states the product release it describes; the help shown matches the deployed release;
   earlier releases' articles stay readable.
3. Each process article covers purpose, prerequisites and permissions, dependencies, steps, outcomes, decision guidance
   ("which option should I choose"), and what the system will refuse and why (in user terms).
4. Exclusion rule: no proprietary or security-sensitive implementation detail (no key names, cryptographic parameters,
   internal endpoints, signing or journal internals, detection thresholds, error-code-to-cause maps that aid an attacker).
   Each article passes a content review against this rule before release.
5. Authored as versioned content in the repository (reviewed like code), rendered by the `support` feature; no external
   help service holding customer data (PB-10).
6. Accessible (PB-14), translatable (Phase C14), and permission-aware (an article about an operation the user cannot
   perform may be shown, but never reveals organization data).

## 10. Requirements register governance (mandatory, C-194)

`docs/testing/CLAUDE_MD_S11_REQUIREMENTS_REGISTER.csv` is a mandatory project governance artifact. At every milestone the
affected rows are updated in the same change, with exact supporting evidence; the original criterion text is preserved; the
row distinguishes implementation, evidence, development acceptance, persistent activation, staging validation and full
acceptance; unresolved conflicts and obligations are kept; acceptance is never inferred from a merge or a passing test
alone. Rows #7–#82 stay preliminary (evidence pointers, not verdicts) until their evidence is reviewed.
