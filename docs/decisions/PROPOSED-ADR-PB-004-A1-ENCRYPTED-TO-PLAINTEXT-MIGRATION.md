# ADR-PB-004 Amendment A1 — Controlled Removal of Field Encryption (Encrypted → Plaintext Migration)

- **Status:** **PROPOSED — NOT APPROVED, NOT IMPLEMENTED.** Written 2026-10-19 at the project owner's request for review. Until the owner explicitly approves this amendment, the platform keeps its no-downgrade enforcement: `FieldProtectionPolicy.ResolveStrengthening` refuses every lower classification or plaintext target (`FIELD_PROTECTION_DOWNGRADE_REFUSED`), the authority's `PublishFormSecurityRevision` command accepts only `ApplicationEncrypted`, and no code path decrypts stored values into typed plaintext rows.
- **Amends:** `ADR-PB-004-ENCRYPTION-SEARCH-CAPABILITY.md` §"Versioning and classification changes" (which already states that downgrading is "disallowed as an ordinary Designer edit" and names the exception conditions this amendment turns into a protocol).
- **Related:** CLAUDE.md §4C.2 (never automatically downgrade; downward migrations require a privileged, separately approved workflow), §4L (signed rows, manifests, anti-rollback), §4M–§4O (authority-first content commits), decision B-28 (plaintext → encrypted migration, implemented), `security_hardening/SECURITY_SIMPLIFICATION_STANDARD.md` Decision 2.
- **Companion, implemented:** the *upward* migration (B-28) is the mirror image of what is proposed here and is the implementation baseline: `PublishFormSecurityRevision` command → per-record reprotection operations validated by `ReprotectionRules` → verified completion.

## 1. Why a separate, approved protocol

Removing encryption is the only protection change that *increases* what a database reader can learn. It is irreversible in practice: once a plaintext row has existed in Business PostgreSQL, backups, WAL and replicas may hold it even after a later re-encryption. The rules below therefore treat encrypted → plaintext as a governance event with its own authorization, its own authority command, its own evidence, and a state machine that never reports success it has not verified. Nothing here grants anyone access to a record they could not already open: the same Authorization Authority decisions gate every read before and after.

## 2. Proposed rules (owner's rules 1–8, made precise)

| # | Rule | How it is enforced |
|---|---|---|
| 1 | Public and Internal fields may use plaintext only when the organization policy explicitly permits plaintext for that classification. | `organization_security_policies.PlaintextInternalFieldsPermitted` (exists) plus a new `PlaintextDowngradePermitted` flag, both owner-only, audited, revision-checked. The authority receives the policy flags in the command payload and rejects a downgrade command whose organization policy (as recorded in the authority's own journal by a prior `UpdateMandatoryPolicy` command — see §6) does not permit it. |
| 2 | Confidential and Restricted fields cannot become plaintext without an independently authorized reclassification and governance approval. | Two commands, two distinct principals: (a) `ReclassifyField` to Public/Internal issued by an organization owner or system administrator **with step-up assurance** (PB-08) and a governance approval reference; (b) `PublishFormSecurityRevision` with `required_protection_mode = Standard` issued by a *different* authorized principal. The authority enforces distinct identity bindings, the approval reference and a maximum delay between the two. A designer cannot issue either. |
| 3 | An ordinary Form Designer cannot remove encryption from a populated field with a checkbox. | The Add/Edit field dialogs never offer a plaintext target for an encrypted field. The strengthen route keeps refusing downgrades. A new, separate "Request plaintext migration" flow exists only on the Organization security page, requires `organization.manage`, and produces a *request*, not a change. |
| 4 | Removing encryption from existing values requires an independently authorized, audited decryption migration. | Mirror of B-28: `field_protection_migrations` with `MigrationDirection = Decrypt`; a background worker performs per-record **deprotection operations** that the authority validates with `DeprotectionRules` (encrypted row of the named field → plaintext row of the same item; nothing else changes; no encrypted row of the field remains). Every operation is journaled under the downgrade command with the actor who approved it. |
| 5 | Authority permissions, record integrity, published versions and audit history are preserved. | No relationship edge changes; every record gets a new accepted content revision with a complete signed manifest; published form versions keep their protection snapshot (a *new* version is required to present the field as plaintext to new records); all events flow through the canonical audit pipeline with redaction. |
| 6 | Interruptions, failures, concurrent edits and rollback are safe. | Leased batches, idempotent operation ids, bounded attempts, Blocked/Failed states, user edits recognised as completion (a user edit during a decrypt migration writes **encrypted** until the migration flips the field — see §4 ordering), and a defined rollback: a `CancelDowngrade` command re-queues re-encryption (B-28) of every record already decrypted and records the plaintext exposure window as evidence. |
| 7 | "Plaintext migration complete" is displayed only after every active value is verified. | Same post-check pattern as B-28: completion requires zero encrypted rows of the field in every latest committed snapshot (recomputed) and all items Completed; a forged business row cannot change the claim. |
| 8 | Changing encryption settings never grants access to previously inaccessible records. | Discovery and read remain authority decisions unrelated to protection mode; the migration worker acts under the command, never under a user with record permissions, and produces no readable output. Search of Public/Internal plaintext remains gated by field predicate authorization (ADR-PB-004 §6). |

## 3. Authorization model

| Actor | May do |
|---|---|
| Organization owner / system administrator (step-up verified) | Set `PlaintextDowngradePermitted`; issue `ReclassifyField` with a governance approval reference; cancel a downgrade. |
| Second authorized principal (owner/administrator, distinct identity binding) | Issue `PublishFormSecurityRevision(Standard)` referencing the reclassification command. |
| Form Designer | See the field's protection state and migration progress; **cannot** request, approve or start a downgrade. |
| Record owner / contributor | Unaffected; their reads and edits continue through the same authority decisions. |
| Business API runtime / worker | Submit deprotection operations under the effective downgrade command only; cannot sign anything else (same anti-oracle rules as B-28, tested by the direct authority tests). |
| Business DBA | Cannot create, promote or complete a migration: the authority accepts operations only under a journaled command; forged business rows do not change the status claim. |

## 4. Migration state machine (proposed)

```
Requested ──(ReclassifyField effective, approval ref)──► Reclassified
Reclassified ──(second principal: PublishFormSecurityRevision(Standard) effective)──► Authorized
Authorized ──(worker lease)──► Running ──► Completed (post-check: 0 encrypted rows of the field) 
Running ──(dependency down)──► Blocked ──(auto)──► Running
Running ──(bounded attempts exhausted)──► Failed ──(owner retry)──► Running
Requested/Reclassified/Authorized/Running/Blocked/Failed ──(CancelDowngrade)──► RollingBack ──(B-28 re-encryption complete)──► Cancelled
```

Ordering guarantees:

1. The field's live protection mode stays `ApplicationEncrypted` until the migration is **Completed**; new writes during the migration are still encrypted and are picked up by the enumeration (same "untracked records" handling as B-28). Only after Completed does the field flip to `Standard` for new writes.
2. Each record's deprotection is one accepted content revision; a concurrent user commit wins and the worker re-reads.
3. `Completed` is written only after the post-check; the UI shows "Plaintext migration complete" from the server status only.
4. Cancellation never deletes rows: it re-encrypts and leaves the plaintext revisions as immutable history flagged in the exposure evidence.

## 5. Security risks and required mitigations (must be accepted explicitly)

| Risk | Mitigation / residual |
|---|---|
| Plaintext persists in WAL, backups, replicas and immutable history after a later re-encryption. | Document the exposure window per migration (first plaintext commit → last) in the authority journal; retention/purge of historic plaintext revisions is a separate privileged workflow (CLAUDE.md §4A retention) and is **not** promised by this amendment. |
| Low-cardinality values become inferable to a DBA once plaintext. | The owner's policy statement must acknowledge DBA readability (already required for plaintext Internal fields). |
| A compromised worker could produce a wrong plaintext for a value. | Same bound as B-28: the authority cannot check semantics; the worker self-checks (decrypt → format → compare) before staging; items with a mismatch fail closed. Residual: trust in the business runtime, as for every user write. |
| Downgrade used to make Confidential data searchable without governance. | Confidential/Restricted require reclassification by a step-up-verified administrator plus an approval reference and a distinct second principal; both are journaled security commands; the designer cannot do it. |
| Policy relaxation during a running migration. | Commands bind the policy revision they were evaluated against; the authority rejects a downgrade whose policy precondition no longer holds at commit. |
| Replay of an old migration to re-expose data later. | Command idempotency keys and revision CAS as for every security command; a completed downgrade cannot be replayed to a new revision. |

## 6. What this amendment requires before implementation

1. Owner approval of this amendment, recorded in the pre-build decision register.
2. Authority: `ReclassifyField` and `PublishFormSecurityRevision(Standard)` planners with distinct-principal and approval-reference checks; `UpdateMandatoryPolicy` command so the authority holds the organization's plaintext policy itself (today only the business row does); `DeprotectionRules` (mirror of `ReprotectionRules`); `CancelDowngrade`.
3. Business: migration direction, request/approval UI on the Organization page, exposure-window evidence, rollback path reusing B-28.
4. Tests: mirror of the B-28 integration suite plus: designer cannot request; single principal cannot complete; policy flip mid-migration aborts; cancel re-encrypts; forged completion does not claim success; search becomes available only after Completed and only within predicate authorization.
5. Independent security review of the amendment (Level 2) and PB-12 evidence before any production tenant uses it.

## 7. Decision requested

Approve or reject the protocol above. Until approved: no encrypted → plaintext conversion exists, the no-downgrade enforcement stays in force, and the only supported direction is plaintext → encrypted (B-28).
