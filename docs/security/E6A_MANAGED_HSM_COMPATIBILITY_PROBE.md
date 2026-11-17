# E-6a Managed HSM HMAC Compatibility Probe Preparation (Development/Testing)

**Scope:** E-6a preparation only. It builds on the accepted E-0, E-1, E-2, E-4 and E-5 work. Owner-authorized; owner-led review. This is not an independent review.

- E-6 provider implementation is blocked pending this probe.
- E-3 is deferred.
- E-6b, E-7 and later are not started.

**Unchanged:** Authority signing or verification code, HMAC providers, integrity schema, evidence formats and database. No Azure resource was created or contacted, and no real authentication was performed.

**The question:** can Azure Managed HSM compute HMAC-SHA-256 directly over EnterpriseForms' raw canonical bytes? It must produce the same 32-byte MAC as `LocalHmacProvider` and `VaultTransitHmacProvider`, which compute `HMAC-SHA-256(key, canonical bytes)`. This report does **not** answer that question. It prepares the experiment that will.

Each finding is labelled:

- **[DOC]**: confirmed by Microsoft documentation or SDK source.
- **[LOCAL]**: confirmed by local tests in this repository.
- **[REAL-HSM]**: requires validation against a real Managed HSM.

## 1. Microsoft API capabilities (E6a-1)

Sources were read on 2026-11-12. They are listed at the end of this report.

| # | Capability | Finding | Label |
|---|---|---|---|
| 1 | `oct-HSM` symmetric keys | Managed HSM supports `oct-HSM` keys of 128, 192 and 256 bits. Ordinary Key Vault does not support symmetric keys. | [DOC] |
| 2 | HS256 sign/verify | The key-types table lists HS256, HS384 and HS512 under sign/verify for `oct` keys. `SignatureAlgorithm.HS256` has been in the .NET SDK since `Azure.Security.KeyVault.Keys` 4.8.0 and is present in 4.10.2 (used here). | [DOC]; behaviour [REAL-HSM] |
| 3 | `sign` accepts arbitrary raw input | **Not documented.** REST Sign: "Creates a signature from a digest"; `value` is "The value to operate on". Concept page: Sign/Verify is "sign hash" or "verify hash", because "the service doesn't hash content as part of signature creation". This statement is not HMAC-specific. | **Unresolved** → [REAL-HSM] Experiment B |
| 4 | Input must be a SHA-256 digest | Implied, not stated, for HS256. The SDK treats HS256 like the digest algorithms: `SignAsync` signs "the pre-hashed digest", and `SignDataAsync` hashes with SHA-256 locally (`GetHashAlgorithm()` maps HS256 to SHA-256). Whether the service *enforces* a 32-byte input is undocumented. | SDK request shape [DOC]+[LOCAL]; enforcement [REAL-HSM] |
| 5 | Input length limits | None documented for `value`. | **Unresolved** → [REAL-HSM] Experiment B (0 B to 64 KB) |
| 6 | Importing a known test key | The only documented `oct-HSM` import is BYOK. The target key must be wrapped by a supported vendor HSM's BYOK tool (Cryptomathic, Entrust, Fortanix, IBM, Marvell, Securosys, StorMagic, Thales, Utimaco). The page says the method "is available only for supported HSMs". Plaintext JWK import (`k`) of `oct-HSM` into Managed HSM is not documented: the generic Import Key REST page is a Key Vault reference, and the Managed HSM pages list PEM import for RSA and EC only. | BYOK [DOC]; plaintext import **unresolved** → [REAL-HSM] Experiment I |
| 7 | Key sizes and algorithm match | The table pairs `oct` 128/192/256 with HS256/384/512, and says "the sign and verify algorithm must match the key type and size. Otherwise, the service returns a key-size-incorrect error". No explicit HS256-to-256-bit rule. EnterpriseForms uses 32-byte keys, so a 256-bit key is assumed. | Assumption → [REAL-HSM] (key inspection records `key_size`) |
| 8 | Explicit opaque version selection | `key-version` is a required path segment of sign and verify. Examples are 32 hex characters, but no format rule is documented. Get Key with no version returns the latest; this is documented for Get only. Unversioned sign behaviour is undocumented, and the probe can never send such a request. Limits: 100 versions per key, 5,000 keys per HSM. | [DOC]; behaviour [REAL-HSM] Experiment V |
| 9 | Data-plane permissions | Managed HSM local RBAC, separate from the control plane: "Granting control plane access … does *not* grant … data plane access". **Managed HSM Crypto User** is the only built-in role with `/keys/sign/action` and `/keys/verify/action`, and it *also* allows create, import, rotate, delete, encrypt, decrypt, wrap and unwrap. **No built-in role is limited to sign/verify.** Scope is `/`, `/keys` or `/keys/<name>`. Crypto Officer can self-assign Administrator. | [DOC] |
| 10 | Authentication | Microsoft Entra ID for both planes; data plane at `https://<name>.managedhsm.azure.net`. The token scope `https://managedhsm.azure.net/.default` was **not found in the pages read**. The existing IX-7 seal signer already uses it. The SDK derives its scope from the 401 `WWW-Authenticate` challenge; locally confirmed against a fake challenge only. The probe records the real challenge's `resource`. | Entra [DOC]; scope [REAL-HSM] |

API versions:

- The REST pages show `api-version=2025-07-01`.
- SDK 4.10.2 exposes `ServiceVersion.V7_6` and `V2025_07_01`; 2025-07-01 has been the default since 4.9.0.
- The probe accepts only `7.6` and `2025-07-01`, and records the one it used.
- The minimum API version for HS256 is undocumented.

Other documented facts relevant to E-7:

- Managed HSM "does not throttle on cryptographic operation transactions". Published benchmarks do **not** include HMAC sign.
- Microsoft recommends waiting about six minutes after creating or updating a key before using it, because of replication.
- Verify works outside a key's `nbf`/`exp` window; sign does not.
- FIPS 140-3 Level 3 validated HSMs.

## 2. Unresolved API assumptions (each needs real-HSM evidence)

| ID | Assumption | Resolved by |
|---|---|---|
| U-1 | HS256 `sign` computes HMAC-SHA-256(key, `value`) over the bytes exactly as sent | Experiment B (known key) |
| U-2 | HS256 accepts non-32-byte `value`, including 0 bytes and up to at least the largest Authority canonical payload | Experiment B per length |
| U-3 | HS256 `verify` recomputes over `digest` exactly as sent | B-verify / A-verify checks |
| U-4 | Plaintext JWK import of an `oct-HSM` key is accepted | Experiment I |
| U-5 | Importing under an existing name creates a new version | Experiment I (two imports) |
| U-6 | A 256-bit `oct-HSM` key is the right size for HS256 | Key inspection plus a successful HS256 sign |
| U-7 | Every sign/verify uses exactly the version named in the URL | Experiment V (two known keys) |
| U-8 | A nonexistent version is refused (404 or 400), never mapped to latest | V-nonexistent |
| U-9 | Token scope is `https://managedhsm.azure.net/.default` | Successful REST calls; recorded challenge resource |
| U-10 | The returned `kid` ends with `/keys/<name>/<version>` | Every response (enforced by the probe) |

## 3. Test harness location (E6a-2)

`tools/experiments/e6a-managed-hsm-probe/` is self-contained:

- It is not in `EnterpriseForms.All.slnx` (CI builds only the solution).
- It references no EnterpriseForms project and is referenced by none.
- It pins its own packages (`Azure.Security.KeyVault.Keys` 4.10.2, `Azure.Identity` 1.21.0, `xunit.v3` 4.0.2). Central package management is off for these two projects only, so `Directory.Packages.props` is unchanged.
- It keeps the repository's warnings-as-errors and analyzers.

| Command | Network | Purpose |
|---|---|---|
| `local` (default; no arguments) | none | RFC 4231, vector and explicit-version self-checks |
| `generate-vectors` | none | Regenerate the reference vectors from the published RowHmac/v1 file |
| `validate-config` | none | Validate a configuration |
| `hsm-import-test-keys` | Azure | Experiment I (undocumented behaviour, opt-in) |
| `hsm-run` | Azure | Key inspection, Experiments A, B and V |

Design points:

- **REST path.** `ManagedHsmRestClient` sends exactly the given bytes as unpadded base64url. Every call is bounded by the timeout (1 to 60 s) and has no retries. Caller cancellation propagates and is not reported as a timeout.
- **SDK path.** The SDK is used for Experiment A only, through `CryptographyClient.SignAsync(HS256, digest)`, as an independent cross-check. The SDK cannot express raw input, so Experiment B runs through REST only and is labelled as an undocumented-behaviour experiment.
- **Verdicts.** Pass means the observed bytes equal a locally computed reference. Fail means a refusal, mismatch, or an answer for another key or version. Inconclusive covers authentication, authorization, throttling, 5xx, transport, timeouts, malformed answers, and any byte comparison against an HSM-generated (unknown) key. An experiment passes only if every check passes.

## 4. Local test results (E6a-9)

Run on 2026-11-12. Only the probe's own tests ran; no repository suites and no Stage B.

| Run | Result |
|---|---|
| `ManagedHsmProbe.Tests.exe` (fake data plane; no network) | **95 passed, 0 failed, 0 skipped** |
| `ManagedHsmProbe.exe` (local mode) | **OVERALL: Pass** (RFC 4231 ×6, 17 vectors, test keys, explicit-version guard) |
| `ManagedHsmProbe.exe validate-config --config probe.settings.template.json` | exit 2: template refused (`AllowAzureCalls`, placeholder endpoint and versions); nothing contacted |
| `ManagedHsmProbe.exe hsm-run` without `--allow-azure-calls` | exit 2; nothing contacted |
| `python -I …/vectors/verify_vectors.py` | 34 key/vector MAC pairs recomputed independently: 0 failures. A deliberately corrupted copy: 2 failures detected, exit 1 |

Coverage [LOCAL]:

- **Vectors:** the committed file equals the generator's output, and the embedded copy equals the committed file. Raw and digest MACs are recomputed with an independent RFC 2104 construction and always differ. Row vectors equal the published signatures and the recorded Vault Transit HMACs.
- **Configuration:** local-only default; `AllowAzureCalls` and the command-line flag are both required. Rejected: non-HTTPS endpoints, Key Vault hosts, look-alike hosts, ports, user info, paths and queries; non-`e6a-probe-*` key names (EnterpriseForms `org-<uuid>-<purpose>` names included); a missing credential or provenance; malformed, duplicate or missing version mappings; unknown JSON properties or secrets; out-of-range timeouts; unsupported API versions.
- **No network in local mode:** a tripwire client factory throws if asked for a client. It is never reached in `local`, `validate-config`, or any refused HSM command.
- **Missing version:** an unmapped integer version is refused before any call, with the message "never falls back to the latest version". A URL without an explicit version cannot be built.
- **Timeouts and cancellation:** a slow service gives `Timeout` (Inconclusive) within the bound. Caller cancellation throws, and the application exits 130 without writing a report.
- **Failure classification:** 401 (challenge `resource` captured), 403, 404, 429, 503 and 400 are each classified. A wrong `kid` is a Fail; a malformed body is Inconclusive; a credential failure sends no request. The access token never appears in the console or the report.
- **Hypothesis discrimination (fake semantics models):**
  - HMAC-over-value: everything passes.
  - Hash-then-HMAC: A and B fail, and B explains the hashing.
  - Digest-length-only: B passes at 32 bytes only and fails overall (no generalization).
  - 4 KB size limit: 64 KB fails, 4 KB passes.
  - HSM-generated key: no byte check can pass.
  - Hash-unless-32-bytes: detected even with an unknown key.
  - Dishonest "latest version" service echoing the requested `kid`: caught.
  - Nonexistent version: refused. Exportable key: fails inspection.
- **Import:** the request shape is `oct-HSM`, sign/verify, `Hsm=true`, `exportable=false`. The key bytes (hex or base64url) never reach the report. A refused import is a Fail with a BYOK note.
- **Observed SDK behaviour** (the real SDK 4.10.2 against the fake transport):
  - `SignAsync(HS256, digest)` sends the digest unchanged.
  - `SignDataAsync(HS256, data)` sends SHA-256(data), so its MAC is HMAC(key, SHA-256(data)), not the EnterpriseForms MAC.
  - The SDK answers the Bearer challenge with `https://managedhsm.azure.net/.default`.
  - A new `CryptographyClient` first **GETs the key** before signing, so the SDK path also needs key read permission.
  - With `MaxRetries=0`, a 429 is not retried.

The fake models behaviour hypotheses. **It is not evidence of how Managed HSM behaves.**

## 5. Known-answer vectors (E6a-3)

`vectors/e6a-reference-vectors.json`:

- Schema `EnterpriseForms/E6a/ManagedHsmHmacProbe/reference-vectors/v1`; 17 vectors × 2 test keys.
- Length vectors of 0, 1, 31, 32, 33, 64, 256, 1024, 4096 and 65536 bytes, with `input[i] = i mod 256`.
- Seven representative canonical payloads (257 to 369 bytes) copied from the published synthetic `trust_authority/row-integrity-v1.test-vectors.json`. They contain no real data.

Each vector records:

- the input length;
- SHA-256(input);
- `rawMessageHmacSha256Hex` = HMAC-SHA-256(key, input);
- `digestMessageHmacSha256Hex` = HMAC-SHA-256(key, SHA-256(input));
- `rawEqualsDigest` (false for all 34 pairs).

The two MACs are computed separately in C#. They are re-checked with an RFC 2104 construction and independently in Python.

Test keys (both published, test-only; neither equals or derives from any deployment key):

- `e6a-test-key-1` = the RowHmac/v1 test key `000102…1f`;
- `e6a-test-key-2` = `202122…3f`.

**Existing integrity-vector compatibility [LOCAL]:** under `e6a-test-key-1`, all seven row vectors' raw-message MACs equal the published `rowSignatureHexWithTestKey` values. They also equal the real Vault Transit HMACs recorded in Stage A (`docs/security/stage-a-vault-transit-vectors.json`). For example, `ROWV1-FORM-RECORD-USER-OWNED` gives `7c7a359e…f7`. A real HSM therefore matches existing evidence if and only if Experiment B produces the `rawMessage` values.

## 6. Key-import feasibility (E6a-5)

- **Documented route:** BYOK.
  - Create an RSA-HSM key-exchange key (2048, 3072 or 4096 bits, `--ops import`) in the HSM.
  - Export its public key to a **supported vendor HSM**.
  - The vendor BYOK tool wraps the `oct` target key as raw bytes: RSA-OAEP-SHA1 plus AES key wrap with padding, `CKM_RSA_AES_KEY_WRAP`.
  - Import with `az keyvault key import --hsm-name <hsm> --name <key> --byok-file <file>`.
  - Required role: Crypto User, which holds `/keys/import/action`.

  This needs vendor HSM hardware or a service, which is outside this harness and not authorized. Software-generated transfer blobs are neither documented as supported nor forbidden. The probe does not create them.
- **Undocumented route (Experiment I):** `PUT /keys/<name>` with `{"key":{"kty":"oct-HSM","k":…,"key_ops":["sign","verify"]},"Hsm":true,"attributes":{"enabled":true,"exportable":false}}`. It is opt-in (`AllowTestKeyImport`) and runs as its own command. Acceptance or refusal is recorded. After acceptance, the key is inspected: `oct-HSM`, non-exportable, no release policy.
- **If neither route is available:** use HSM-generated keys: `az keyvault key create --hsm-name <hsm> --name e6a-probe-hmac --kty oct-HSM --size 256 --ops sign verify`. The probe then reports byte compatibility as **Inconclusive** by design. It never substitutes an unknown key for the vectors. Experiment V still works, as does detection of a service that hashes non-32-byte inputs.
- **Lifecycle:** the keys exist only in the throwaway HSM under `e6a-probe-*` names, and are deleted with the HSM (section 8). The test keys are public: anything signed with them is forgeable. They are never used anywhere else.

## 7. Version mapping (E6a-6)

- **Opaque identifiers.** Managed HSM versions are service-generated identifiers, not integers. EnterpriseForms keeps the integer `CurrentSigningKeyVersion` (E-5) and the integer `KeyVersion` recorded in evidence, so a trusted integer-to-identifier mapping is required.
- **E-6a mapping.** For E-6a it is a **static, test-only** `KeyVersionIdentifiers` table in the probe configuration (`"1"` → id, `"2"` → id). An unmapped integer is refused before any call. No AuthorityDb or production mapping exists.
- **Experiment V checks:**
  - each mapped identifier exists (list versions; latest by `created`);
  - each explicit version signs with its own key, and its `kid` names that version;
  - two versions give different MACs, and a MAC does not verify under the other version;
  - an older version never yields the latest version's MAC (this catches a service that ignores the version but echoes the `kid`);
  - a random nonexistent identifier is refused.
- **Options for E-6/E-7** (production decisions, owner choice, not made here):
  1. per-organization deployment configuration (the IX-7 seal-signer precedent);
  2. HSM key tags written by the custody operator (mutable by anyone holding key-update rights);
  3. an AuthorityDb mapping column, which needs a migration.

  `InspectKeyAsync.LatestVersion` and E-5 activation also depend on this decision.

## 8. Azure resource prerequisites (E6a-8)

Nothing below has been done. All of it requires separate owner authorization.

- **Subscription.** A non-production subscription and resource group, where the operator may create `Microsoft.KeyVault/managedHSMs`, with the `Microsoft.KeyVault` provider registered. Use a test Entra tenant or identity, never production credentials.
- **Region.** PB-10 prefers Canada Central, but Managed HSM availability there was **not confirmed** in the pages read. Check it with `az provider show --namespace Microsoft.KeyVault --query "resourceTypes[?resourceType=='managedHSMs'].locations"`. Canada East cannot be a multi-region *extension* region (not needed here). The probe uses only published synthetic data.
- **Provision.** `az keyvault create --hsm-name <name> --resource-group <rg> --location <region> --administrators <operator-object-id> --retention-days 7`. **Do not** pass `--enable-purge-protection`: with it "you pay for the entire retention period", and it cannot be disabled.
- **Activate.** The data plane is disabled until the security domain is downloaded. That needs at least three RSA key pairs (maximum 10) and a quorum, for example `az keyvault security-domain download --hsm-name <name> --sd-wrapping-keys k1.cer k2.cer k3.cer --sd-quorum 2 --security-domain-file <name>-SD.json`. Keep the security-domain file and private keys outside the repository, and destroy them after purge.
- **Roles.** The initial Managed HSM Administrator cannot perform key operations. Assign **Managed HSM Crypto User** to the probe operator at scope `/keys` (needed for import or create, sign, verify and read), for example `az keyvault role assignment create --hsm-name <name> --role "Managed HSM Crypto User" --assignee <object-id> --scope /keys`. Confirm the exact CLI syntax before use. This role is broader than production needs: a sign/verify-only production identity would need a custom role (E-7).
- **Network.** The firewall is off by default (Entra authentication still required). Restrict it to the operator's public IPv4 (up to 10 CIDR ranges; RFC 1918 not allowed), or use a private endpoint (group `managedhsm`, zone `privatelink.managedhsm.azure.net`). Virtual network service endpoints are not supported.
- **Cleanup.**
  1. Remove role assignments.
  2. `az keyvault delete --hsm-name <name>`.
  3. `az keyvault purge --hsm-name <name> --location <region>`. Soft-delete cannot be disabled, and **billing continues while soft-deleted until purged**.
  4. Delete the resource group.
  5. Destroy the security-domain material.

## 9. Pricing references (E6a-8)

- Official pricing: <https://azure.microsoft.com/en-us/pricing/details/key-vault/>, section "Managed HSM Pools". It lists "Hourly usage fee per HSM pool", tier Standard B1. The numeric price did not render in the fetched page (shown as `$-`), so **no price is stated here**.
- Pricing calculator: <https://azure.microsoft.com/en-us/pricing/calculator/>.
- Billing facts [DOC]:
  - charged per HSM pool per hour;
  - charges continue while soft-deleted until purged;
  - purge protection means paying for the whole retention period;
  - multi-region replication doubles billing (not needed).

Confirm before creation:

- the current hourly price for the chosen region and currency;
- any minimum billing increment;
- subscription credits or spending limits;
- the expected duration of provisioning, activation, the run and the purge, which together set the billed hours.

## 10. Exact future test procedure (requires separate owner authorization)

1. Owner authorizes the real-HSM run, subscription and region. Provision, activate and assign roles as in section 8. Wait about six minutes after key creation or import before use.
2. Copy `probe.settings.template.json` outside source control and set:
   - `HsmEndpoint`, `Credential` (`AzureCli`) and `AllowAzureCalls: true`;
   - `KeyProvenance: ImportedKnownTestKeys`;
   - `KnownTestKeyByVersion` = `{1: e6a-test-key-1, 2: e6a-test-key-2}`;
   - `AllowTestKeyImport: true`;
   - and remove the placeholder version table.
3. Sign in with `az login` against the test tenant. Run `ManagedHsmProbe local`, which must be Pass. Then run `ManagedHsmProbe validate-config --config <file> --import`.
4. Run `ManagedHsmProbe hsm-import-test-keys --config <file> --allow-azure-calls --report import.json`.
   - **Pass:** copy the reported version identifiers into `KeyVersionIdentifiers` and set `AllowTestKeyImport: false`.
   - **Fail:** either obtain authorization for BYOK, or create two `oct-HSM` 256-bit `sign verify` versions with `az keyvault key create`. Then set `KeyProvenance: HsmGenerated` and clear `KnownTestKeyByVersion`.
5. Run `ManagedHsmProbe hsm-run --config <file> --allow-azure-calls --report run.json`. Keep both reports. They contain only synthetic inputs, test MACs, version identifiers and error codes; no tokens or key material.
6. Interpret each `B-sign-<vector>` separately:

   | Result | Meaning |
   |---|---|
   | B Pass for every length (known keys) | Raw-input HMAC observed for 0 B to 64 KB and all row vectors. This is still an **undocumented** contract: get Microsoft confirmation, and keep a real-HSM regression check in E-7. |
   | B passes only at 32 bytes, or rejects other lengths | The HSM accepts digests only. Raw-input compatibility is impossible. |
   | B Fail "hashed the input first" | The HSM computes HMAC(key, SHA-256(input)). Compatibility would need a new, versioned MAC construction (owner decision). |
   | A Pass and B Fail | Only the documented digest path works. |
   | Any Inconclusive | Fix the infrastructure (role, network, scope, throttling) and rerun. No conclusion may be drawn. |
   | V Fail | Version semantics differ from E-5. Stop and report. |

7. Clean up as in section 8 the same day. Then owner-led review of the reports.

## 11. Security limitations

- **Test keys only.** Both keys are published; MACs made with them prove nothing about deployment keys. The probe never accepts EnterpriseForms key names and refuses non-Managed HSM hosts.
- **Plaintext key import.** Experiment I sends published test-key bytes to the HSM over TLS, which is exactly what production must never do. Production keys stay HSM-generated and non-exportable (CRYPTOGRAPHIC_SIGNING_AUTHORITY.md).
- **Broad test role.** Managed HSM Crypto User allows much more than sign/verify, and is acceptable only on a throwaway HSM.
- **Trust in Azure.** The probe trusts TLS and the Azure Identity credential. It pins no certificates.
- **No secrets stored.** The configuration holds no secret (authentication is `az login` or a managed identity). Secret-like JSON properties are rejected. Tokens and key bytes are never printed, though the report does include observed test MACs.
- **Fake ≠ HSM.** Fake-based tests prove harness logic only. Nothing here establishes real Managed HSM compatibility.
- **Small samples.** A pass at the tested lengths and inputs does not prove behaviour at other lengths. The probe reports per length and never generalizes.

## 11A. Azure findings (E-6b run, owner-led; experimental, NOT approved for production)

Recorded after the corrected probe was run against a temporary Azure Managed HSM, which has since been deleted and purged. The original E-6b evidence reports are unchanged and remain the primary record. This section is a summary only. All results are **[REAL-HSM]** and apply to the tested inputs only.

- Raw-input HS256 HMAC-SHA256 matched the existing EnterpriseForms providers (`LocalHmacProvider`, `VaultTransitHmacProvider`) for the tested supported inputs.
- Genuine signatures verified successfully.
- Tampered signatures were rejected.
- Cross-version verification correctly rejected signatures made under a different key version.
- Empty (zero-length) raw input failed with HTTP 400.
- A 65,536-byte raw input failed with HTTP 413.
- The maximum supported raw-input size remains **undetermined**.
- Raw-input signing is **not** established as a documented Azure API guarantee.
- Azure reports a failed verification as HTTP 400, code `BadParameter`, with the token `RET_SIGNATURE_VERIFICATION_FAILED` in the message. The probe recognizes this only for verify calls, with a strict token boundary. The same error on any other operation is still a failure. An earlier probe revision matched the token as the error code and so reported correct negatives as failures; that was fail-safe and is corrected (148 local tests pass).
- **E-6 is experimental and NOT approved for production.** No provider was implemented.

## 12. Risks for E-7 (or a later owner-authorized real-HSM step)

- **Contract stability.** Even if Experiment B passes, raw-input HS256 is undocumented, so its behaviour could change without notice. Production use would need Microsoft confirmation or a standing real-HSM regression gate.
- **Payload sizes.** RowHmac canonical bytes include typed field values and ciphertext envelopes, so their maximum size depends on field limits. Inventory the largest payload the Authority MACs and compare it with B's accepted lengths.
- **Version mapping.** The integer-to-opaque mapping and its trust source are an open owner decision (section 7).
- **Permissions.** There is no sign/verify-only built-in role, so production needs a custom role. The SDK path also needs key read.
- **Unmeasured operations.** HMAC throughput, latency, throttling and Canada Central availability have not been measured.
- **Recovery.** HSM security-domain and recovery procedures remain open (G14 / PB-10 manual Canadian recovery path).

## 13. Files changed

| File | Change |
|---|---|
| `tools/experiments/e6a-managed-hsm-probe/README.md` | new |
| `tools/experiments/e6a-managed-hsm-probe/probe.settings.template.json` | new (test-only template) |
| `tools/experiments/e6a-managed-hsm-probe/ManagedHsmProbe/` (`ManagedHsmProbe.csproj`, `Program.cs`, `ProbeApplication.cs`, `ProbeConfiguration.cs`, `ManagedHsmUrls.cs`, `ManagedHsmRestClient.cs`, `SdkDigestSigner.cs`, `HsmExperiments.cs`, `LocalSelfChecks.cs`, `ReferenceVectors.cs`, `ProbeReport.cs`, `Codec.cs`) | new |
| `tools/experiments/e6a-managed-hsm-probe/ManagedHsmProbe.Tests/` (`ManagedHsmProbe.Tests.csproj`, `FakeManagedHsm.cs`, `TestSetup.cs`, `ReferenceVectorTests.cs`, `ConfigurationTests.cs`, `ApplicationTests.cs`, `RestClientTests.cs`, `ExperimentTests.cs`, `SdkBehaviourTests.cs`) | new |
| `tools/experiments/e6a-managed-hsm-probe/vectors/e6a-reference-vectors.json`, `verify_vectors.py` | new |
| `ManagedHsmProbe.Tests/VerificationTests.cs` and corrected verification handling in `ManagedHsmRestClient.cs`, `HsmExperiments.cs`, `FakeManagedHsm.cs` | E-6b follow-up |
| `docs/security/E6A_MANAGED_HSM_COMPATIBILITY_PROBE.md` | new (this report), findings section 11A added |
| `spec-files.json`, `SHA256SUMS.txt` | regenerated manifest entries for this report |

The local commit hash is in `git log` on branch `e6/managed-hsm-hmac-provider` (on `cdade17`).

## Sources

- Managed HSM key types, algorithms and operations: <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/about-keys-details>
- About Managed HSM keys: <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/about-keys>
- REST Sign / Verify / Import / Get Key / Get Key Versions: <https://learn.microsoft.com/en-us/rest/api/keyvault/keys/sign/sign>, <https://learn.microsoft.com/en-us/rest/api/keyvault/keys/verify/verify>, <https://learn.microsoft.com/en-us/rest/api/keyvault/keys/import-key/import-key>, <https://learn.microsoft.com/en-us/rest/api/keyvault/keys/get-key/get-key>, <https://learn.microsoft.com/en-us/rest/api/keyvault/keys/get-key-versions/get-key-versions>
- BYOK for Managed HSM and BYOK specification: <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/hsm-protected-keys-byok>, <https://learn.microsoft.com/en-us/azure/key-vault/keys/byok-specification>
- Local RBAC built-in roles and access control: <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/built-in-roles>, <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/access-control>
- Quickstart (CLI), security domain, recovery: <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/quick-create-cli>, <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/security-domain>, <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/recovery>
- Network security and private link: <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/network-security>, <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/private-link>
- Overview, service limits, scaling guidance, multi-region replication: <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/overview>, <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/service-limits>, <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/scaling-guidance>, <https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/multi-region-replication>
- Azure SDK for .NET: `SignatureAlgorithm.cs`, `CryptographyClient.cs`, `CHANGELOG.md` under <https://github.com/Azure/azure-sdk-for-net/tree/main/sdk/keyvault/Azure.Security.KeyVault.Keys>; package XML documentation of `Azure.Security.KeyVault.Keys` 4.10.2
- Pricing: <https://azure.microsoft.com/en-us/pricing/details/key-vault/>, <https://azure.microsoft.com/en-us/pricing/calculator/>
