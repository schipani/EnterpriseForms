# ADR-PB-010 — Canada-First Data Residency, Privacy and Controlled Recovery

- **Status:** Accepted by product owner
- **Decision date:** 2026-10-17
- **Finding:** PB-10; resolves major pre-build decision C (data residency)
- **Scope:** First production deployment; subsequent geographies require a new approved deployment boundary and ADR
- **Implementation status:** Specification, not provisioned resources or a claim of legal compliance.
- **Companion:** `docs/privacy/PB10_CANADIAN_RESIDENCY_AND_PRIVACY_STANDARD.md`, `docs/privacy/PB10_DATA_FLOW_RESIDENCY_REGISTER.csv`, `docs/testing/PB10_DATA_RESIDENCY_ACCEPTANCE_TESTS.md`.

## Approved decision

1. **Canadian custody first.** Customer records, encrypted/plaintext field contents, documents and original files, data-bearing metadata, signed manifests, replayable security events, archival audit bodies, evidence, primary/secondary PostgreSQL data, backups, logs containing customer data, customer encryption keys and HSM security-domain recovery artifacts must remain stored in approved **Canadian Azure regions** for first production deployment. External identity processing and other unavoidable data flows must be documented and separately reviewed; mere Canadian storage does not prove every network packet or provider operation stays within Canada.
2. **Preferred primary:** Azure **Canada Central** (`canadacentral`). **Preferred disaster recovery location:** **Canada East** (`canadaeast`). They are an Azure regional pair. This is a placement preference **subject to per-service regional availability, replication destination, contractual residency policy and successful recovery tests**, not an assertion that every Azure feature automatically supports this pairing or auto-fails over.
3. **No silent fallback:** Infrastructure as code, runtime configuration, backup tooling and the deployment pipeline must reject customer-data-bearing workloads, storage, archives, backup copies, ledgers, key custody or exports targeting unauthorized locations. A service that cannot satisfy the policy is a **blocking architectural conflict**, not permission to move it to a US region.
4. **Azure Confidential Ledger:** If available and approved in Canada Central, verify at provisioning that the ledger's mandatory paired-region replication remains **within Canadian regions**. Document both locations; Microsoft documents that ACL pairing cannot be disabled. Ledger stores minimal cryptographic checkpoints/receipts rather than sensitive full activity payloads. Its availability, recovery and access policies still require testing.
5. **Azure Managed HSM:** The first deployment uses a Canada-resident production HSM with non-exportable key material. **Do not promise automatic multi-region HSM replication to Canada East**: Microsoft's Managed HSM documentation currently states that Canada East cannot be an *extended region*. Design **Canadian-only manual disaster recovery** using independently protected full HSM backups, security-domain recovery data and quorum keys, subject to confirmed destination-region service availability and tested recovery. Do not move HSM keys or domain-quorum artifacts outside the approved Canadian custody boundary. Preserve historic key versions and test recovery within the approved four-hour RTO; if impossible, block production or request a new approved decision rather than silently relaxing the targets.
6. **Azure Database for PostgreSQL:** Conventional geo-redundant backups are asynchronous; Microsoft states geo-restore may have **up to one hour of RPO**. Our previously approved **15-minute business-data RPO** therefore requires a separately designed and *tested* protected replication/journal/recovery strategy. Independence of the authority and no accepted privilege rollback remain absolute. Missing committed content must be detected/quarantined, not presented as current.
7. **Telemetry, identity and transfers:** Configure customer-data-free OpenTelemetry and Azure Monitor with Canadian workspace/storage. Redact filenames, personal identifiers, authentication claims, form values, document content, raw search terms, and tokens from application telemetry. Identify and review cross-border risks from Entra workforce authentication, operational support, email, scanners, package registries, content delivery, CDN/WAF, browser crash reports, external vendors and developer tooling, not just database and Blob geography.
8. **Privacy and retention:** Before production, document which laws, contracts and operational policies apply to the customer/use case; perform privacy/security impact assessments when required. Make retention periods and deletion/legal-hold requirements policy-driven per organization within mandatory constraints; do not invent default legal retention periods. Audit all archival and disposal transitions, secure erased keys where permitted, and ensure legally held or immutable archives are not prematurely removed. Define tenant offboarding separately under PB-13.
9. **Future international deployment:** Support region-specific deployments with an independently provisioned business store, Authority/SpiceDB, crypto and audit evidence per allowed geography. No implicit cross-region tenant replication or tenant region switch; formal migration requires authorized controlled re-encryption, journal checks, data-subject/contract review and revocation-safe reconciliation.
10. **Level 2 and fail closed:** Residency must not weaken the independently administered Authorization Authority, key separation, merkle/integrity verification, audit provenance, replayable journal, search completeness, or fail-closed recovery. Do not start production if approved geography, available services and recovery proofs are incompatible.

## Binding rollout gates

- Maintain a per-component **residency/replication register** with region, primary/backup destination, controller, admin/data access, retention, trust boundaries, processor and proof links. Every customer-data store and processing pathway must be accounted for.
- Check approved locations via Bicep parameters plus deployment policy/CI inventory. **Location-based policies do not by themselves validate managed replication, globally delivered services or subcontractor access**: combine provider-specific control-plane queries, service contracts and independent evidence.
- Verify Canadian placement for PostgreSQL server(s), SpiceDB backing DB, containers/worker logs, storage/versions, immutability archives, HSM/key backup, ledger primary/paired region, Log Analytics workspace, scanner/quarantine, pipeline logs/artifacts, and secret/DR copies.
- Exercise Canada Central outage scenarios; prove Canadian-only recovery, ≤4-hour RTO objective, ≤15-minute business-data RPO objective or fail promotion, and **zero authorization rollback**.
- Review key custody and quorum holder procedures, delete/offboarding and legal hold behaviour, and cross-border risks with the designated privacy/security owner.
- Security/Privacy owner sign-off on the complete register, evidence and exceptions is required prior to production; no implicit waivers.

## Rationale and known limitations

Regional pairing is a *geographic relationship*, not assurance that all services deploy or fail over there. Microsoft documentation separately confirms ACL paired-region replication, PostgreSQL geo-backup lag, Managed HSM extended-region restrictions and manual key recovery. Service support must be reverified for the subscription and region at provisioning. Staging may not claim Canadian availability, compliance, sovereignty or the target RPO/RTO merely because the architecture is documented.

## References (verify as part of implementation)

- Azure region list: https://learn.microsoft.com/en-us/azure/reliability/regions-list
- Azure Confidential Ledger residency: https://learn.microsoft.com/en-us/azure/confidential-ledger/data-residency
- Azure PostgreSQL backup and geo-restore: https://learn.microsoft.com/en-us/azure/postgresql/backup-restore/concepts-backup-restore
- Managed HSM multi-region replication: https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/multi-region-replication
- Managed HSM DR: https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/disaster-recovery-guide
