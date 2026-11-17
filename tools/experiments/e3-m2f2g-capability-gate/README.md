# E3-M2f-2G capability-floor verification agents — disposable-environment gate

Development/Testing only. **Test-only.** Not part of `EnterpriseForms.All.slnx`; never built in place. Owner authorization of E3-M2f-2G
(2026-11-16). Record: `docs/security/E3_M2F2G_VERIFICATION_RECORD.md`. Built and run only by `scripts/dev/e3-m2e2-acceptance-pg-gate.ps1`
(groups G, GC, GR, GH of `tools/experiments/e3-m2e2-acceptance-gate/HarnessE2`).

| Agent | Built against | Does |
|---|---|---|
| `BusinessAgent` | One business Release output each: `git archive 11e15ee` (before E3-M2b-2), `git archive 0be5f52` (E3-M2e-2, no header), this tree | `encode`: that build's own `BusinessSignedSchemas.Encode` of stored row values. `send`: one content request through that build's own `AuthorityHttpClient`, `ProtectionVerifierHttpClient` or `SearchIndexBuilderHttpClient` to the run's loopback Authority host, recording the capability header instances that actually left the process |
| `AuthorityAgent` | One Authority Release output each: `git archive 0be5f52` (baseline without E3-M2f-2), this tree (control) | One of the seven content entry points of that build's own services on the run's disposable database (least-privilege runtime role, Release B floor 3), with the harness's deterministic test MAC and a SpiceDB double |

The same source compiles unchanged against every build; no historical source is modified. Agent inputs (test-only credentials, the run's
seal key) stay in the work directory; only outputs are copied into the evidence.
