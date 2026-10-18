# PB-10 — Canadian Residency and Privacy Implementation Standard

**Accepted:** ADR-PB-010. **Status:** Binding specifications only; no resources or regulatory compliance evidence have been validated.

## Threat and deployment boundary

First production deployment is **Canada Central**, with only explicitly approved Canadian locations for backup, replication and disaster recovery (Canada East preferred). The trust authority, its independent PostgreSQL instance, SpiceDB datastore, signer/HSM, key backups, replayable security journal and authorized content revisions are within the same country custody boundary while remaining **independently administered** from business data.

**Do not equate** data residency with security, sovereign legal jurisdiction, processing location, identity-provider data location, or AODA/PIPEDA/public-sector compliance. Each contract, applicable law, Microsoft service-specific location behaviour and subprocessor must be separately evaluated with accountable reviewers.

## Component checklist

| Component/data | Location and control | Deployment/DR proof |
|---|---|---|
| Business PostgreSQL | Canada Central; independently authorized Canadian backup/replication | Query region/storage/geo-destination, restore proof, tenant/field integrity checks |
| Authority PostgreSQL | Separate server, identity, administration, networking; Canadian locality | Independent journal restore/epoch fencing test, no permission rollback |
| SpiceDB datastore/graph | Canadian Authority boundary, journal-based rebuild | Replay + lag consistency tests |
| API/worker/web and queues | Canada Central containers and Canadian log sinks; no record content in image/CI logs | Image/runtime inventory, egress allowlists and log scans |
| Azure Blob and quarantine | Canadian primary/replica, malware scan before commit, encrypted immutable version archive | Actual replication target and recovery test; avoid third-party scanner data exfiltration |
| Managed HSM | Canada Central or another approved available Canadian region | Canada East is currently **not** a supported extended region. Test manual same-security-domain DR from Canadian backup/quorum to a supported Canadian destination |
| Vault Transit | Developer/test only with disposable keys/data | Prohibit connection to production data, no production Vault dependency |
| Azure Confidential Ledger | Approved Canadian primary and paired region **after service availability verification** | Verify receipt and paired location; only digests/checkpoint metadata, not full record bodies |
| Durable security journal | Append-only recoverable archive in approved Canadian storage | Full event bodies + checkpoints + replay/restore integrity |
| Logs / App Insights / Monitor | Canadian workspaces, no personal content or metadata in traces | Query workspace region, process/egress policy, automatic PII leakage tests |
| Entra/OIDC | Separate risk assessment of authentication data processing and international access | DPAs, provider options and minimal claims; no claim all auth processing is Canadian |
| CDN/WAF/Email/Support/DevOps | Must not persist or transmit customer records across unapproved geography | Verify global routing, telemetry, processor and support-access policies |

## Bicep and CI controls

- Parameterize `allowedDeploymentLocations=['canadacentral','canadaeast']`, with `preferredPrimary='canadacentral'` and `preferredRecovery='canadaeast'`; each service needs its **own supported-region inventory** and specific replication-target validation before provisioning.
- Reject any data-bearing Azure resource with `location` not in allowlist, **and** reject unknown/global/multi-region storage behaviors until individually approved. Treat RG region, endpoint DNS and Azure Policy allowed-locations alone as insufficient evidence.
- Produce a signed, environment-specific resource-location manifest after `what-if` and actual deploy. Inspect `replication` and backup destinations rather than assuming Canada East for every service.
- Enforce no customer data in workflow logs/artifacts, container build context, source control, developer CLI telemetry or LLM/code-assistant prompts without explicit confidentiality authorization.
- If a required service lacks Canadian region compatibility, **stop production release** and escalate with a documented decision. Dev emulator or US regional service is not an acceptable silent production substitute.
- Treat remote service and identity-provider endpoints as potential off-region processing and review separately; do not falsely claim Canada-only packet transit.

## Managed HSM recovery design gate

1. Confirm service availability for Canadian primary and emergency destination and ensure administrative teams are distinct from Business PostgreSQL DBAs.
2. Store newest full HSM backups **in Canada**, separately protected; preserve quorum private keys and security-domain data with designated independent custodians in Canada.
3. Run an isolated staging DR drill that builds the destination HSM, activates security-domain recovery, backs up the destination, restores the preserved source backup, updates endpoint references and validates signing + verification of old key versions.
4. Measure full time and any data/key-version loss. If four-hour RTO cannot be shown, production remains blocked. No assumption of automatic multi-region replication to Canada East.
5. Keep production keys non-exportable; encrypted HSM backups and security-domain recovery are not user-facing raw-key exports.

## PostgreSQL recovery design gate

- Geo-backup alone can lag by ~one hour; do not present it as satisfying 15-minute RPO.
- Build independent durable commit/journal replication or other verified short-RPO recovery strategy and test primary loss, paired-region delay, simultaneous Business/Authority skew and corruptions.
- On business rollback, quarantine absent committed revisions; never roll back trusted revocation/authorization epochs.
- Failed proof requires an alternative architecture or explicit reapproval of target; cannot silently waive the previously accepted PB-09 metrics.

## Privacy and lifecycle requirements

- A per-organization classification matrix covers user-generated fields, document metadata, filenames, audit data, forms and images. Classified content must be encrypted under existing policy; encryption does not remove residency constraints.
- Collect only needed identifiers in dashboards, exports, logs and audit projections. Partition retention by subject/category, legal hold, jurisdiction and actual contract; keep mandatory event evidence only for approved durations.
- Deletion, subject access, consent/notice, litigation holds, organization shutdown and recovery history require documented rights and legal review. Deletion from mutable storage may not erase retained WORM/ledger history; limit checkpoint metadata to avoid permanent sensitive disclosure.
- Validate processor agreements, support access, intrusion telemetry, emergency break-glass evidence, subprocessors and authority-to-business data transfers.
- No default legal retention period is asserted here; policy owners must set it before hosting sensitive production data.

## Acceptance

Implement and document the tests in `docs/testing/PB10_DATA_RESIDENCY_ACCEPTANCE_TESTS.md`. A release is blocked when region availability, replication data flows, production identity, backup recovery, or unresolved privacy approvals violate the accepted Canada-first boundary. This remains true if functional tests are green.
