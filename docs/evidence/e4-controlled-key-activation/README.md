# E-4 evidence: controlled key activation

| File | Content |
|---|---|
| `e4-vault-integration-run.txt` | Output of `scripts/dev/e2-vault-integration.ps1` (extended for E-4), run against a throwaway HashiCorp Vault 1.20.4 dev container. Random tokens are not recorded. See below for what the run does. |

The run:
1. Prepares Vault with the E-2 init and provisioning scripts.
2. Provisions a second organization (`019a5b2c-e400-7000-8000-0000000000e4`).
3. Adds version 2 of that organization's `field-value` key with `infrastructure/docker/vault/provision-business-encryption-key-version.sh` (not activated).
4. Shows five refusals of that script, each with its reason: a repeated run, an unknown purpose, a missing key, a non-UUID organization, and an invalid expected version.
5. Runs `VaultTransitDockerIntegrationTests` (5, E-2) and `KeyActivationVaultIntegrationTests` (4, E-4): 9 tests passed.

The focused fake-Vault and local-keyring tests (`KeyActivationTests`, 14) are listed in `docs/security/E4_CONTROLLED_KEY_ACTIVATION.md`. Their results are in the E-4 completion report.

This is local Development/Testing evidence only. It is not a security certification and not Azure staging evidence.
