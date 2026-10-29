# Development-environment incident — browser tests against the persistent development organization (2026-10-29)

- **Status:** investigated read-only; **no remediation executed**; every cleanup below waits for the owner's explicit approval of the exact identities and operation.
- **Environment:** persistent development stack `enterpriseforms-dev` (web 8091, API 5280), organization `01a16640-87f6-7488-a1f1-664db54fbff3`. Development only; synthetic personas; no production or customer data.
- **Cause:** a Vite dev server already listening on `127.0.0.1:5173` (started 07:50 local, proxying to the persistent API) was reused by Playwright (`reuseExistingServer: true`). Corrected permanently (§6; `docs/PHASE_C7_STATUS.md` §31).

## 1. Method and evidence sources

All queries ran in read-only transactions (`default_transaction_read_only=on`) inside the stack's own database containers; nothing was written. Two independent sources were compared:

* **Authority database** (independently administered): `authority_resources`, `security_commands`, `record_manifest_commits`, `record_content_heads`.
* **Business database:** forms, records, shares, attachments, option lists, branding, policy, `audit_events`.
* **Backup** `~/Documents/EnterpriseForms-dev-backups/enterpriseforms-dev/20261025-083213` (created 2026-10-25 12:33 UTC): its business-database volume was extracted into a throwaway, network-less PostgreSQL container (removed afterwards) and only the branding row was read.

Exact identity lists: `docs/evidence/phase-c7/dev-incident-2026-10-29/` (`forms.csv`, `records.csv`, `option-lists.csv`, `attachments.csv`, `shares.csv`, audit/command/resource summaries). They contain identifiers and synthetic names only — no record values.

## 2. Incident window and attribution

* **Window:** 13:49:37–14:09:06 UTC (first and last audit event of the two local Playwright runs of this session: two encryption specs, then the full suite).
* **Attribution:** every form created in the window carries the Playwright name suffix (`… mvtl…`/`mvtm…`; 0 exceptions); every record created in the window belongs to one of those forms (0 records of other forms); every policy command in the window carries a Playwright reason text.
* **Correction of the first report:** the first, quick count used a 3-hour window and reported 30 forms / 45 records. The exact window holds **28 forms and 41 records**. The other two forms (and their four records) predate the runs: "C6 Lookup mvthgahy" at 11:51:37 UTC (a Playwright-named form, **not attributable** to this session's runs — origin unknown) and "Upload form" at 12:22:24 UTC (manual).

## 3. Inventory (window 13:49–14:10 UTC)

| Object | Count | Business source | Authority corroboration |
|---|---|---|---|
| Form definitions | 28 | `form_definitions.created_on` | 28 `form_definition` registrations, 28 `RegisterFormDefinition` commands |
| Published versions | 29 published / 26 registered with the Authority | audit | 26 `RegisterPublishedFormVersion` |
| Records | 41 (all of the 28 forms) | `form_records.created_on` | 41 `form_record` registrations, 41 `RegisterRecord` |
| Shares | 7 (5 active, 2 revoked) | `record_shares` | 7 `CreateShare`, 2 `RevokeShare` |
| Form access changes | — | — | 2 `UpdateFormUsers` |
| Field protection decisions | 59 | audit | 59 `PublishFormSecurityRevision` (57 requirements, 2 releases) — on the 28 synthetic forms |
| Attachments | 4 (2 records) | `file_attachments`, versions | blob objects in the development Azurite |
| Shared option list | 1 — "Marital Status mvtmcrze" (`01a187fe-0080-7d89-ab9f-b0fc903e4cd4`) | `option_lists` | (business-only catalog) |
| Organization data-protection policy | 8 updates (revision 10 → 18) | `organization_security_policies`, audit | 8 `UpdateMandatoryPolicy` |
| Organization logo | replaced (branding revision 7, 13:52:37 UTC) | `organization_brandings` | (business-only; **logo uploads are not audited** — finding F-C7-03) |
| Audit events | 428 business audit events | `audit_events` | Authority journal entries for each command |

**No pre-existing data was modified** (Authority evidence): 0 records registered before the window received an accepted manifest commit in the window, and 0 pre-existing record content heads changed in the window. In the business database, 0 audit events in the window reference a form or record that existed before it. Policy values after the runs equal those before (plaintext search permitted, minimum Internal, plaintext Internal fields permitted, removal of encryption not permitted).

## 4. Earlier occurrences found during the investigation

* **2026-10-23:** 97 Playwright-named forms were created in the same organization (two earlier sessions; policy commands at 22:02–22:03 and 23:35–23:36 UTC with Playwright reason texts). These are outside this incident's window and are listed by count only; they can be enumerated exactly on request.
* **Logo:** the 2026-10-25 backup's branding row is revision 3 (2026-10-23 23:33 UTC) and its logo is **byte-identical** to the spec's one-pixel PNG (SHA-256 `497790947d4666760ce38f3c00e852c71fdb66cae849bae8e9ede352719e1581`, 70 bytes) — the same as today. So the logo was already a test artefact since 2026-10-23. Revisions 4 (2026-10-25 12:43 UTC) and 6 (2026-10-29 12:17 UTC) are "branding updated" audit events; revision 5 left no audit event (consistent with an unaudited logo upload). **Whether a genuine logo was uploaded at revision 5 cannot be determined** from any available source.
* **Policy review note:** the Authority's command history shows the statement before today (revision 10, 2026-10-25) was already "Playwright mvllypdm: restored the development default after the encryption spec." — carried from the 2026-10-23 runs. There is no non-test statement to restore.

## 5. Remediation plan — proposed, not executed

Every step uses an existing, authorized, audited application operation. No direct SQL, no purge API, no history rewrite, no audit deletion. The owner approves each item (and the exact identities) separately.

| # | Item | Supported operation | Effect and what remains | Approval needed |
|---|---|---|---|---|
| R1 | Organization logo | Organization → Branding → upload (`POST /api/v1/organization/branding/logo`, organization owner) | New branding revision 8. The original logo is **not recoverable** (not in any backup; never audited). | The owner supplies the original PNG — or confirms there was none. There is no supported "remove logo" operation; adding one would be a small new feature (decision). |
| R2 | 41 synthetic records | Record lifecycle **Archive** (`POST /api/v1/form-records/{id}/archive` with `expectedContentRevision`/`expectedStatus`, by each record's owner persona; ids in `records.csv`) | Hidden from My Records by default; reversible (Unarchive). Each archive is a new accepted revision; Authority registrations, history, audit and blobs remain (immutable by design). | Approve the exact list. Records already archived are skipped. |
| R3 | 28 synthetic forms | **No supported operation** archives, retires or deletes a form definition. Option: revoke their form users (`POST /api/v1/form-definitions/{id}/access`) so no one can create records. | Forms stay in the Forms list. | Approve leaving them, revoking users, or a future "archive form" feature (decision). |
| R4 | Option list "Marital Status mvtmcrze" | Update (`PUT /api/v1/option-lists/{id}`): deactivate its items and/or rename with a "[test]" prefix | Kept (option ids are permanent); no longer offered. | Approve. |
| R5 | 5 active synthetic shares | Revoke (`POST /api/v1/form-records/{id}/shares/{shareId}/revoke`, owner) | Optional: the records are synthetic and archived by R2. | Approve or skip. |
| R6 | 4 attachments | None needed: they belong to synthetic records (R2); no supported deletion exists. | Ciphertext blobs remain in development storage. | — |
| R7 | Policy review note | Ordinary policy update (`PUT /api/v1/organization/security-policy`) with the **same values** (`minimumFieldClassification: Internal`, `plaintextInternalFieldsPermitted: true`, `plaintextDowngradePermitted: false`, `plaintextSearchPermitted: true`) and a statement the owner writes, `expectedPolicyRevision: 18` | New revision 19 (Authority `UpdateMandatoryPolicy`); revisions 10–18 and their audit stay. | The owner's statement text. |
| R8 | 2026-10-23 artefacts (97 forms, their records) | Same as R2–R4 after an exact inventory | — | Request the inventory first. |

Execution, once approved, is done through the browser as the respective persona or a reviewed one-off script that dry-runs first and prints exactly the ids it would change. Nothing is executed by this report.

## 6. Prevention (implemented; `docs/PHASE_C7_STATUS.md` §31)

Playwright now uses a dedicated port (5199) that is never reused, refuses persistent endpoints (8091, 5280, 5080) at configuration load, and its global setup proves — through the browser's own base URL, with GET requests only — that the API and its Authority are the isolated Testing stack (`/api/v1/testing/stack-identity`, Testing environment only) and that no persistent organization is served; `signIn` refuses any origin that was not verified. A Playwright-level regression reproduces the misrouting and shows the run stops before its first write.
