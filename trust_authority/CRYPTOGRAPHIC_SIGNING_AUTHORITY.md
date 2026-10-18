# Level 2 G04 — Cryptographic Signing Authority and Local Development Providers

> **Security simplification cross-reference:** For duplicate audit-writer consolidation, confidentiality defaults, trusted search completeness, high-assurance step-up and future HMAC/Merkle optimization, see `security_hardening/SECURITY_SIMPLIFICATION_STANDARD.md` (ADR-SEC-2026-10-16-01). This document remains normative for its own trust boundary and state machine; no prior anti-rollback/recovery requirement is weakened.


Status: **Normative architecture / implementation reference; not yet deployed or penetration-tested.**
Date: 2026-10-16
Applies to: .NET 10, dedicated Authorization Authority, signed rows, per-record Merkle manifests, authoritative content revisions, and independently anchored authorization state.

## 1. Decision

Create an `IIntegrityMacProvider` abstraction **inside the independently deployed Authorization Authority only**, and provide the following interchangeable adapters:

1. `LocalHmacProvider` — unit tests and offline development, using .NET `HMACSHA256` and ephemeral **development-only** keys, stored outside business PostgreSQL. Never permit in production.
2. `VaultTransitHmacProvider` — local Docker integration; HashiCorp Vault Transit `hmac`/`verify` APIs, versioned keys and HTTP failure simulation. Docker **dev mode is insecure, in-memory, and never production**.
3. `AzureManagedHsmProvider` — required production provider; Azure Managed HSM `oct-HSM` 256-bit key, `HS256` sign/verify, managed workload identity, independent HSM administration.
4. Optional later `SoftHsmPkcs11Provider` — specialized PKCS#11 interoperability testing. SoftHSM is a software crypto module, **not an Azure Managed HSM API emulator** and not a replacement for production HSM guarantees.

The business API continues to call `IAuthorizationAuthorityClient` / approved content commands; **it must not receive the raw `IIntegrityMacProvider` interface, keys, HSM credentials, or a generic sign-payload endpoint**.

## 2. Trust boundary and processing flow

`Caller -> authenticated business API -> authenticated independent Authorization Authority -> authoritative state + permissions check -> canonical row/manifest payload -> internal MAC provider -> staged signed content -> commit CAS -> accepted Merkle root/ledger anchor`.

The authority checks verified actor and workload identity, tenant binding, object identity, current authority revision, full current permission/delegation scope, mandatory encryption policy, previous accepted content root, expected revision, idempotency, and audit operation ID **before calling the provider**. A provider only authenticates *bytes under a managed key*; it does not itself approve ownership, sharing, or record changes.

Verification is also gated through a trusted read/access flow. `VerifyAsync` may be used internally to check raw HMAC validity, but valid HMAC alone never authorizes access, prevents replay, or establishes membership completeness. Accepted manifest root, content head, and authorization revision are checked independently.

## 3. Internal .NET contract (provider boundary)

```csharp
public sealed record IntegrityKeyReference(
    Guid OrganizationId,
    string KeyPurpose,           // e.g. "row-integrity-v1"; allowlisted
    string KeyVersion);         // opaque provider-native pinned version

public sealed record IntegrityMacResult(
    byte[] Mac,                 // exactly 32 bytes for HMAC-SHA-256
    string KeyVersion,
    string Algorithm);         // "HMAC-SHA-256"

public interface IIntegrityMacProvider
{
    Task<IntegrityMacResult> SignAsync(
        IntegrityKeyReference keyReference,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken);

    Task<bool> VerifyAsync(
        IntegrityKeyReference keyReference,
        ReadOnlyMemory<byte> canonicalPayload,
        ReadOnlyMemory<byte> expectedMac,
        CancellationToken cancellationToken);
}
```

`IIntegrityKeyCatalog` is separate and authority-admin-only: register tenant key, get approved current signing key reference, enumerate historical **verification-only** versions, request rotation, pause/disable use after incident. External tenant/user input cannot choose arbitrary key IDs or key versions. Use a unique key-purpose namespace (row, content manifest, security checkpoint, audit) and organization ID in canonical signed bytes; never reuse field-encryption DEKs, challenge secrets, JWT keys, etc. The provider-level MAC format must match across implementations when supplied with identical key material and canonical bytes.

**No direct provider calls from API controllers, background jobs, web forms, search handlers, or client SDKs.** The internal signing coordinator holds both `IIntegrityMacProvider` and the authoritative `ISecurityCommandValidator` / `ICommittedRevisionStore` dependencies.

## 4. Provider selection and deployment

| Environment | Provider | Allowed | Additional conditions |
|---|---|---|---|
| Unit tests | LocalHmac | yes | test-generated isolated keys; deterministic test fixtures |
| Local offline | LocalHmac | yes | keys stored outside business DB; ephemeral data may be discarded |
| Local Docker | VaultTransit | yes | loopback-only port, dev token, in-memory data, isolated machine |
| CI | VaultTransit or test mock | yes | ephemeral per job, no production credentials |
| Azure staging | AzureManagedHsm | required for release evidence | independent authority identity/admin; separate staging HSM and keys |
| Production | AzureManagedHsm | only permitted provider | never local or Vault dev adapter |

Configuration in the **authority deployment**, not in `form_definitions`, organization settings, business SQL, or remotely modifiable feature flags:

```json
{
  "IntegritySigning": {
    "Provider": "VaultTransit",
    "RequiredAlgorithm": "HMAC-SHA-256",
    "VaultTransit": {
      "BaseUrl": "http://127.0.0.1:8200"
    }
  }
}
```

Retrieve Vault tokens from development secret storage/environment (never checked-in JSON); Azure uses managed workload identity. Fail service startup in production if selected provider is not AzureManagedHsm. Deny runtime switching or fallback on errors. HSM/unavailable => fail closed. Verify before handing out plaintext on all protected paths.

## 5. Key provisioning and lifecycle

- Generate random organization-scoped signing keys inside the production HSM (non-exportable). Do not derive keys by hashing organization UUIDs or predictable values.
- Provisioning/rotation restricted to separately authorized **Authority security operators**, with step-up authentication, two-person approval where policy requires, independent audit event and revision checkpoint. Organization Owners may request a change, not export or replace keys directly.
- Pin a specific key **version** to every signature. New writes use the authority-approved signing version. Historical accepted bytes verify using the recorded previous version until historical retention/re-signing is completed.
- Never auto-resign rows merely because verification failed; rotate/resign only against independently verified accepted heads and manifests.
- Provision and verify cross-environment cryptographic test vectors (same known test key -> same 32-byte MAC) and rotate old/new keys; live dev Vault keys and production HSM keys must not be equal.
- Secure backup/restore of HSM key versions and authority key catalog, separate from business PostgreSQL. Reconcile independently anchored latest checkpoints after disaster recovery.
- Restrict signer to approved key purposes and **HSM Sign/Verify** (not create/delete/purge/export or decrypt). An independent provisioning identity owns key management. Monitor sign volume and per-tenant quotas.

## 6. HMAC input format

Do **not** hash ordinary `ToString()`, unordered dictionaries, or culture-sensitive JSON. Follow existing Merkle v1 canonical binary format and bind each signed message to domain separation (`row|manifest|security`), organization ID, immutable entity IDs, key purpose, schema version, content revision, operation ID, explicit null markers, deterministic ordered typed fields, and a complete membership commitment. HMAC exactly the normative message bytes; never double-hash implicitly when switching providers. Verify MAC using constant-time comparison where computed locally. Invalid length, missing key, unknown algorithm/version, mismatched revision, or provider failure => reject and alert.

## 7. Vault Transit local integration

Official container `hashicorp/vault` in `-dev` mode with a **development-only root token**, memory-only state. Use `transit/keys/<trusted-name>` with `type=hmac`; sign via `POST /v1/transit/hmac/<key>/sha2-256` with `input` = base64(canonical bytes), `key_version` = pinned positive integer; verify via `POST /v1/transit/verify/<key>/sha2-256` with `input` and `hmac`. Vault's `vault:vN:<base64>` token format contains its own key version; the adapter must parse and normalize it to the contract's `{Mac: raw bytes, KeyVersion: N}` and reconstruct the versioned token for verify. **Do not** treat Vault's serialized HMAC string as equivalent to an Azure `byte[]` tag. Use pinned named keys mapped **internally** from organization + purpose; never insert user-supplied key-name strings into URLs.

Dev quick-start files: `docker-compose.signing-dev.yml`, `bootstrap-vault-signing-dev.sh`, and `INTEGRITY_MAC_PROVIDERS.reference.cs` (uncompiled reference providers). The bootstrap creates a *single sample organization key* for demonstration; real tenant onboarding calls an authorized **authority-only key provisioning workflow**. Vault dev mode restart removes keys and invalidates existing signatures; dev datasets must be reset/re-seeded. If testing persistence is needed, deploy a non-dev Vault with secured storage and TLS in a **non-production** environment, but still do not claim HSM equivalence.

Sample test organization UUID: `11111111-1111-4111-8111-111111111111`, key purpose `row-integrity-v1`, key name `org-11111111111141118111111111111111-row-integrity-v1`.

## 8. Azure production adapter

`AzureManagedHsmProvider` uses the Azure Key Vault Keys Cryptography SDK with the Managed HSM key's **versioned identifier**, `HS256`, and an authority-managed identity. The .NET provider must ensure Azure SDK `SignData` vs `Sign` semantics are tested with canonical **raw message bytes**: no accidental caller-side hash/double-hash, no inconsistent base64 encoding, and no silent algorithm downgrade. `oct-HSM` symmetric keys cannot expose key material. Identity must only have limited cryptographic sign/verify permissions for required keys and must not have permission to alter policies or administer key material. Validate functionality, latency, throttling and failures in Azure staging; **Vault Docker is not an Azure SDK API emulator**.

Production key policy should be independently administered and governed by configuration-as-code, private network where supported, restricted egress, Entra workload identities, alerting on key operations, and separate deployment pipelines. Do not grant business API any HSM or authority-store role.

## 9. Performance and scale

Thousands of EAV leaves must not cause thousands of independent HSM round trips on each list-page read. Reuse canonical SHA-256 leaf digests in the accepted manifest, verify a trusted single HMAC over the committed root/head for batched records where that cryptographically proves the requested payload consistency, and use batched HMAC operations where available for requirements calling for individual row tags. The verifier must still check complete authoritative membership and accepted revisions; batching must **not** skip security checks. Benchmark local vs Vault vs Azure staging; set explicit payload sizes, rate limits, concurrency ceilings and fail-closed budgets.

## 10. Required tests and release gates

SIG-01 Provider `LocalHmac` and Vault generate/verify HMAC-SHA-256 for canonical payload using respective key versions.
SIG-02 Wrong bytes, entity, tenant, purpose, revision, key version, MAC length, or algorithm fail verification.
SIG-03 Version-pinned old signatures remain verifiable after rotation; only approved current version signs new rows.
SIG-04 API user cannot call raw `SignAsync`, register keys, choose a key version, or create arbitrary authorized security changes.
SIG-05 Compromised business PostgreSQL writer cannot change owner/team/shared field GUIDs and request a valid MAC from authority.
SIG-06 A valid old HMAC cannot override the externally accepted content revision/Merkle root.
SIG-07 Authority failure, Vault outage, HSM outage, HSM permission denial, timeout and rate limit fail closed, never fallback to local keys.
SIG-08 Production configuration rejects LocalHmac/VaultTransit at startup and forbids secret-bearing fields in application log output.
SIG-09 Organization A cannot use B's key reference; key naming and purpose separation enforced by authority catalog.
SIG-10 Docker restart destroys Vault dev keys and dev data is reset; production cannot use Vault dev root token.
SIG-11 Canonical test vectors match across development provider and Managed HSM in staging with deliberately imported matching **test-only** keys if supported by security policy; production key material is never exported.
SIG-12 Signing and verification scale benchmarks respect latency limits without eliminating full manifest, policy, or revision checks.
SIG-13 Tampered/deleted/replayed records trigger independent incident audit without blindly re-signing.
SIG-14 Dual control, key lifecycle auditing and key recovery drills verified in staging.

**Level 2 release gate:** Only close G04 when Azure Managed HSM adapter, workload identities, isolated signer, anti-oracle command validation, independent checkpoint integration and the above adversarial/failure tests have been deployed and independently reviewed. Unit/Docker tests alone do NOT prove Level 2 isolation.


## 11. Getting started locally (manual integration steps)

```bash
# From trust_authority/:
docker compose -f docker-compose.signing-dev.yml up -d
# Requires Bash and curl (Git Bash or WSL on Windows).
./bootstrap-vault-signing-dev.sh
# Vault listens only at http://127.0.0.1:8200
# Token for THIS throwaway dev instance: dev-only-token
```

Configure only the dedicated local authority process with `IntegritySigning:Provider=VaultTransit`, `VAULT_ADDR=http://127.0.0.1:8200`, and `VAULT_TOKEN=dev-only-token` in local secrets. Use sample org `11111111-1111-4111-8111-111111111111`, purpose `row-integrity-v1`, version `1`. Do **not** configure the business API with Vault credentials.

`INTEGRITY_MAC_PROVIDERS.reference.cs` includes development-only local and Vault adapter samples; adjust DI, authorization coordinator and test coverage before shipping. An Azure HSM adapter is specified but **not yet implemented**. The reference code has not been compiled in this environment.


## G14 coordinated cryptographic disaster recovery

Production key availability must be proven for *every historic key version* referenced by current accepted manifests and retained history. Full Azure Managed HSM restore is destructive and requires the same Security Domain; protect quorum-held recovery material and test restoration to an isolated replacement HSM. Never use the local Vault Transit development key as a production recovery substitute. A missing key means quarantine, not plaintext fallback or blind re-signing. See `COORDINATED_DISASTER_RECOVERY.md` and DR-17 through DR-21.
