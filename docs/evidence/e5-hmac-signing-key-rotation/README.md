# E-5 evidence: controlled HMAC signing-key rotation

| File | Content |
|---|---|
| `e5-vault-hmac-integration-run.txt` | Output of `scripts/dev/e5-vault-hmac-integration.ps1`, run against a throwaway HashiCorp Vault 1.20.4 dev container. Random tokens are not recorded. See below for what the run does. |

The run:
1. Creates two organization HMAC keys the way key custody does.
2. Gives the Authority a token with the development stack's onboarding policy (read/hmac/verify on `org-*`).
3. Adds version 2 of the first key with `infrastructure/docker/vault/provision-integrity-key-version.sh` (not activated).
4. Shows five refusals of that script: a repeated run, a missing key, a non-UUID organization, an invalid expected version, and the Authority's own token (`403` on rotate).
5. Runs `IntegrityKeyActivationVaultIntegrationTests`: 4 tests passed.

The focused in-process tests (`IntegrityKeyActivationTests`, 11) and the full Authority unit suite are listed in `docs/security/E5_HMAC_SIGNING_KEY_ROTATION.md`. Their results are in the E-5 completion report.

This is local Development/Testing evidence only. It is not a security certification, not Azure staging evidence, and not Managed HSM evidence.
