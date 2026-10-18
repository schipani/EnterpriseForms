# Independent Cryptographic Review Package — Stage A

**Status: REVIEW NOT PERFORMED. Stage A is BLOCKED on this item** (`docs/STAGE_A_STATUS.md` §gate). This package was
assembled by the implementation author. Author-generated test vectors, author code review and machine agreement between
three author-written implementations are **not** independent review (row-integrity-format.md §9, PHASE0_STATUS §10.4).

## 1. What the reviewer is asked to attest

A reviewer who did **not** author the format or the code, recorded in a change/release system outside the business
database, is asked to answer each question below with PASS / FAIL / FINDING and sign the record.

| # | Question | Normative source |
|---|---|---|
| R1 | Is `DynamicForms/RowHmac/v1` an unambiguous, injective canonical encoding for every registered column type (length prefixes, type tags, explicit NULL, NFC text, normalized decimals, UTC microsecond instants, big-endian UUID)? Can two distinct logical rows ever produce identical bytes? | `docs/dynamic-forms/row-integrity-format.md` §2–3 |
| R2 | Does the framing bind every trust-relevant identifier (domain, schema version, key version reference, entity kind, organization id, primary key, row revision) so that a tag cannot be transplanted across tenants, tables, rows or revisions? | §2, SEC-P11 step 5, MRK-27 |
| R3 | Is the JSON v1 subset (integers only, ASCII keys, ordinal sort) sufficient to guarantee PostgreSQL JSONB ↔ RFC 8785 agreement? **F-03 decision:** v1 forbids non-integer numbers; fractional presentation values must use Decimal/Text columns. Confirm or require a v2 subset before any JSONB column carries floats. | §3.6, F-03 |
| R4 | Merkle v1: domain-separated leaf/node/empty/index hashing, odd-node promotion without duplication, sorted unsigned item keys — is the construction second-preimage resistant for a versioned map snapshot, and is the leaf-index digest a sound complete-set commitment? | `trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md` §2 |
| R5 | Is the v1 "re-sign every leaf under the candidate operation" policy free of replay between operations (SEC-P05 shows detection; confirm no residual replay window)? | §2.1 |
| R6 | Provider adapters: do LocalHmac and Vault Transit produce HMAC-SHA-256 over **identical message bytes** (no double hashing, no base64 confusion, Vault `vault:vN:` token normalized to 32 raw bytes)? Confirm with the live vectors in §3. | `trust_authority/CRYPTOGRAPHIC_SIGNING_AUTHORITY.md` §6–7 |
| R7 | Key naming/purpose separation, version pinning, non-exportable keys, no sign-any-bytes path: is the Stage A signing coordinator (`ContentService.PrepareAsync` → `IIntegrityMacProvider`) the only signing path and is it gated by actor, tenant, revision CAS and delta authorization before every `SignAsync`? | §3–5, invariant A9 |
| R8 | Journal hash chain and archive receipt: is `SHA-256(canonical JSON envelope with previous_digest_hex)` adequate as a tamper-evident chain given the archive is append-only (If-None-Match: *) and the ledger anchoring is not yet implemented? | `JournalChain`, G14 |
| R9 | Any use of non-constant-time comparison, truncated MACs, weak randomness, or secret-bearing logs/exceptions? | all |

## 2. Artifacts under review (exact paths, commit `git rev-parse HEAD` at review time)

- Format: `docs/dynamic-forms/row-integrity-format.md`; code `src/shared/EnterpriseForms.Integrity/Canonical/*.cs`, `.../Merkle/MerkleManifestV1.cs`, `.../Canonical/RowCanonicalHeader.cs`.
- Vectors: `trust_authority/row-integrity-v1.test-vectors.json`, `trust_authority/merkle-v1.test-vectors.json`; verifiers `scripts/integrity/verify_vectors.py`, `scripts/integrity/verify-vectors.mjs`, `tests/integrity/*`.
- Registry: `src/backend/EnterpriseForms.Application/Features/FormRecords/Integrity/BusinessSignedSchemas.cs` (form_records v2, field_string_values v1) and `tests/architecture/.../SignedColumnRegistryTests.cs`.
- Signing coordinator and providers: `src/authority/AuthorizationAuthority.Application/Features/Content/ContentService.cs`, `src/authority/AuthorizationAuthority.Infrastructure/Signing/*.cs`.
- Journal/archive: `.../Features/Journal/JournalChain.cs`, `.../Infrastructure/Archive/BlobSecurityEventArchive.cs`.
- Projection CAS: `.../Infrastructure/SpiceDb/SpiceDbProjectionClient.cs`, `.../Application/Features/Projection/ProjectionProcessor.cs`.
- Executed evidence: `artifacts/security/gate-a-evidence.json` and `artifacts/security/evidence/SEC-P*.json` from the reviewer's own run of `security-gate-a`.

## 3. Reproduction steps for the reviewer (do not trust author-run output)

```sh
python scripts/integrity/verify_vectors.py && node scripts/integrity/verify-vectors.mjs
dotnet test --solution EnterpriseForms.All.slnx -- --minimum-expected-tests 100
cd infrastructure/docker && cp env.example .env
docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait \
  business-db authority-db spicedb vault vault-transit-init archive-store azurite authority-migrate authority business-migrate api attacker-sql
cd ../.. && dotnet test --project tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj -- --minimum-expected-tests 13
python security_validation/validate_security_evidence.py artifacts/security/gate-a-evidence.json
```

Live Vault Transit vs local provider agreement (R6): run SEC-P11 and SEC-P04; additionally, from the authority container
network, HMAC the published vector `ROWV1-USER-OWNED` bytes with a transit key imported from the published test key is
**not possible** (Vault transit HMAC keys cannot be imported in dev mode without the `import` endpoint and the policy
forbids exportable keys). The reviewer should instead sign a fixed payload with Vault and with `HMACSHA256` using a
throwaway key created through `transit/keys/<name>` + `export` on a **separate throwaway Vault** to confirm the adapter
performs no double hashing. This procedure is documented, not yet executed.

## 4. Known limitations the reviewer must weigh (author-declared)

1. Vault dev mode and the Azurite archive are test doubles; Azure Managed HSM provider and Confidential Ledger anchoring are not implemented (G04, G14 remain OPEN).
2. Only `form_records` (v2) and `field_string_values` (v1) are registered; every other protected table arrives with its feature and must be added to the registry and the inventory test.
3. The TestIdentityProvider (HS256 shared key) stands in for Entra ID; it is refused outside Development/Testing.
4. Search completeness (G06) is out of scope; no search endpoint exists in Stage A.
5. Transport between business API and authority is plain HTTP inside Docker; mTLS and workload identity are production requirements (PB-05/PB-08) and not emulated.

## 5. Sign-off record (to be completed by the reviewer, stored outside BusinessDb)

```
Reviewer: ______________________  Organization/role: ______________________
Commit reviewed: ______________________  Image fingerprint: ______________________
R1 [ ] R2 [ ] R3 [ ] R4 [ ] R5 [ ] R6 [ ] R7 [ ] R8 [ ] R9 [ ]
Findings (ID, severity, required change): ______________________
Decision: APPROVED / APPROVED WITH CONDITIONS / REJECTED   Date (UTC): __________  Signature: __________
```
