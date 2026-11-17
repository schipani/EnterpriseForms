# E3-M2c evidence — dark Envelope v2 readers and OD-M2c-02a decryption modes

Development/Testing evidence for `docs/security/E3_M2C_IMPLEMENTATION_RECORD.md`. Produced on branch `e3/m1-envelope-v2-core` on top of `9810a67a1135137585a1d0d7bc82e73a5bc7c1f9`, with the E3-M2c changes uncommitted at capture time; accepted by the owner for Development/Testing and recorded as one signed local commit (record §12). Owner-led work, not an independent review. No Azure call, no persistent database, no Stage B, no deployment.

| File | Content |
|---|---|
| `test-results.md` | Suite totals (contract, architecture, Authority, integrity, real Vault) and the names of the new tests |
| `vault-reader-run.txt` | Output of `scripts/dev/e3-m2c-vault-reader.ps1` (throwaway Vault 1.20.4 dev server on 127.0.0.1, removed afterwards) |
| `mutations/m2c_mutations.py` | Enforcement-removal driver: applies one mutation, builds, runs the focused contract classes and the architecture suite (time-boxed), restores the file from a backup and checks its SHA-256 |
| `mutations/results.json` | Per-mutation result: built, test totals, failing tests, detected, restore verified |
| `mutations/closure-results.json` | The same for the final security closure (MX-1..MX-5, reruns of MU-11..MU-13) |

Local paths and machine names are removed from the captured outputs.

## Re-running

```powershell
# Builds first (Debug), then:
tests\features\EnterpriseForms.Api.ContractTests\bin\Debug\net10.0\EnterpriseForms.Api.ContractTests.exe
tests\architecture\EnterpriseForms.ArchitectureTests\bin\Debug\net10.0\EnterpriseForms.ArchitectureTests.exe
powershell -File scripts\dev\e3-m2c-vault-reader.ps1          # needs Docker; uses port 18202
python docs\evidence\e3-m2c-readers\mutations\m2c_mutations.py <repo> <out.json> [MU-01,MU-02,...]
```

The mutation driver edits product source files in place and restores them; run it only on a clean working copy you can reset, and rebuild afterwards (it does a final rebuild).
