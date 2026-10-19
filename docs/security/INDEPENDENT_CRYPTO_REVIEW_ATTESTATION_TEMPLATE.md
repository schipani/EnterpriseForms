# Independent Cryptographic Review — Attestation Template (Stage A)

Use this template to turn a completed review into verifiable evidence. Until a filled-in copy exists **outside the
business database** (release/change system, signed email, or a signed commit by the reviewer), the Stage A gate report
records the review as *NOT PERFORMED* or, when the work is done but this form is missing, *Completed — evidence pending*.
The implementation author must not fill in, sign, or edit any value in this form on the reviewer's behalf.

## 1. Identity and independence

| Field | Value |
|---|---|
| Reviewer name | |
| Organization / role | |
| Contact (verifiable) | |
| Independence statement | I did not author, pair on, or approve the code and documents under review. [ ] confirmed |
| Qualification (brief: relevant cryptographic engineering / security review experience) | |
| Tools or assistants used (e.g. static analysis, a language model used as an aid) — the reviewer remains accountable for every conclusion | |

## 2. Exact subject of review

| Field | Value |
|---|---|
| Repository | `github.com/schipani/EnterpriseForms` |
| Commit reviewed (full SHA) | |
| Review package version (SHA-256 of `docs/security/INDEPENDENT_CRYPTO_REVIEW_PACKAGE.md` as reviewed) | |
| Files reviewed (list paths, or "all files in package §0 with matching digests") | |
| Evidence runs inspected (`docs/evidence/stage-a/<run>/gate-a-evidence.json` ids, image fingerprints) | |
| Reproduction performed by the reviewer (package §5 steps actually executed; attach output digests) | [ ] vectors  [ ] unit tests  [ ] Gate A Docker run  [ ] Vault vector generator |

## 3. Findings per question (PASS / FAIL / FINDING)

| # | Question (package §1) | Result | Severity (Critical / High / Medium / Low / none) | Evidence (file:line, vector id, command output) | Required change |
|---|---|---|---|---|---|
| R1 | Canonical encoding injective for every column type | | | | |
| R2 | Framing binds domain, schema, key version, entity, organization, PK, revision | | | | |
| R3 | JSON v1 subset sufficient for JSONB ↔ RFC 8785 agreement (F-03) | | | | |
| R4 | Merkle v1 construction and `leaf_index_digest` soundness | | | | |
| R5 | No residual replay window across operations, crash recovery, restore | | | | |
| R6 | Local and Vault Transit HMAC over identical bytes | | | | |
| R7 | Single gated signing path; no sign-any-bytes or verify oracle | | | | |
| R8 | Journal hash chain, canonical `text` body, archive receipt adequacy | | | | |
| R9 | Constant-time comparison, no truncated MACs, randomness, secrets in logs | | | | |

Additional findings not covered by R1–R9:

| ID | Severity | Location | Scenario | Required change |
|---|---|---|---|---|
| | | | | |

## 4. Decision

| Field | Value |
|---|---|
| Decision | APPROVED / APPROVED WITH CONDITIONS / REJECTED |
| Conditions (each with an owner and a closing criterion) | |
| Scope limits of this review (what was **not** reviewed) | |
| Date (UTC) | |
| Signature (digital signature, signed commit SHA, or reference to the signed record) | |
| Record location outside the repository (system and record id) | |

## 5. Author acknowledgement (filled in by the implementation team, after the reviewer)

| Field | Value |
|---|---|
| Received on (UTC) | |
| Issues opened for each FAIL/FINDING (ids) | |
| Commit(s) addressing conditions | |
| Re-review required? | yes / no — reviewer's statement reference |

---

How this form changes the gate report (`docs/STAGE_A_GATE_CLOSURE.md` §3a):

| State | Criterion |
|---|---|
| NOT PERFORMED | No reviewer has delivered findings for R1–R9 on a named commit. |
| Completed — evidence pending | A named independent reviewer has delivered R1–R9 findings for a named commit (e.g. in a PR review, an email, or a document), but §1–§4 of this form are not yet completed and stored outside the repository. |
| Completed — attested | This form is complete, signed and stored outside the repository; its location is cited in the gate report. Only this state satisfies Stage A requirement 11 (subject to conditions being closed). |
