# Independent Cryptographic Review Package — Stage A

**Status: REVIEW NOT PERFORMED. Stage A is BLOCKED on this item** (`docs/STAGE_A_GATE_CLOSURE.md` requirement 11).
This package was assembled by the implementation author. Author-generated test vectors, author code review and machine
agreement between three author-written implementations are **not** independent review
(`docs/dynamic-forms/row-integrity-format.md` §9, ADR-PB-012). Nothing below is a reviewer statement; §7 is blank.

## 0. Exact versions under review

| Item | Value |
|---|---|
| Repository | `github.com/schipani/EnterpriseForms`, branch `stage-a/level2-security-foundation`, PR #9 |
| Source commit | `e6df484b2ee35ed822cd011a06f41b3615ec10c8` (code commit the Gate A evidence is bound to; later commits on the branch are documentation-only, see A-20) |
| Image fingerprints | recorded per run in `docs/evidence/stage-a/*/gate-a-evidence.json` (`image_fingerprint` = SHA-256 of the two image IDs; `api=…`, `authority=…`) |
| Runtime | .NET 10 SDK 10.0.201, PostgreSQL 18.0, SpiceDB v1.56.2, HashiCorp Vault 1.20.4 (dev mode, transit), Azurite 3.9.0 (archive test double) |
| Line endings | all tracked text is LF (`.gitattributes`); digests below are over the committed bytes |

SHA-256 of each artifact as committed at `e6df484` (`git show e6df484:<path> | sha256sum`; the reviewer must recompute):

```text
c75162ac988be343644d988c1f69ffdd14a09cb4185a940e1d67b08783f7bf05  docs/dynamic-forms/row-integrity-format.md
a785670f335c21fa592e3afa93aac4f685029e1c44109411743831068346399d  trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md
7a42ce583f61e7efc7ddf07231074b4baaa3e8acd1481b643df3f8bd98a14899  trust_authority/row-integrity-v1.test-vectors.json
e6f43b3743562c3971e1b8c8bbb1b7518224e2e8340b2e7b01b93d2a6e269245  trust_authority/merkle-v1.test-vectors.json
037be3608ab900e9e4da7e1941d6e6303d82d33cdf40b350b08d960a32a9c987  docs/security/stage-a-vault-transit-vectors.json
8fe3d13823ca42f60ae5265610199227c83754bbab4f2c88cacad81507b0353c  scripts/integrity/row_integrity_v1.py
48c3339b31e9bebd628e8c2dd76e20219f04ce1441ed359c85cf52a1a46f5124  scripts/integrity/merkle_v1.py
c7cdab8cbea6b13aad73debc0a92d3b85028737913f6596288a8a5035388c989  scripts/integrity/verify_vectors.py
4fab13dd5feddaf3d0bdf96f75b6647d63b1ba4e2402e825b204aef4d43710c6  scripts/integrity/verify-vectors.mjs
d93a892f67493b53b61509289609e713de04023f52138b72a797e3cc59f626de  scripts/integrity/generate_vault_vectors.py
ac3539933bab23dc0aae8bc3ae4138677fdfe4117bb94f72e7a76099d861845f  src/shared/EnterpriseForms.Integrity/Canonical/RowCanonicalizerV1.cs
3415940bc42993f3d22d3a046f8a89af035627294268f9cb0bc874216255a15e  src/shared/EnterpriseForms.Integrity/Canonical/RowCanonicalHeader.cs
1434d2d58616fae945724a34e4cd4a4db8ce416f463d0c6beef7f54189049c27  src/shared/EnterpriseForms.Integrity/Merkle/MerkleManifestV1.cs
03529da8e998e339c73599d91ed2f263c36add7d3ad5d91774129691a785c5dd  src/backend/EnterpriseForms.Application/Features/FormRecords/Integrity/BusinessSignedSchemas.cs
2320ea52d714a288ceafd341deda75f19333505e5bb7845fe0158b6348b014a0  src/authority/AuthorizationAuthority.Application/Features/Content/ContentService.cs
5487b5144cdcaf968dd60ebc29e564c7b8dc66dac617e987cbe8904a458bb825  src/authority/AuthorizationAuthority.Infrastructure/Signing/LocalHmacProvider.cs
dcece8643c298766b1c7e0ba60d85510a458e4bf7c0e61a52d6c38057ffdf95c  src/authority/AuthorizationAuthority.Infrastructure/Signing/VaultTransitHmacProvider.cs
a8aeb2a77c92e6edbfb112ca85fadfdd44032ebe965b7e96b6a31c2872fc49be  src/authority/AuthorizationAuthority.Application/Features/Journal/JournalChain.cs
411589e72f5496d03aa697da4af559516f870c858d77d08faf3a22e9139c4dbc  src/authority/AuthorizationAuthority.Application/Features/Journal/ArchiveProcessor.cs
a0fd7193cb19d58bc7d5db37a47ee5aa99dfd243a2de7f8d2b81f0820d53fc59  src/authority/AuthorizationAuthority.Infrastructure/Archive/BlobSecurityEventArchive.cs
36f4b7cb71fe9981f1080fff98df551068c398187d5cfeb1d921c8f2e62d342a  src/authority/AuthorizationAuthority.Infrastructure/SpiceDb/SpiceDbProjectionClient.cs
1e8288ff30d0bd98c6f84c522c5da72f6255d710a78857776d5684cc375bff2c  src/authority/AuthorizationAuthority.Application/Features/Projection/ProjectionProcessor.cs
a4e5760f7c01fa164e545a73e2303ccd7d43dd4c36b285d222c4de2352b56779  src/authority/AuthorizationAuthority.Application/Common/CanonicalJson.cs
```

Also in scope: `src/shared/EnterpriseForms.Integrity/Canonical/{RowIntegrityHmac,RowSchemaDefinition,RowValue}.cs`,
`tests/integrity/*`, `tests/authority/AuthorizationAuthority.Tests/{IntegrityBindingTests,ArchiveProcessorTests}.cs`,
`tests/architecture/.../SignedColumnRegistryTests.cs`, `tests/security/EnterpriseForms.SecurityTests/**`.

## 1. What the reviewer is asked to attest

A reviewer who did **not** author the format or the code, recorded in a change/release system outside the business
database, answers each question with PASS / FAIL / FINDING and signs §7.

| # | Question | Normative source |
|---|---|---|
| R1 | Is `DynamicForms/RowHmac/v1` an unambiguous, injective canonical encoding for every registered column type (length prefixes, type tags, explicit NULL, NFC text, normalized decimals, UTC microsecond instants, big-endian UUID)? Can two distinct logical rows ever produce identical bytes? | `row-integrity-format.md` §2–3; §2 below |
| R2 | Does the framing bind every trust-relevant identifier (domain, schema version, key-version reference, entity kind, organization id, primary key, row revision) so that a tag cannot be transplanted across tenants, tables, rows or revisions? | §2 of the format; SEC-P11 step 5; MRK-27 |
| R3 | Is the JSON v1 subset (integers only, ASCII keys, ordinal sort) sufficient to guarantee PostgreSQL JSONB ↔ RFC 8785 agreement? **F-03 decision:** v1 forbids non-integer numbers; no Stage A JSONB column is signed. Confirm, or require a v2 subset before any JSONB column carries floats. | format §3.6; A-16 |
| R4 | Merkle v1: domain-separated leaf/node/empty/index hashing, odd-node promotion without duplication, sorted unsigned item keys — is the construction second-preimage resistant for a versioned map snapshot, and is `leaf_index_digest` a sound complete-set commitment? | `MERKLE_RECORD_MANIFEST_PROTOCOL.md` §2.1–2.2; §3 below |
| R5 | Is the v1 "re-sign every leaf under the candidate operation" policy free of replay between operations (SEC-P05 shows detection of a revision-1 row replayed into revision 2; confirm there is no residual window, including after crash recovery SEC-P09 and restore SEC-P10)? | protocol §2.1; §4 below |
| R6 | Provider adapters: do LocalHmac and Vault Transit produce HMAC-SHA-256 over **identical message bytes** (no double hashing, no base64 confusion, `vault:vN:` normalized to 32 raw bytes)? Re-execute the vector generator in §5 and compare with the committed file. | `CRYPTOGRAPHIC_SIGNING_AUTHORITY.md` §6–7; `stage-a-vault-transit-vectors.json` |
| R7 | Is `ContentService.PrepareAsync` → `IIntegrityMacProvider.SignAsync` the only signing path, and is it gated by actor binding, tenant, authorization-revision CAS, expected content head/root, entity-kind allow-list, key-version pin and delta authorization before every signature? Is there any sign-any-bytes or verify-oracle path on either HTTP plane? | signing authority §3–5, invariant A9; SEC-P11 steps 5–6 |
| R8 | Journal hash chain and archive receipt: is `SHA-256(canonical JSON envelope incl. previous_digest_hex)` adequate as a tamper-evident chain given (a) the archive is append-only (`If-None-Match: *`), (b) the body is now stored as `text` and re-verified against its digest before delivery (A-18, finding A-F12), (c) Confidential Ledger anchoring is not implemented? Does the `CanonicalJson` serializer (`UnsafeRelaxedJsonEscaping`, ordinal key order, integers only) give byte-stable envelopes across platforms? | `JournalChain`, `ArchiveProcessor`, `CanonicalJson`; G14 |
| R9 | Any use of non-constant-time comparison on tags, truncated MACs, weak randomness (`Guid.CreateVersion7` is used for identifiers only), secret-bearing logs/exceptions, or key material at rest outside Vault? | all |

## 2. Canonical serialization rules the reviewer must check (summary; the format document is normative)

- Framing: `0x10 ‖ len16 ‖ "DynamicForms/RowHmac/v1" ‖ schema_version16 ‖ key_version32 ‖ len16 ‖ entity_kind ‖ org_uuid16 ‖ pk_uuid16 ‖ row_revision64 ‖ column_count32 ‖ columns…`; all integers big-endian; UUIDs RFC 4122 byte order (`Guid.TryWriteBytes(bigEndian: true)`).
- Columns: registry per `(entity_kind, schema_version)`, lowercase ASCII snake_case, sorted by ordinal bytes; every registered column present exactly once; the column name is part of the bytes; **absent ≠ NULL**, NULL is an explicit type tag; extra or missing columns are rejected.
- Types: fixed-width two's-complement integers; 1-byte boolean; decimals as normalized `(minimal two's-complement coefficient, scale)` with trailing zeros stripped and floats rejected; `Date` = days since epoch; `TimestampUtc` = microseconds since epoch with mandatory offset and no sub-microsecond input; `Text` = NFC then UTF-8, length-prefixed, empty ≠ NULL; byte arrays length-prefixed; JSON v1 subset (integers only, ASCII keys, ordinal order, minimal escapes).
- `row_signature = HMAC-SHA-256(org_key[key_version], canonical_bytes)`; `canonical_payload_digest = SHA-256(canonical_bytes)`; the tag is never part of its own input; `key_version` is a reference, not material.
- Stage A registry: `form_records` schema v2 (owner columns deliberately **not** signed — ownership is authority state), `field_string_values` v1. `SignedColumnRegistryTests` fails CI if the compiled EF model exposes a protected table or column that the registry does not cover.
- Business staging writes rows exactly as signed; the authority re-reads the staged rows, re-canonicalizes, verifies the HMAC through the provider and recomputes the Merkle root before CAS-advancing the head (`ContentService.CommitAsync`). Readers repeat the verification per read (`VerifyRecordProjection`).

## 3. Merkle manifest: membership versus completeness

- Leaf: `SHA-256(0x00 ‖ "DynamicForms/MerkleLeaf/v1" ‖ 0x0011 ‖ item_key[17] ‖ row_signature[32] ‖ payload_digest[32] ‖ kind[1] ‖ content_revision64 ‖ operation_uuid16)`; node `SHA-256(0x01 ‖ "DynamicForms/MerkleNode/v1" ‖ left ‖ right)`; odd node promoted, not duplicated; empty root `SHA-256(0x02 ‖ "DynamicForms/MerkleEmpty/v1")`; `leaf_index_digest = SHA-256(0x03 ‖ "DynamicForms/ManifestIndex/v1" ‖ count64 ‖ Σ(0x0011 ‖ item_key ‖ leaf_hash))`.
- **Membership** (an inclusion path to a trusted root) is never used as proof that nothing was omitted. **Completeness** in v1 is proven only by set equality against the authority-held complete leaf index for the accepted `(record, content_revision, committed_operation_id)`: `VerifyRecordProjection` requires every expected key present exactly once, every signature valid, the recomputed root equal to the accepted root, and the content checkpoint archive receipt present; extra, missing, duplicated or re-signed-under-another-operation rows quarantine the head (SEC-P04, SEC-P05, SEC-P10).
- Partial reads return the authorized subset after the full-set verification; hidden keys are never disclosed.
- The tree is a versioned map snapshot, not an RFC 9162 append-only log; anti-rollback comes from the authority content head plus the hash-chained content checkpoint stream, not from the tree.

## 4. Replay, recovery and freshness assumptions the reviewer must weigh

1. **Anti-replay of content**: every leaf is re-signed under the candidate operation id; an older valid row carries an older `committed_operation_id`/revision in its signed bytes and fails set equality (SEC-P05). Business-side `committed_operation_id` is a hint; the authority head is the only source of currency.
2. **Command idempotency**: key = `(organization, actor, idempotency key, canonical body minus expected_authorization_revision)`; same key + same body returns the recorded result; same key + different body is `IDEMPOTENCY_CONFLICT` (SEC-P08, SEC-P09). Operation ids for content are caller-proposed but deterministic over the same inputs (A-03).
3. **Effectiveness pipeline**: a command is Effective only after its journal entry is archived (receipt recorded) **and** the ordered SpiceDB projection has applied its revision under the cursor-marker CAS. While `projection_applied_revision < authorization_revision`, or any authorization-stream entry lacks a receipt, or the organization is quarantined, every check fails closed for everyone in that organization (SEC-P06, SEC-P07, SEC-P12).
4. **Crash recovery**: deterministic crash points after journal commit and after the SpiceDB write but before acknowledgement are reconciled by re-reading the cursor marker and verifying every tuple of the delta; ambiguous writes are never acknowledged blindly (SEC-P09).
5. **Archive**: create-only blobs; redelivery of identical bytes is idempotent; differing bytes under the same key quarantine; a body whose SHA-256 no longer equals its chained digest is never delivered (`JOURNAL_BODY_DIGEST_MISMATCH`, A-18). The Azurite container is a test double for WORM storage; it is not a ledger receipt.
6. **Restore**: restoring BusinessDb to an older dump does not revive revoked grants or older content (SEC-P10). Restoring AuthorityDb, epoch fencing and DR-01..36 are **not** implemented or tested (G14 open).
7. **Not claimed**: confidentiality of plaintext typed values against a BusinessDb reader; search completeness (G06); protection against a compromised authority host, Vault or its administrators.

## 5. Reproduction for the reviewer (do not trust author-run output)

```sh
git fetch origin && git checkout e6df484b2ee35ed822cd011a06f41b3615ec10c8
python scripts/integrity/verify_vectors.py && node scripts/integrity/verify-vectors.mjs
dotnet test --solution EnterpriseForms.All.slnx -c Release --results-directory artifacts/test-results --report-xunit-trx -- --minimum-expected-tests 10
python scripts/ci/assert_test_totals.py artifacts/test-results --min-total 130 --min-files 4
cd infrastructure/docker && cp env.example .env
docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait \
  business-db authority-db spicedb vault vault-transit-init archive-store azurite authority-migrate authority business-migrate api attacker-sql
cd ../.. && dotnet build tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj -c Release
dotnet test --project tests/security/EnterpriseForms.SecurityTests/EnterpriseForms.SecurityTests.csproj --no-build -c Release -- --minimum-expected-tests 13
python security_validation/validate_security_evidence.py artifacts/security/gate-a-evidence.json
# R6 — real Vault Transit vectors (throwaway Vault container, published test key imported as type=hmac, exportable=false):
python scripts/integrity/generate_vault_vectors.py            # rewrites docs/security/stage-a-vault-transit-vectors.json
git diff --stat -- docs/security/stage-a-vault-transit-vectors.json   # expect only generated_on_utc to differ
```

`stage-a-vault-transit-vectors.json` (committed, result `PASS`): seven `ROWV1-*` cases where Vault Transit
(`vault:v1:` token decoded to 32 bytes exactly as `VaultTransitHmacProvider.ExtractRawMac`), .NET `HMACSHA256` and the
published vector `row_signature` agree byte for byte; a last-byte flip of one tag verifies `false`. The key is the
**published** test key from `row-integrity-v1.test-vectors.json`; no deployment key equals or derives from it.

## 6. Known limitations the reviewer must weigh (author-declared)

1. Vault dev mode and the Azurite archive are test doubles; Azure Managed HSM provider and Confidential Ledger anchoring are not implemented (G04, G11, G14 remain OPEN). Production startup refuses non-HSM providers by construction and therefore cannot start today.
2. Only `form_records` (v2) and `field_string_values` (v1) are registered; every other protected table arrives with its feature and must be added to the registry and the inventory test.
3. The TestIdentityProvider (HS256 shared key) stands in for Entra ID; it is refused outside Development/Testing.
4. Search completeness (G06) is out of scope; no search endpoint exists in Stage A.
5. Transport between business API and authority is plain HTTP inside Docker; mTLS and workload identity are production requirements (PB-05/PB-08) and not emulated.
6. Finding A-F12 (journal body stored as `jsonb`) was found by the clean-worktree Gate A run and fixed in `e6df484`; the earlier run on commit `5e56238` passed SEC-P12 only because the Azurite conflict path was not reached. The reviewer should treat the archive/digest binding (R8) as freshly changed code.
7. The SEC-P12 conflict path (delayed PUT landing after unpause, retry 409, read-back compare) is timing-dependent in the Docker test; it is covered deterministically by `ArchiveProcessorTests` with the in-memory archive.

## 7. Sign-off record (to be completed by the reviewer, stored outside BusinessDb)

Not completed. No reviewer has been engaged as of 2026-10-18. The author must not fill this in.

```
Reviewer: ______________________  Organization/role: ______________________  Independent of authorship: [ ]
Commit reviewed: ______________________  Image fingerprint(s): ______________________
R1 [ ] R2 [ ] R3 [ ] R4 [ ] R5 [ ] R6 [ ] R7 [ ] R8 [ ] R9 [ ]
Findings (ID, severity, required change): ______________________
Decision: APPROVED / APPROVED WITH CONDITIONS / REJECTED   Date (UTC): __________  Signature: __________
Record location (change/release system id): ______________________
```
