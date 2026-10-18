# PB-12 — Security Evidence, Exceptions and Release Gate Standard

**Source:** ADR-PB-012. **Status:** Approved implementation standard, not implemented enforcement.

## 1. Sources of truth and evidence mapping

The canonical specification repository remains the source of requirement definitions, including SEC-P01..SEC-P12, critical gaps, form/security contracts and privacy controls. Generate an evidence register at each release candidate in a machine-readable artifact conforming to `docs/governance/PB12_RELEASE_EVIDENCE_SCHEMA.json`.

Fields must bind:
- the **requirement ID** and rule category (mandatory Level 2 vs separately waivable noncritical finding);
- a **feature owner**, test or proof **case ID**, and required execution phase (local Docker, Azure staging, regression, production cutover);
- reported `pass|fail|not_run|blocked` plus the actual test-execution UUID, completion instant and tester principal;
- **immutable evidence object reference**, approved Canadian storage location, SHA-256 and externally verifiable origin/attestation;
- **Git commit**, exact OCI image digest per affected service, Bicep/IaC commit, migrations/config/policy versions, test suite and environment identity;
- independent **reviewer/approval decision**, security role and approval time; no self-approval for independently controlled scopes;
- policy evaluation reference/digest and deployment decision (allow/block).

A SHA-256 hash by itself proves only equality to some bytes, not that those bytes are genuine. Evidence must be authenticated by the execution identity and tied to trusted source/release provenance. No full sensitive audit event payloads in public GitHub artifacts.

## 2. Evidence-producing workflow

1. Compile a **release requirement matrix** from normative specs. Missing test ownership is a failure for mandatory controls.
2. Run deterministic CI checks (unit/API/architecture/contract/security) and real isolated Docker Stage A tests. Capture failure injection and authorization/revision evidence.
3. Build/sign/attest immutable images (ADR-PB-011), deploy exact digests to real Azure staging and run the required cloud identity/HSM/ledger/DR/Canada residency tests.
4. Produce independently verifiable test results with stable test IDs, environment/source/image bindings, provenance references and canonical event IDs.
5. Publish the evidence register to protected Canadian-region immutable storage; enforce appropriate privacy filtering, RBAC, malware-safe artifacts and retention/legal holds.
6. Evaluate a separately protected **release policy** against requirements and exact candidate deployment, including resolved exceptions. Fail closed on missing/stale/mismatched evidence and failed tests.
7. Independent production approver reviews results, residual risk and rollback plan. Trust Authority deploy path has separate approval and credential boundary.
8. Anchor a minimal signed release-evidence digest/checkpoint to Confidential Ledger where required. Do not place raw evidence bodies or arbitrary personal data in Ledger.
9. After deployment, verify live digests, deployment identity, active policy revisions and external checkpoint, then archive release and any later revocation/incident status.

## 3. Exceptions

Noncritical exceptions require: scoped finding ID; owning accountable human/group; affected environment, configuration and digest; independently approved risk assessment; documented compensating control; expiration; mandatory scheduled revisit and remediation verification. No "permanent" or unbounded exception.

Forbidden exceptions include malicious-business-DB-to-authority boundary bypass, fabricated ACL grant, cryptographic signer compromise without containment, unauditable privilege change, unauthorized data export, data residency violation, missing required independently anchored/recoverable journal, stale revocation, and missing mandatory proof. Such incidents BLOCK release irrespective of deadlines.

## 4. Separation of duties

- The submitter/implementer must not approve their own independently reviewed critical release.
- CI/release-maintainer cannot change required gates via a normal unprivileged pull request.
- Business deploy operators cannot approve Trust Authority policy/image/signing/identity deployments.
- Security approvers must be authorized by independent system-level identity and policy; an email string in a JSON file does not constitute approval.
- Protected scope and service identities must be enforced by GitHub environment rules and Azure RBAC, not merely in a dashboard.

## 5. Residency, retention and privacy

Customer-bearing test artifacts, traces, backup copies and ledger references must remain in approved Canadian locations per ADR-PB-010. CI should use synthetic fixtures and redact names, sensitive record contents and secrets. Retention windows, legal holds, disposition, and operator permissions are jurisdiction/customer policy decisions to be approved before production; do not invent statutory minima.

## 6. Dashboard

Existing role-gated Administration → Release Security Dashboard may render:
- environment/image/build/policy status;
- mandatory control coverage, missing proof and tests last executed;
- per-control evidence links (authorized/redacted), current exception owners and expirations;
- release history, reviewer identities, recent gate failures and DR/accessibility status.

Cards load independently, fail securely, expose per-card refresh, honor tenant/operator and step-up restrictions and never offer "override critical gate" actions. The dashboard reads the authoritative release-policy decision output.

## 7. Policy-gate negative tests

Use `docs/testing/PB12_RELEASE_GOVERNANCE_ACCEPTANCE_TESTS.csv` as the initial scenario inventory. Include fabricated "pass" evidence, stale artifacts, switched image digest, absent `SEC-P03`, post-review signed image change, revoked approver, unapproved jurisdiction, leaked real customer fixture, unreviewed HSM config, illegal exception waiver, missing independent approval, and tampered immutable evidence.

## 8. Not yet proven

ADR-PB-012 is an approved design. Until CI pipelines, protected artifact service, signing, policy evaluator, role permissions and real tests execute, no Level 2 release has been evidenced.
