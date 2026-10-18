# Mandatory Level 2 Security Proof — Execution Runbook

> Status: implementation contract; **no tests have been run or passed merely by creating these files**. Do not confuse this with a working Docker environment.

## 1. Provision the disposable test environment

Create a Docker Compose integration profile **in the application repository** containing two different PostgreSQL 18 instances/clusters or independently administered equivalents (`BusinessDb`, `AuthorityDb`), SpiceDB, development Vault Transit, Trust Authority API/worker, ASP.NET Core API/BFF/worker, and independent append-only security journal test storage. Provide an object-store emulator for file tests and explicit identity/ledger test adapters. The business API may not share DBA passwords, authority administration identities, key-signing credentials, or unrestricted graph-writer credentials.

Use isolated test principals and an explicit tenant/organization. Seed two users, two teams, one form and signed record, encrypted field values, a child record and a document reference. Authenticate through the actual API and authority API; do not use direct database reads as substitutes for API assertions. Isolate cryptographic test keys from production. Start clean for each scenario or record fixture sequencing.

## 2. Execute the tests

Implement `SEC-P01` to `SEC-P12` as automated .NET integration tests (xUnit/NUnit) plus trusted test harness fault-injection methods. Require normal and tampered state assertions, authority revision/head before and after, business API outcome, trusted archive receipt and captured logs. Use direct SQL **attacker identity limited to BusinessDb** to modify projection rows in negative tests. Also verify forbidden attempts against AuthorityDb/SpiceDB/Vault with separate identities. Do not run tests against production/shared data.

Group long-running snapshot-restore, multi-container crash, and recovery chaos scenarios in isolated scheduled/pre-release jobs; do not falsely mark them PASS if they were not executed. CI for each protected commit must at minimum run deterministic unit, schema/contract and bounded service-integration tests; Gate A aggregates current qualifying passes for all 12 tests on the **same code/image version**.

## 3. Evidence and verification

Every test emits immutable CI artifacts containing (a) machine-readable result, (b) sanitized API outputs and trusted authority state/revision and (c) runner/service logs or traces, excluding secrets/PII. `security-evidence.schema.json` defines the manifest structure, and `validate_security_evidence.py` checks its shape, required tests, identical source commit/image fingerprint, UTC timestamps, and checksum of **local referenced files**. A JSON `PASS` is only a claim; runner logs, actual pipeline execution and independent verification must substantiate it.

Example use after implementing and running the real tests:

```sh
python3 security_validation/validate_security_evidence.py artifacts/security/gate-a-evidence.json
```

No sample PASS manifest is provided because tests have not been executed. Treat missing evidence, missing/modified logs, failed required tests or mismatched deployment fingerprints as `BLOCKED`. Compare against original test runner exit codes and immutable CI job metadata.

## 4. Review and progression

Stage A gate owner examines CI provenance, a complete row for each `SEC-Pxx` ID, cryptographic and authorization-state assertions, and unresolved incidents. Record approval in an access-controlled change/release system **outside the business database**. Stage B protected endpoints must be disabled until approved evidence corresponds to the running image digests. Test data shall be synthetic only.

For Azure staging re-run the mandatory matrix and complete phase-appropriate PG/SEC/SIM/CONS/MRK/SIG/DR tests using real service boundaries, identities, HSM and Confidential Ledger. Confirm managed identity denied cross-service access, signing and key rotation, authoritative archive receipts, restore with revoked access, region/network constraints and separate approval. Do not weaken or replace the required security controls for performance or availability.

## 5. Automation ownership

- `tests/Security/Authorization` — SEC-P01, P02, P03, P06, P11
- `tests/Security/Integrity` — SEC-P04, P05, P10
- `tests/Security/DistributedConsistency` — SEC-P07, P08, P09
- `tests/Security/AuditEvidence` — SEC-P12
- `tests/Security/DisasterRecovery` — P09, P10 (scheduled destructive tests)
- `infrastructure/docker` — disposable isolated stack, health checks and clean teardown
- `scripts/ci` — qualification/test-run execution and evidence artifact upload

**Existing security tests remain normative:** reuse security_hardening/SIM-01..SIM-42 and trust_authority/CONS/MRK/SIG/PG/DR tests without duplicating their assertions as unrelated implementations. Cross-reference from every SEC-P test to existing IDs in the CSV matrix.
