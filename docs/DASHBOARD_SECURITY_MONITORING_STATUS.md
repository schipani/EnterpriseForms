# Dashboard redesign and security monitoring — status (C-90)

Branch `ui/dashboard-security-monitoring`, based on `ui/consistency-unified` (5e3f7d3). **Owner visual acceptance
recorded 2026-10-30 (C-91, §6).** Not merged: a signed pull request into `main` and its required hosted checks are
still outstanding. This is a development feature, not production Level 2 evidence.

## 1. What was delivered

| # | Deliverable | Where |
|---|---|---|
| 1 | Organization sidebar 13.5rem → 16rem on every Organization page; sentence-case labels ("Security administrators"); labels stay on one line in the mobile strip | `features/organizations/components/OrganizationLayout.tsx` |
| 2 | PG-006 dashboard: heading with organization and date, period selector (7/30/90 days), 7 KPI tiles, records-by-status donut, record-activity columns (created vs submitted), upload-activity stacked columns | `features/dashboard` |
| 3 | Four searchable, independently server-paged sections: My records, Upload activity, Recent personal activity, Permitted forms | `features/dashboard/components/DashboardSections.tsx` |
| 4 | 15 rows per page everywhere (server default and every client request); Previous/Next, position, totals, page count, disabled ends | `packages/ui` `Pager`; `OffsetPageCursor.DefaultPageSize` |
| 5 | Section Refresh buttons removed; sections load on open, page change and filter change (free text debounced 350 ms); no polling | dashboard |
| 6 | Security monitoring section (Security Administrators only): 5 KPIs, incidents-by-status donut, detections-over-time columns, incident list | `features/security-operations` |
| 7 | Persistent incident detection and storage (business-side triage store) | see §3 |
| 8 | PG-056 Security incidents page and incident detail (notes, investigator, status, resolution, detection history, audit trail) | `/app/security/incidents` |

Charts are dependency-free SVG/CSS components in `packages/ui` (`DonutChart`, `ColumnChart`): fixed validated palette
tokens (`--ui-chart-1..4`, validated with the data-viz palette checker: adjacent CVD ΔE ≥ 9.1), legends with values, a
keyboard-readable tooltip (focus the plot, then arrow keys / Home / End), a "Show data table" alternative, explicit empty
states and no animation. Controls use the unified standard (8px controls, 12px panels, 40px inputs, 14/13/12px text).

### Server APIs (all `/api/v1`, opaque 15-minute cursors bound to caller, organization, list and filters)

| Endpoint | Data boundary |
|---|---|
| `GET dashboard/record-statistics?periodDays&timeZone` | complete Authority discover set (same boundary as My Records); status/dates are listing metadata, labelled unverified |
| `GET dashboard/upload-statistics`, `GET dashboard/uploads?q&status&from&before` | the caller's own upload sessions only |
| `GET dashboard/activity?category&outcome&from&before` | the caller's own audit events only |
| `GET dashboard/forms?q&access&status` | forms the Authority lets the caller design or use (existing per-form decisions) |
| `GET security/incidents?q&type&severity&status&from&before`, `GET …/summary`, `GET …/{id}`, `PATCH …/{id}`, `POST …/{id}/notes` | Authority `administer_security` (Security Administrators); 403 otherwise |

My records reuses the existing `GET /form-records` (exact REC-/ID lookup semantics unchanged) with `pageSize=15`.

## 2. Authorization (no Authority change)

- Every incident operation calls the Authority for the **existing** `administer_security` permission on the organization
  (relation `system_administrator`, shown as Security Administrator). No permission was added or broadened.
- Organization owners, designers, clerks/members and readers receive 403 and no counts; the dashboard does not request
  security data for them (navigation hints only; the server decides).
- A record identifier or reference is returned only when the viewer may discover that record (Authority batch check);
  otherwise the incident shows "Withheld (no access)". Searching by a record ID or REC- reference matches only
  discoverable records, so search cannot reveal which records have incidents.
- Investigators can be assigned only from the Authority's Security Administrator roster.

## 3. Incident detection and storage

- **Detection points:** `VerifiedRecordReader` (record open and earlier-version reads) and historic document-version
  verification. Classification: a false `Verified` from the Authority (other than the NO_COMMITTED_CONTENT / HEAD_CHANGED
  races) → *Confirmed integrity mismatch* (Critical); `INTEGRITY_QUARANTINE` → *Integrity quarantine* (High);
  `AUTHORITY_UNAVAILABLE` / `KEY_UNAVAILABLE` → *Verification service unavailable* (Medium, one incident per error
  code); a missing committed header after verification → *Operational verification failure*. `AUTHORITY_NOT_READY`
  (for example awaiting the archive receipt right after an edit), not-found and permission errors are not incidents.
  A type never asserts malicious DBA activity.
- **Fail closed first:** the existing refusal is unchanged; the recorder runs before the existing `throw`, in its own
  transaction with a 5-second budget independent of the request, and never throws (failures are logged). Record content
  is never stored: only codes, identifiers, revisions and times.
- **Deduplication:** one problem key per record (or per outage code), serialized with a transaction-scoped advisory lock;
  a partial unique index allows one Open/Investigating incident per key. Repeats are counted; history rows are sampled
  at most once a minute per incident. A quarantine refusal after an incident was closed is counted on that closed incident
  without reopening it; a fresh confirmed mismatch after closure opens a new incident.
- **Tables** (owned by SecurityOperations): `security_incidents`, `security_incident_occurrences` (insert-only),
  `security_incident_notes` (insert-only). The runtime role may update only status, assignment, resolution, counts and
  revision columns; a schema-owned trigger keeps identity/evidence columns immutable and counts, last detection and
  revision monotonic. Migration `20261030212807_SecurityIncidentMonitoring` (additive).
- **Investigation:** status Open → Investigating → Resolved / False positive (closing requires an explanation; reopening
  is refused when a newer active incident exists), optimistic concurrency (`409 INCIDENT_MODIFIED_CONCURRENTLY`), audited
  as `SECURITY_INCIDENT_DETECTED / _VIEWED / _STATUS_CHANGED / _ASSIGNED / _NOTE_ADDED` (note text never in audit metadata).
  Resolving never lifts the Authority quarantine or changes verification.

### Trust limitation (documented, not hidden)

The triage store is **business PostgreSQL**: a business-database administrator can alter or delete incident rows (the
trigger and grants stop the runtime role, not a DBA). It is therefore **not** tamper-proof evidence. The independent
evidence of a confirmed mismatch already exists: the Authority quarantines the record head and appends a hash-chained,
archived `IntegrityVerificationFailed` journal entry (its correlation id equals the incident's first correlation id).
The application cannot yet read those journal entries back — see SD-1.

Also not covered: problems the Authority detects without a business request (for example the protection-verifier census)
appear only when someone next opens the record; record lists and search do not verify content and so record no incidents.

## 4. Decisions requiring owner approval (none implemented)

| ID | Decision needed | Proposal |
|---|---|---|
| SD-1 | Independently durable incident evidence and investigation history | **AC-10** (Authority change): read-only `POST /internal/v1/integrity-incidents/query` returning journal-sourced incidents of the caller's organization (record id, accepted revision, failure code, observer, journal stream/sequence, entry digest, archive state), gated by `administer_security`, so the page can cross-check the triage store against the journal and show census detections; plus an Authority command `RecordIncidentDisposition` (status/assignee/resolution digest) journaled like other security commands so investigation history is tamper-evident. |
| SD-2 | Organization-owner security summary | No explicit authority exists today, so owners see nothing. If wanted: new organization permission `view_security_summary = organization_owner + system_administrator` returning counts only (no lists, no record identifiers). Authority schema change. |
| SD-3 | Auditor access (future role, C-78 condition) | Not implemented. Proposed contract: organization relation `security_auditor`, permission `view_security_incidents = system_administrator + security_auditor` (read-only list/summary/detail; no PATCH/notes), granted by the existing two-approval role process. |
| SD-4 | Investigation notes and resolution text are stored as plaintext business data | Accept as administrative metadata (current behaviour, with on-screen guidance not to include record content) or require application encryption with a dedicated key purpose. |
| SD-5 | PB-08 step-up for investigation changes | Not available (same residual as other administration actions). |
| SD-6 | Removing per-section Refresh buttons departs from ADR-PB-007 ("each data-backed widget has an independent accessible refresh action") and the PG-006 spec ("refresh each card separately") | Record an owner amendment: dashboard sections refresh on open, page and filter change; a failed section keeps its own Try again button. |

## 5. Tests (executed 2026-10-30 on this branch)

| Suite | Result |
|---|---|
| .NET contract/unit (`EnterpriseForms.Api.ContractTests`, incl. 31 new `DashboardSecurityMonitoringTests`; OpenAPI snapshot, endpoint ownership) | 449 / 449 passed |
| Architecture (`EnterpriseForms.ArchitectureTests`: naming, boundaries, ownership matrix, signed-column registry) | 71 / 71 passed |
| Stage B integration on the isolated sectest stack, filtered: new `SecurityIncidentTests` + existing `AuthorizationAndTamperTests` | 5 / 5 passed |
| Stage B regression of verified-read paths: `RecordHistoryTests`, `DocumentTests`, `RecordWorkspaceTests`, `AdministrationTests`, `RecordLookupTests` | 22 / 22 passed |
| Frontend vitest (incl. new `DashboardWidgets.test.tsx`, `DashboardPage.test.tsx`, `incidentModel.test.ts`) | 257 / 257 passed (34 files) |
| ESLint (architecture import rules), TypeScript, production build | clean / clean / built |
| Playwright on the isolated stack: `dashboard.spec.ts` (4), `organization-administration.spec.ts` (5), `security-administrators.spec.ts` (2), `ui-consistency.spec.ts` (1), `responsive.spec.ts` (2) | all passed |
| Playwright `pb14-accessibility.spec.ts` | 1 passed, 2 failed on the known data precondition "an Accused record visible to the persona" (fresh sectest database); not caused by this change |

`SecurityIncidentTests` proves on the real stack: a hostile `field_date_values` update is refused (423) and never displayed;
four refused reads produce **one** Critical incident with every detection counted; the triage row holds no record values;
the Security Administrator sees it with the record withheld and cannot find it by the record's id; organization owner,
designer, clerk and reader get 403 on list, summary and detail; another organization's Security Administrator sees 0
and gets 404; notes, assignment (Security Administrators only), stale-revision 409 and resolution are audited (note text
not in audit metadata); after resolution the record is still refused and the refusal is counted on the closed incident;
the guard trigger refuses rewinding the occurrence count.

Not run: the complete Stage B suite and SEC-P suite (only the classes above); human NVDA/VoiceOver review; 200%/400%
browser zoom was approximated with 640 px and 320 px viewports. Persistence across restarts is by construction (rows in
the business database; the dev stack was rebuilt with the migration and kept its data) but has no dedicated restart test.

## 6. Owner acceptance (C-91, 2026-10-30)

The project owner visually tested and **approved** the Dashboard, the Security monitoring section and incident pages,
the unified styling, the charts, per-section search and the 15-row pagination as delivered in `3ae5ad7`, and asked that
they be preserved unchanged. Later work branches from this commit and must not redesign these surfaces.

What the acceptance covers and does not cover:

| Item | Status after C-91 |
|---|---|
| Visual and functional acceptance of §1 deliverables | Accepted by the owner (development) |
| Merge into `main` | Not done. Needs a signed pull request and the required hosted checks (`ci` runs on every pull request; `stage-b-integration` on pull requests into `main`). No pull request is open yet; hosted CI has not run on this branch. |
| SD-1 Authority journal read / disposition command (AC-10) | **Open** — proposal only |
| SD-2 Organization-owner security summary permission | **Open** — proposal only |
| SD-3 Auditor role contract | **Open** — proposal only |
| SD-4 Investigation note encryption | **Open** — current behaviour (plaintext administrative metadata) unchanged |
| SD-5 PB-08 step-up for investigation changes | **Open** — not available |
| SD-6 Per-section refresh amendment to ADR-PB-007 / PG-006 | **Open.** The approved screens have no per-section Refresh buttons; the owner's visual acceptance is consistent with the proposed amendment but is not recorded as the formal ADR amendment. A decision is still required. |
| §3 trust limitation (triage store is business PostgreSQL, not tamper-proof evidence) | Unchanged |
| §5 tests not run (full Stage B, SEC-P, NVDA/VoiceOver, real 200%/400% zoom) | Unchanged |

The acceptance is not an independent security review and does not change any production Level 2 gate.
