# G14-R1 — Proposed Contract Amendment A-4 (DRAFT): Controlled Audit-Stream Recovery

**Status (2026-11-18):**

| Item | State |
|---|---|
| Document | **DRAFT — PROPOSED AMENDMENT FOR OWNER REVIEW. NOT AUTHORITATIVE.** It is not part of `SEC_BL_01_DR_CONTRACT_CLOSURE.md`. It must not be appended there or implemented until the owner explicitly approves it. If approved, it would be appended as a dated §24, with §1–§23 unchanged |
| Basis | `G14_R1_CONTROLLED_AUDIT_STREAM_RECOVERY_DESIGN.md` (rationale, findings SR-F1..SR-F12, decisions SR-OD-01..SR-OD-18) |
| Scope if approved | Audit stream; Development/Testing; the recovery approval mode refused elsewhere (SR-AU-9) |
| Precedence if approved | Where it conflicts with §5 (DP-9), §22.3 (DD-3) or §23.1 (SM-1, SM-5..SM-8), A-4 governs for the scopes stated. It relaxes no check outside an executed, signature-verified recovery scope |
| Review | Owner-led; prepared by the implementing assistant; not an independent review |
| Update (2026-11-18, G14-R2) | The owner accepted G14-R1 as design work. The approved, implementation-ready provisions are now authoritative as contract **§24** (A-4, `SEC_BL_01_DR_CONTRACT_CLOSURE.md`), reconciled with the G14-R2 dispositions (§24 preamble). **This draft stays non-authoritative**: where it differs from §24, §24 governs. Its SR-B rules (SR-DL-3, -5 and -7 for SR-B), SR-D, SR-E and the verifier's commitment comparison (SR-EP-5) are not adopted |

Each rule cites the owner decision it depends on (SR-OD-xx). A rule whose decision is rejected falls away.

---

## A-4.1 Records and layout (SR-OD-01, SR-OD-02)

| # | Rule |
|---|---|
| SR-RC-1 | **Stores.** Recovery records are create-only objects in the journal-verification store under `recovery/{organization:D}/{stream}/{failed_revision:D12}/`, mirrored byte-identically in the archive under `meta/dplus-recoveries/{organization:D}/{stream}/{failed_revision:D12}/`. A record is valid if a valid copy exists in **either** store. Two copies with different bytes under the same name are `SR_RECORD_CONTRADICTORY` (security) and invalidate that record |
| SR-RC-2 | **Records per recovery** (`{recovery_id:D}` is a UUIDv7): `{recovery_id}/investigation.json` (`DynamicForms/DPlusStreamInvestigation/v1`); `{recovery_id}/statement.json` (`DynamicForms/DPlusStreamRecoveryStatement/v1`); `{recovery_id}/approvals/{jti}.jws`; `{recovery_id}/rejections/{jti}.jws`; `{recovery_id}/cancellations/{jti}.jws`; `{recovery_id}/action.json` (strategy action, SR-C1 only); `{recovery_id}/outcome.json` (`DynamicForms/DPlusStreamRecoveryOutcome/v1`). Per stop set: `executions/{stop_set_sha256}.json` (`DynamicForms/DPlusStreamRecoveryExecution/v1`, naming one `recovery_id`) |
| SR-RC-3 | **Evidence bytes** (row envelopes, listing results) are stored only in the archive copy, under `{recovery_id}/evidence/`. The verification-store copy holds their digests. No protected or business data beyond what the archive already holds for the audit stream |
| SR-RC-4 | **Canonical form.** Every JSON record is canonical (sorted members, no insignificant whitespace, UTF-8, RFC 8785 number rules), with unknown members refused. Identical bytes on retry are idempotent; different bytes are a conflict and nothing is overwritten |
| SR-RC-5 | **Never removed.** No component deletes, overwrites or supersedes a recovery record, stop record, pre-commitment or archived object (SM-3, SM-9, BL-2 unchanged). Recovery state is **derived** from the records; there is no mutable recovery state field, and AuthorityDb holds none that is trusted |

## A-4.2 Statement (SR-OD-01, SR-OD-09)

| # | Rule |
|---|---|
| SR-ST-1 | **Members:** `statement_version` (1); `recovery_id`; `organization_id`; `stream_name` (`audit`); `failed_revision`; `stop_generation` (the generation of the stops being resolved); `stop_set` (sorted list of `{record_kind: Marker | Finding | InvalidObject | EvidenceCommitment, object_name, sha256}`); `stop_set_sha256`; `strategy` (`SR-A`, `SR-C1`, `SR-B`, `SR-B-UNDELIVERABLE`); `eligibility_class` (design §2.1); `evidence_snapshot_sha256`; `expected` (strategy-specific post-recovery evidence, SR-DL-3); `investigation_sha256`; `requester_label`; `request_reason_sha256`; `not_before_utc`; `not_after_utc` (≤ 24 h after `not_before_utc`, SR-OD-07); `tool_version`; `source_commit` |
| SR-ST-2 | **Built by the tool from observed state.** The requester chooses only the strategy, the reason, the investigation narrative and the window. Every digest and expectation is computed by the tool. A statement whose stop set is not the complete set of unresolved stop records at build time (SR-EL-4) is refused |
| SR-ST-3 | **Evidence snapshot** (`DynamicForms/DPlusStreamRecoveryEvidence/v1`): the row `k` journal digest and canonical row-field digest; the predecessor state (`Present` with SHA-256, or `Absent` with the listing-result digest); the sentinel digest and instance id; the provisioning record digest, if any; the trusted checkpoint head (sequence, revision, digest, assurance) and its chain verification result; the AuthorityDb organization audit head (informational, Observed); each item's class (Verified, Observed). Any Unavailable or Contradictory mandatory item ⇒ no statement (SR-EL-5) |

## A-4.3 Authorization (SR-OD-03..SR-OD-07, SR-OD-17)

| # | Rule |
|---|---|
| SR-AU-1 | **Approver keys** come only from protected deployment configuration section `JournalRecovery` (`Mode`, `Approvers[]` with `ApproverId`, `PublicKeyBase64`, optional `RetiredOnUtc`). They are never read from AuthorityDb, the registry or any store. The Authority holds public keys only |
| SR-AU-2 | **Approval** = compact JWS, ES256, `kid` = approver id. Claims: `approval_kind = "DPlusStreamRecovery"`, `statement_sha256`, `decision` (`approve`/`reject`), `justification_sha256`, `jti` (UUIDv7), `iat`, `exp` (`exp − iat` ≤ 900 s) |
| SR-AU-3 | **Authorized statement:** two approvals with `decision = approve`, distinct `kid`s, the same `statement_sha256` as the stored statement, valid signatures, and each `kid` either active or retired with `iat ≤ RetiredOnUtc`. A key removed from the configuration verifies nothing (SR-OD-17: its recoveries re-stop) |
| SR-AU-4 | **Separation of duties:** the two approver ids differ from each other and from `requester_label`'s registered approver id, if any; production additionally requires distinct verified human identities (Entra `oid`) and step-up evidence bound to `statement_sha256` (not implemented) |
| SR-AU-5 | **Rejection and cancellation:** a single valid JWS with `decision = reject` from any configured approver, or a signed cancellation (`approval_kind = "DPlusStreamRecoveryCancellation"`, from an approver or the requester's registered key), makes the statement **permanently non-executable**, and never lifts anything |
| SR-AU-6 | **Single use:** `jti` is the object key (create-only), so a JWS cannot be stored twice. `recovery_id` is single-use. One execution record per `stop_set_sha256` (create-only): a second execution for the same stop set conflicts and fails closed |
| SR-AU-7 | **Execution preconditions**, checked by the executor in this order: the statement is authorized (SR-AU-3); no rejection or cancellation; current time within the window; the **freshly re-read** evidence snapshot digest equals `evidence_snapshot_sha256`; the current unresolved stop set equals `stop_set`; the checkpoint chain is valid where the strategy needs an anchor (SR-PB-8). Any failure ⇒ nothing written; outcome `Failed` with the reason. **Then, in this order:**
1. For SR-C1 only, `action.json` and the create-only predecessor write. Its bytes are bound in the statement and equal the trusted anchor; a write conflict ⇒ `Failed`.
2. The row hint (SR-DL-5).
3. **The execution record, last.** The lift therefore never takes effect before the strategy action and the hint exist. A termination between the steps leaves the stream stopped, and a re-run continues idempotently |
| SR-AU-8 | **No caller assertion:** no input flag, file or argument states that verification passed; every check is executed by the tool and again by D+ (SR-DL-1) |
| SR-AU-9 | **Modes:** `Unavailable` (default; the only mode outside Development/Testing until production approval evidence and WP-8 exist; every execution is refused `RECOVERY_DUAL_CONTROL_UNAVAILABLE`) and `SyntheticDevelopment` (Development/Testing only; startup refuses it elsewhere; recorded as **not two-human approval evidence**) |

## A-4.4 Amendments to A-3 (SR-OD-09, SR-OD-12)

| # | Rule |
|---|---|
| SR-SM-1 | **Stop generations** (amends SM-1, SM-3 and the finding identity of DD-9). Generation `g` of a stream = the number of `Completed` recovery outcomes for that (organization, stream) with a valid execution and authorization. A stop created at generation 0 keeps the A-3 key `{revision:D12}.json`. A stop created at generation `g ≥ 1` uses `{revision:D12}.r{g:D4}.json`, and its finding identity includes `g`. The two forms are canonical; anything else under the prefix stays `DPLUS_STOP_MARKER_INVALID` |
| SR-SM-2 | **Discovery** (amends SM-5) additionally lists the stream's recovery records (SR-RC-1) and pre-commitments (SR-EP-1) completely, in both stores, on every pass. A failed listing is handled as in SM-5: archive ⇒ unavailability; verification store ⇒ inconclusive (the audit stream waits) |
| SR-SM-3 | **Verifier** (amends SM-7): `meta/dplus-recoveries/` and `meta/dplus-intents/` are recognized prefixes. Their objects are validated canonically (signatures are verified by the verifier too); an invalid or contradictory object is `SR_RECORD_INVALID` / `SR_RECORD_CONTRADICTORY` (security). Nothing is silently ignored |
| SR-SM-4 | **Archive instance pinning** (SR-OD-12, separate increment; amends DD-1/DD-11 for the audit stream): D+ compares the sentinel's instance id with the provisioning record or the trusted checkpoint head. A difference is `ARCHIVE_REGRESSION/INSTANCE_CHANGED`, handled like a stream-stopping refusal (marker in the verification store only), and is lifted only through SR-D |

## A-4.5 D+ lift and strategy rules (SR-OD-01, SR-OD-08, SR-OD-10)

| # | Rule |
|---|---|
| SR-DL-1 | **Resolved stop record:** a stop record is resolved only if a statement whose `stop_set` contains it exactly (name and SHA-256) is authorized (SR-AU-3), has no valid rejection or cancellation, and an execution record for its `stop_set_sha256` names that `recovery_id`. D+ re-verifies this **on every pass**, including signatures. An unresolved record stops the stream as in SM-5 |
| SR-DL-2 | **No other lift.** AuthorityDb state, codes, receipts, archive presence of the failed revision, store recovery and restarts never resolve a record (SR-EL-2). In particular, archive presence of `k` without an authorization does not resolve a stop at `k` |
| SR-DL-3 | **Strategy rule** for the first pending entry `k = failed_revision` when every record is resolved. **SR-A:** full DP-2..DP-6, and the evidence must equal `expected` (the unchanged snapshot). **SR-C1:** the same, with `expected` naming the restored predecessor digest, which must equal the anchor recorded in the snapshot. **SR-B:** R-1..R-5 on row `k` as before; the predecessor check is replaced by "the predecessor state equals `expected.predecessor_state` **and** row `k`'s journal digest equals `expected.row_journal_digest`"; then deliver. **SR-B-UNDELIVERABLE:** row `k` is never delivered; for `k + 1` the predecessor check is "archive `k` absent **and** row `k + 1`'s previous digest equals `expected.row_journal_digest_of_k`". Entries after the recovered revision follow normal D+ unchanged |
| SR-DL-4 | **Mismatch:** evidence differing from `expected` is refused as a stream-stopping refusal `DPLUS_RECOVERY_EVIDENCE_MISMATCH` at generation `g + 1`. The recovery's outcome becomes `Failed`. Nothing of the old stop is reopened or removed |
| SR-DL-5 | **Delivery state** (C-SR-5): before it creates the execution record (SR-AU-7 order), the executor sets row `k` from `VerificationFailed` to `Pending` with code `DPLUS_RECOVERY_AUTHORIZED:{recovery_id}`, except under SR-B-UNDELIVERABLE, where row `k` stays `VerificationFailed`. This write is a hint for the work loop's fetch, never trusted (SM-8). M-6, if implemented, must permit this transition for the recovery identity only |
| SR-DL-6 | **Completion:** the outcome `Completed` is written once the archive holds the recovered revision (or `k + 1` under SR-B-UNDELIVERABLE) with the expected digest. A missing outcome changes nothing for D+: the lift rests on SR-DL-1 alone |
| SR-DL-7 | **Assurance:** SR-B and SR-B-UNDELIVERABLE require a re-baseline (A-1 RB-1) with an owner disposition whose reference is `recovery_id`, creating the AX record for the break. Assurance stays Reduced permanently (AX-8). SR-A and SR-C1 change no assurance level |
| SR-DL-9 | **Hold above a lifted revision:** while a lift is in effect and the recovered revision is not yet archived, D+ evaluates no revision above it. Such entries stay `Pending` with `DPLUS_STREAM_STOPPED`, and no finding is recorded. Under SR-B-UNDELIVERABLE, the revision delivered first is `k + 1`. This prevents a spurious `PREDECESSOR_MISSING` at `k + 1` when the work loop fetches the next row first |
| SR-DL-8 | **Isolation:** recovery records under one (organization, stream) never affect any other organization or stream |

## A-4.6 Evidence pre-commitment W2-S (SR-OD-11, SR-OD-15)

| # | Rule |
|---|---|
| SR-EP-1 | **Record:** `meta/dplus-intents/{organization:D}/{stream}/{revision:D12}.json` in the archive, mirrored under `intents/{organization:D}/{stream}/{revision:D12}.json` in the verification store. Schema `DynamicForms/DPlusEvidenceCommitment/v1`: organization; stream; revision; row journal digest; canonical row-field digest; predecessor state (`Present` + SHA-256, or `Absent`); sentinel instance id; D+ rule-set version. **No time and no body digest**, so retries are byte-identical |
| SR-EP-2 | **Order:** read the evidence; then write the commitment (create-only) and confirm it in at least one store; **only then** evaluate D+. An entry is never evaluated without a confirmed commitment |
| SR-EP-3 | **Existing commitment:** read it first. **Equal** to the freshly read evidence: evaluate. **Different** (or a conflict on write): a stream-stopping refusal `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT` (security), recorded per SM-4 with the commitment named in the stop set. A rule-set version difference alone is not "different": re-evaluate under the current rules and record both versions (SR-OD-15) |
| SR-EP-4 | **Store failures:** both stores unavailable for the commitment ⇒ no evaluation; the audit stream waits (operational). One store has the commitment and the other differs ⇒ `SR_RECORD_CONTRADICTORY`, stream stopped |
| SR-EP-5 | A commitment is never deleted, also after the revision is delivered (SR-RC-5). The verifier validates commitments against archived entries: for a delivered revision, the commitment must match the archived predecessor and row digests, otherwise `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT` (security, alert-only) |
| SR-EP-6 | The predecessor-absent state is committed like any other: a later reappearance of the predecessor is a change, and needs recovery (SR-A after investigation, or SR-C1 when anchored) |
| SR-EP-7 | **Residuals (mandatory statement):** an entry altered before its commitment is unchanged R-DR-1. Removal of the commitment from both stores by J4/J5 and J6 is the A-3 known limit. A compromised runtime is out of scope. Development/Testing stores are configuration-separated only (PV-9) |
| SR-EP-8 | **DD-10 is not closed by A-4.** Closure requires implementation, the tests of the matrix and an owner decision accepting the residuals of SR-EP-7, and for production WP-8 |
| SR-EP-9 | Cost: one create-only write per store per evaluated audit revision; measured under AC-DR-11 |

## A-4.7 Permanent blocks

SR-PB-1..SR-PB-8 of the design (§6.2) are contract rules. No A-4 path may lift an organization quarantine, act on an authorization or content stream, create a new stream epoch, overwrite or delete any object, proceed without containment where required, run outside Development/Testing, proceed on Unavailable or Contradictory evidence, or use an anchor from an invalid checkpoint chain.

## A-4.8 Identifiers

SR-RC-1..5, SR-ST-1..3, SR-AU-1..9, SR-SM-1..4, SR-DL-1..9, SR-EP-1..9. Codes, all new: `DPLUS_RECOVERY_EVIDENCE_MISMATCH`, `DPLUS_EVIDENCE_CHANGED_AFTER_COMMITMENT`, `SR_RECORD_INVALID`, `SR_RECORD_CONTRADICTORY`, `ARCHIVE_REGRESSION/INSTANCE_CHANGED`, `RECOVERY_DUAL_CONTROL_UNAVAILABLE`, row code `DPLUS_RECOVERY_AUTHORIZED:{recovery_id}`.

## A-4.9 Not claimed by A-4

No implementation; no closure of DD-10, SEC-BL-01, PD-15, CF-1, OI-DR-1 or G14; no change of M2F3-43; no production enablement; no two-human assurance in Development/Testing; no independence of stores; no independent review.
