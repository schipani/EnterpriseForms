# PB-10 — Canadian Data Residency and Privacy Acceptance Tests

These are **required, not yet executed**, tests. Attach actual Bicep outputs, Azure resource listings, subscription/region confirmations, privacy approvals and DR evidence to the release register.

| ID | Test | Expected result |
|---|---|---|
| RES-T01 | Bicep plan attempts an unapproved US business database | Reject before deployment |
| RES-T02 | Storage secondary replication points outside Canada | Reject, including implicit provider replication |
| RES-T03 | Inspect runtime BusinessDb/AuthorityDb/SpiceDB resources | Separate administration and Canadian location proven |
| RES-T04 | Azure Confidential Ledger regional availability/paired region query | Both ledger processing/storage locations in approved Canada regions, else deployment blocked |
| RES-T05 | Managed HSM attempt to enable Canada East extended region | No assumption of supported auto replication; manual Canadian DR design required |
| RES-T06 | Managed HSM full backup, security-domain quorum recovery | Canadian backup, restore old key versions, log evidence; timed ≤4h full-stack RTO target |
| RES-T07 | PostgreSQL Canada East geo-restore using normal geo-backup | Measure actual lag, do not claim ≤15m based only on geo-backup |
| RES-T08 | BusinessDb restored prior to latest trusted authorization revision | Revocations remain enforced; outdated content quarantined |
| RES-T09 | Missing acknowledged committed business-data revision | Clear quarantine/incident, never false completeness |
| RES-T10 | Application/worker log contains protected field value | Automated redaction test fails pipeline |
| RES-T11 | Filenames/search terms/auth tokens placed in OpenTelemetry | Reject/redact and prove no storage in diagnostics |
| RES-T12 | Third-party malware scanning provider moves file abroad | Block until separately approved Canadian processing path |
| RES-T13 | CI pipeline includes full sensitive database dump | Prevent ingestion and artifact publication |
| RES-T14 | Cross-organization tenant attempts off-region data migration | Deny unless separate approved migration/change command |
| RES-T15 | Attempt to disable legal hold or erase WORM evidence under retention | Fail and audit attempt; approved deletion pathways only |
| RES-T16 | Backup/export restore by staff to local laptop/non-Canadian storage | Deny or require approved exception; record evidence |
| RES-T17 | Identity provider/push notification/support data flows | Contract and process location documented; privacy approval before production |
| RES-T18 | Canada Central region lost | Recovery remains in approved geography, measures RTO/RPO, authority never rolls back |
| RES-T19 | Region service unavailable in Canadian footprint | Block deployment; do not silently select another geography |
| RES-T20 | Compliance review of all service entries in residency register | Every entry has ownership, location, backup, processor and sign-off evidence |

**Pass criteria:** No out-of-policy customer-data-bearing resource, missing recovery/region proof, unsupported claims about ACL/HSM/PG geo-failover, unassessed identity/telemetry processing or unauthorized exception. Separate security and privacy approvers must sign off before production.
