# G03 — Implementation and Security Evidence Checklist

The files in this package **specify** Merkle-based record completeness protection; no production code, migrations or Azure resources are implemented by this document. The order below is designed to prevent accidental fail-open endpoints while development is in progress.

1. [ ] Verify authority and business PostgreSQL identities/deployment are independently administered. Confirm business SQL owner/DBA cannot read/write the authority database, KMS credentials, SpiceDB write endpoint, or trusted checkpoint.
2. [ ] Freeze v1 canonical row/HMAC and v1 Merkle wire formats, document all signed columns, and run cross-language test vectors (including `merkle-v1.test-vectors.json`). Any incompatible format change increments version.
3. [ ] Implement authoritative record registration, immutable manifest commit and complete ordered leaf catalog; implement content-head CAS and monotonic content-integrity checkpoint/journal in the **same authority transaction** as the accepted manifest insert. Implement cryptographic digest and leaf-count validation.
4. [ ] Implement the strictly authorized content prepare operation and no generic signing endpoint; bind exact delta, actor, authorization revision, record, expected previous head and idempotency key. Use application controlled staging and no read before commit.
5. [ ] Produce immutable candidate snapshot, HMAC-signed item envelopes, verified full read-back of the staged candidate, group graph checking, and attachment object version/ciphertext hash checking.
6. [ ] Implement `VerifyRecordProjection` to compare **exact independently expected set** with returned leaf set, without permitting caller root/leaf-count/key overrides. Evaluate current ACL/PIN/encryption floor; strip hidden field identifiers and rules before JSON response.
7. [ ] Remove any read/search/file/export route that trusts `form_records.owner_user_id`, business ACL projections or local `published` flags without the independent authority revision gate and content head.
8. [ ] Implement trusted reconciliation for unrequested extra business rows; mark incidents and quarantine. An injected row must be invisible even before the periodic reconciliation job detects it.
9. [ ] Extend observability to missing/duplicate/replay leaf failures, manifests per record, HSM batch costs, projection latency and trusted ledger gaps without exposing protected content.
10. [ ] Perform MRK-01..MRK-32 fault-injection tests, repeat CONS-01..CONS-30 and original SEC suite after integration, benchmark high-volume forms, document source-control test evidence and signed deployment artifacts.
11. [ ] Conduct independent review of authorization graph integration, search completeness (G06), database privileges (G08), HSM key custody (G04) and restore/ledger anti-rollback (G14). These remain separate production blockers until verified.

**Go/no-go:** No bypass for missing signatures, missing authority manifest, corrupt/missing leaf, unknown key version, stale content head, unavailable service, or slow verification. If integrity cannot be verified, deny protected release and alert; do not regenerate a signature on read to hide evidence.
