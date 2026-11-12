# E-6a Managed HSM HMAC compatibility probe

Development/Testing experiment. **Not** an EnterpriseForms provider.

The probe is standalone:

- It is not part of `EnterpriseForms.All.slnx`, so CI does not build it.
- It references no EnterpriseForms project, and none references it.
- It pins its own package versions. Central package management is off for these two projects.

Decision record: [`docs/security/E6A_MANAGED_HSM_COMPATIBILITY_PROBE.md`](../../../docs/security/E6A_MANAGED_HSM_COMPATIBILITY_PROBE.md).

It answers one question for a later, **separately authorized** run against a throwaway non-production Managed HSM:

> Does Managed HSM `HS256` over the raw canonical bytes produce the same 32-byte MAC as `LocalHmacProvider` and `VaultTransitHmacProvider`?

## Layout

| Path | Contents |
|---|---|
| `ManagedHsmProbe/` | The console probe. Default `local` mode makes no network call. |
| `ManagedHsmProbe.Tests/` | Local-only tests. A fake HTTP handler stands in for the Managed HSM data plane. |
| `vectors/e6a-reference-vectors.json` | Known-answer vectors under two published test keys. They are embedded in the probe. |
| `vectors/verify_vectors.py` | Independent Python check of the vectors (stdlib `hmac`/`hashlib`). |
| `probe.settings.template.json` | Test-only configuration template. It is unusable until edited. Copy it outside source control first. |

## Local commands

Run from the repository root.

```
dotnet build tools/experiments/e6a-managed-hsm-probe/ManagedHsmProbe.Tests
tools/experiments/e6a-managed-hsm-probe/ManagedHsmProbe.Tests/bin/Debug/net10.0/ManagedHsmProbe.Tests.exe
tools/experiments/e6a-managed-hsm-probe/ManagedHsmProbe/bin/Debug/net10.0/ManagedHsmProbe.exe            # local self-checks
python -I tools/experiments/e6a-managed-hsm-probe/vectors/verify_vectors.py
```

To regenerate the vectors (deterministic; a test fails if the committed file drifts):

```
ManagedHsmProbe generate-vectors --row-vectors trust_authority/row-integrity-v1.test-vectors.json --out tools/experiments/e6a-managed-hsm-probe/vectors/e6a-reference-vectors.json
```

## HSM commands (future, owner-authorized only)

`hsm-import-test-keys` and `hsm-run` contact Azure. Each one requires all of the following:

- `Mode=ManagedHsm` and `AllowAzureCalls=true` in the configuration file;
- the `--allow-azure-calls` flag on the command line;
- a valid `https://<name>.managedhsm.azure.net/` endpoint (ordinary Key Vault hosts are refused);
- a key name matching `e6a-probe-*`;
- an explicit credential (`AzureCli` or `ManagedIdentity`; there is no chained credential);
- an explicit key provenance;
- a static integer-to-opaque version mapping.

`hsm-import-test-keys` also requires `AllowTestKeyImport=true`. The full procedure is in the decision record.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Pass |
| 1 | Fail |
| 2 | Usage or configuration error. Nothing was contacted. |
| 3 | Inconclusive |
| 130 | Cancelled |
