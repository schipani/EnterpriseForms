# Indexed search: integrity verification cost reassessment (C-125)

- **Status:** **design comparison for owner decision. Nothing is implemented.** IX-1 does not begin until the owner decides whether to retain or replace the per-record seal approach of design §18.1.4.
- **Kind:** owner-led review input; not an independent assessment.
- **Prepared by:** Claude (Opus). All volumes are **estimates**, from the stated assumptions. No measurement was made.
- **External facts** were read from Microsoft documentation on 2026-11-06 (§2). Anything not found there is marked **unverified**.

## 1. Question

Can indexed search keep integrity guarantees equal to or stronger than §18.1.4 with substantially fewer remote Managed HSM operations than the per-record seal design? In particular, can it avoid the about 43 seal verifications per second of the S-B sweep?

## 2. Azure Managed HSM facts and unverified assumptions

| Fact | Status | Source |
|---|---|---|
| Managed HSM supports HMAC sign/verify (HS256, HS384, HS512) with `oct-HSM` keys of 128, 192 and 256 bits | Documented | `managed-hsm/about-keys-details` |
| **No HMAC throughput is published.** Tables exist for RSA, EC and AES only | Documented absence; **HMAC throughput unverified** | `managed-hsm/scaling-guidance` |
| EC P-256 per HSM instance, one partition: **sign 330/s, verify 130/s**. P-384: sign 160, verify 82. AES-KW unwrap about 2,200/s | Documented | `scaling-guidance` |
| Figures are for one partition; up to about 3× with all three; "capacity plan with only one partition available" for guaranteed throughput; figures assume one key | Documented | `scaling-guidance` |
| "Managed HSM does not throttle on cryptographic operation transactions"; the limits are hardware limits | Documented | `scaling-guidance` |
| No batch sign/verify API found | Not found | REST `keys/sign` |
| No Ed25519/EdDSA; EC curves P-256, P-256K, P-384, P-521 | Documented | `about-keys-details`; REST algorithm list |
| **Price:** an hourly fee per HSM pool (Standard B1). Microsoft's page did not render the amount. A third-party listing gives USD 3.20/h (≈ USD 2,336 for a 730-hour month) | **Unverified** | Azure pricing page (amount not rendered); CostBench (third party) |
| **Per-operation billing for Managed HSM:** not stated. The per-10,000-operations wording on the page concerns Key Vault vaults | **Unverified** | Azure pricing page |

**Consequence for "operating cost":**
- If the pool fee is all-inclusive, which is likely but unverified, the number of operations changes **capacity, latency and outage exposure**, not the invoice. The invoice is driven by the number of pools: production, plus a Canadian recovery HSM (PB-10).
- If operations are billed, cost = operations ÷ 10,000 × rate. The volumes below give the operations.
- **PERF-01 must confirm both.**

## 3. Workload assumptions (planned capacity, PB-09)

| Symbol | Value | Basis |
|---|---|---|
| N | 2.5 million index entries | 25 organizations × 100,000 records, one entry per record |
| c | **2 commits/s** platform average | **Assumption** (PB-09 sets latency, not a write rate) |
| q | 5 result pages/s, of which 2/s are first pages | **Illustration**: 10% of the 50 requests/s envelope |
| P | 25 records per page | One of the allowed page sizes 10, 25, 50, 100 |
| F | 500 indexed (organization, form) pairs | **Assumption**: 20 indexed forms per organization |
| Month / year | 2,592,000 s / 31,536,000 s | 30 days / 365 days |

## 4. Options

1. **Per-record HMAC seal (current §18.1.4).** HMAC seal per entry in the HSM: signed at commit, verified remotely per displayed record and by the S-B sweep.
2. **HSM-authenticated Merkle roots.**
   - One Merkle tree per (organization, form) over the entries: 1,024 record-id buckets, each a small Merkle v1-style tree, under a top tree.
   - The root is HMAC-signed in the HSM at each commit.
   - Membership is verified locally; one remote verify of the root per query.
3. **HSM-authenticated batch or organization checkpoints.** As option 2, but the root or checkpoint is signed periodically (for example every 10 s per organization) instead of per commit.
4. **Local verification with managed keys:**
   - **4a.** HMAC key unwrapped from the HSM into Authority memory;
   - **4b.** **ES256 (ECDSA P-256) seal per entry**, signed in the HSM with a non-exportable key, verified **locally** with the public key.
5. **Hybrid.** Option 2's tree with ES256-signed roots (as 4b), local verification, signed checkpoints anchored externally, and independent reconciliation.

## 5. Remote HSM operations (estimates)

| | 1 Per-record HMAC | 2 HMAC Merkle roots | 3 HMAC checkpoints | 4a Local HMAC | **4b ES256 seals, local verify** | 5 Hybrid |
|---|---|---|---|---|---|---|
| Per commit | 1 sign | 1 sign | 0 (batched) | 0 | **1 sign** | 1 sign |
| Per query (first page) | P verify | 1 verify | 1 verify | 0 | **0** | 0 |
| Per further page | P verify | 0 (root verified for the snapshot) | 0 | 0 | **0** | 0 |
| S-B sweep | N per 16 h (**≈ 43/s**) | F roots per pass (≈ 0.14/s hourly) | ≈ 0.14/s | 0 | **0** | 0 |
| Startup, rotation | — | — | — | unwrap per key | public-key fetch per key (Get Key: 1,100/s) | same |
| **Total at §3 assumptions** | ≈ 2 + 125 + 43 = **≈ 170/s** | ≈ 2 + 2 + 0.14 = **≈ 4.1/s** | ≈ 2.5 + 2 + 0.14 = **≈ 4.6/s** | **≈ 0** | **≈ 2/s** | **≈ 2/s** |
| Per month | ≈ 442 million | ≈ 10.7 million | ≈ 12 million | ≈ 0 | **≈ 5.2 million** | ≈ 5.2 million |
| Per year | ≈ 5.4 billion | ≈ 131 million | ≈ 145 million | ≈ 0 | **≈ 63 million** | ≈ 63 million |
| Sweep alone, per month | ≈ 112 million | ≈ 0.37 million | ≈ 0.37 million | 0 | **0** | 0 |
| Reduction of total vs option 1 | — | ≈ 97.6% | ≈ 97.3% | ≈ 100% | **≈ 98.8%** | ≈ 98.8% |
| Reduction of the 43/s sweep | — | ≈ 99.7% | ≈ 99.7% | 100% | **100%** | 100% |

Notes:
- At P = 25, **page verification (≈ 125/s), not the sweep, is the largest search load in option 1**.
- In option 4b, commit signing at 2/s is under 1% of the documented one-partition P-256 sign capacity (330/s). Remote HMAC capacity for option 1 is unknown (unpublished).
- **Outside this decision, but larger:** the existing row-integrity workload. A commit verifies every staged item through the MAC provider, and every record open verifies every item. That probably exceeds search, and PERF-01 must measure it.
- **S-C** (deep comparison with BusinessDb) needs **no HSM in any option**. Comparing SHA-256 of the business bytes with the Authority-held leaf digest and recomputing the root detects business tampering (AD-B) on CPU, so the earlier "HSM unavailable: S-C paused" dependency can be removed.

## 6. Security comparison

Adversaries as in design §18.1.2: AD-B business DBA, AD-1 Authority database credential, AD-2 Authority process, AD-3 AuthorityDb administrator.

| Property | 1 Per-record HMAC | 2/3 HMAC roots or checkpoints | 4a Local HMAC | **4b ES256 seals** | 5 Hybrid |
|---|---|---|---|---|---|
| Displayed result forged by AD-1/AD-3 (identity, field, type, classification, value, approval at derivation) | Refused | Refused (inclusion under the signed root); in 3, entries changed since the last checkpoint are **unprotected** until signed | Refused, unless the key has leaked | **Refused** | Refused |
| Result count includes a forged match (AD-1) | Not bound | **Bound** (all matches checked against the root, on CPU) | Bound if all checked | **Bound if all matches are verified locally** (≈ 10,000 ECDSA verifications at most; CPU cost to measure) | Bound |
| Entry deleted (AD-1), per query | I-1 only (unkeyed) | **Detected** within the trees checked | I-1 | I-1 per query; sweep | Detected |
| Posting omitted so a match disappears (AD-1) | Sweep (≤ 24 h) | Per query **only if** every in-scope (record, field) leaf is checked (cost ∝ scope × conditions × tree depth); otherwise sweep | Sweep | Sweep; **can run hourly at no HSM cost** | As option 2 |
| Rollback to an older authentic state (AD-1/AD-3) | **Not detected per query** | **Not detected**: an older signed root is still authentic | Not detected | **Not detected** | **Detected within the anchor interval**, if the checkpoint is anchored externally (§7) |
| Current approval and authorization (AD-1 forging edges or history, AP-2/AP-3) | Not covered by search; needs R6 | Same | Same | Same | Same |
| AD-2 (Authority process) | Can sign while compromised | Same | **Can exfiltrate the key and forge indefinitely** | Can sign while compromised; **cannot exfiltrate** (non-exportable) | Same as 4b |
| Predicate integrity | In-process re-evaluation on the verified value | Same | Same | Same | Same |
| HSM outage | **Searches and sweeps cannot verify**: pages fail; S-B Inconclusive → suspension (8 h slack) | Queries fail (root verify); sweep needs few operations | Unaffected | **Verification unaffected**; only commits stop (they already need the HSM) | As 4b |
| Key rotation | Re-sign 2.5 M entries, or keep verifying with old key versions in the HSM | Re-sign F roots | Re-wrap the key | Keep old **public** keys for verification; re-sign gradually or never | Re-sign F roots |
| Disaster recovery | Verification depends on HSM recovery (Canadian manual path, PB-10) | Same | Wrapped key depends on the HSM | **Historic verification needs only retained public keys** | As 4b |
| Independent reconciliation (R5) | Needs HSM verify rights (a second identity on the HSM) | Same | Needs the secret | **Needs no secret**: public key only | Same |
| New cryptography to review | New message schema (HMAC) | **New authenticated data structure** (bucketed tree, incremental updates) | Key export | New message schema; **asymmetric signature (ES256) added** to the integrity design; public-key pinning | Option 2 + 4b + checkpoint protocol |
| Concurrent search and sweep load | Contend on the HSM with commits and the row-integrity workload | Little HSM; CPU for tree updates under the organization lock | CPU | **No HSM contention**; CPU only | Same |
| Policy | Fits `CLAUDE.md` §4P | Fits | **Conflicts with §4L.3**: master-wrapped keys are allowed only where KMS-native HMAC is unavailable, and it is available | Needs an owner/ADR decision: the seal is not a RowSignature, and §4L/§4P name HMAC for row signatures | As 4b |

## 7. External trust anchors

- **No signature or root proves query completeness or freshness on its own.** A signed root proves that *some* accepted state existed. It does not prove that state is the latest, nor that the query read all of it.
- **Authenticity anchor (options 4b and 5):** the public verification key must be authentic. It is fetched from Managed HSM by key identifier (Get Key), using the Authority's identity, and checked against a key identifier and thumbprint **pinned in deployment configuration** (PB-11 signed release). It is **never** read from AuthorityDb: a writer there could substitute their own key. This is new, and it is mandatory.
- **Freshness anchor (all options):**
  1. A per-organization checkpoint (organization, content-integrity revision, a digest over the current entry seals or tree roots) is signed with the same non-exportable key.
  2. It is appended to the external security-event archive, which refuses a different body for an existing revision, and, in production, to Azure Confidential Ledger.
  3. Independent reconciliation (R5) compares current AuthorityDb state with the **latest anchored** checkpoint, so rollback is detected within the checkpoint plus reconciliation interval.

  Signing is about one operation per organization per interval: 25 organizations every 5 minutes ≈ 0.08/s.
- **Limit:** this does not stop AD-2, which can sign new checkpoints. It also does not fix the unkeyed security journal (AP-3, remediation R6). The same signed-checkpoint mechanism is a natural basis for R6 but needs its own design.

## 8. Recommendation

**Option 4b (ES256 per-entry seals with local verification), plus signed and externally anchored checkpoints for freshness, delivered with R5 reconciliation.**

It has the lowest remote-operation count that keeps the approved guarantees: ≈ 2 operations/s, all commit signing, against ≈ 170/s for option 1 at the same assumptions. It avoids a new authenticated data structure.

| Versus approved §18.1.4 | Guarantee |
|---|---|
| Binding of identity, version, field, type, classification, value and approval-at-derivation for every displayed record, against AD-B, AD-1 and AD-3 | **Equivalent** |
| In-process predicate re-evaluation; nothing per query against AD-2; current approval and authorization not bound per query | **Equivalent** |
| Counts: every match can be verified (CPU) | **Stronger** (§18.1.4 left counts unbound) |
| Omission detection: S-B can run hourly without HSM cost; the 24 h deadline stays the enforced bound | **Stronger in practice** (same enforced bound) |
| Availability: an HSM outage no longer fails searches or suspends the index; only commits stop, as today | **Stronger** |
| AD-2 key exfiltration: non-exportable private key | **Equivalent** (HMAC in the HSM is also non-exportable) |
| Independent reconciliation without secret access; historic verification survives HSM loss | **Stronger** |
| **New dependency:** public-key authenticity (pinning) | **New risk**, mitigated by §7; a pinning mistake would be catastrophic, so it needs an architecture test |
| ES256 sign latency at commit (remote) | **Unmeasured**; may differ from HMAC |
| Asymmetric cryptography added to the integrity design | **More review scope**: a new provider port, a new key type, randomized signatures (vectors are verification vectors from a fixed test key) |

**Option 5** adds per-query deletion and omission detection and binds counts with less CPU. It needs a new authenticated tree with incremental updates and its own cryptographic review. **Defer it** as a later, separately approved improvement.

**Option 2** is the best choice if the owner wants to stay with symmetric HMAC only: about 4 operations/s, but a new tree structure. **Option 3** gains nothing at the planned commit rate and adds an unprotected window. **Option 4a** is rejected (policy and AD-2 key exfiltration).

## 9. Changes required if 4b is chosen

| Item | Change |
|---|---|
| **OR-1** | Stays open. The binding contract is replaced by the ES256 version below; acceptance still needed |
| **§18.1.4** | Seal `DynamicForms/SearchEntrySeal/v1` keeps its message schema; algorithm **ES256**. Key purpose `SearchEntrySealV1` becomes an **EC-HSM P-256** key, non-exportable, with sign rights for the Authority signing identity only. Verification is local, with the pinned public key (§7). Optionally all matches are verified (counts bound) |
| §17.10 | S-B no longer has "MAC provider unavailable" as a cause of Inconclusive; a pinning failure is Inconclusive. The 24 h deadline is unchanged; the scheduling target may tighten to hourly (owner decision). S-C becomes CPU-only |
| §18.1.5 | HSM table replaced by §5 of this document |
| **IX-1** | Implement through a **new signature-provider port** beside `IIntegrityMacProvider`: local ECDSA for tests; Vault Transit `ecdsa-p256` for Docker (capability to confirm at IX-1); Managed HSM `EC-HSM` P-256 ES256 in production. Plus: the seal sign at commit; public-key pinning with an architecture test ("no verification key read from AuthorityDb"); message-encoding vectors and fixed-key verification vectors; commit p95 delta gate including the ES256 sign |
| Signed checkpoints and R5 | Not IX-1. Scheduled with the remediation proposal (R5/R6); needed before a real-data environment |
| **PERF-01** | Measure:<ul><li>confirmation of the pool price and of whether operations are billed;</li><li>ES256 sign latency p50/p95/p99 and throughput on Managed HSM (documented 330/s per partition);</li><li>commit p95 delta;</li><li>local ES256 verify throughput per core;</li><li>page latency per page size with and without all-match verification;</li><li>hourly S-B CPU load at N = 2.5 million;</li><li>public-key fetch and pinning at startup and rotation;</li><li>**the existing row-integrity HMAC workload** (commit and open), whose HSM throughput is unpublished</li></ul> |
| ADR | Using ES256 for search seals (not RowSignature) is a cryptographic-architecture decision needing owner approval and inclusion in the independent cryptographic review |

## 10. Decisions for the owner

1. Retain option 1 (HMAC per record), or replace it with **4b (recommended)**, or with 2 (symmetric only).
2. If 4b: accept public-key pinning (§7) as a mandatory control, and the new signature-provider port.
3. Whether all-match verification (binding counts) is required, or optional pending PERF-01.
4. Whether the S-B scheduling target tightens to hourly (the 24 h deadline is unchanged).
5. Whether signed external checkpoints are scheduled with R5/R6 before any real-data environment.

## Sources

- Azure Managed HSM scaling guidance: https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/scaling-guidance
- Managed HSM key types and algorithms: https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/about-keys-details
- Key Vault keys overview: https://learn.microsoft.com/en-us/azure/key-vault/keys/about-keys
- Key Vault sign REST API: https://learn.microsoft.com/en-us/rest/api/keyvault/keys/sign/sign
- Key Vault service limits: https://learn.microsoft.com/en-us/azure/key-vault/general/service-limits
- Azure Key Vault pricing (amounts not rendered when read): https://azure.microsoft.com/en-ca/pricing/details/key-vault/
- Third-party price listing (unverified): https://costbench.com/software/secrets-management/azure-key-vault/
