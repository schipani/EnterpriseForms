# E-1 evidence: local keyring known-answer vectors

| File | Content |
|---|---|
| `e1-keyring-kat-crosscheck.py.txt` | Independent reproduction of the local development keyring construction with Python `cryptography` 50.0.2, written from the documented construction: HKDF root extraction and per-scope KEK derivation, then the scope-bound AES-256-GCM key wrap. It was run from a scratchpad virtual environment. |
| `e1-keyring-kat-output-python.txt` | Its output. `EncryptionKnownAnswerTests.TheLocalKeyringWrapReproducesTheVectorsExactly` asserts every reference, wrap AAD and wrapped DEK byte for byte. |

The vectors cover:

| Vector | Organization | Purpose | Version |
|---|---|---|---|
| K1 | A | `field-value` | 1 |
| K2 | A | `field-value` | 2 (current, with 1 retained) |
| K3 | A | `document` | 1 |
| K4 | B | `field-value` | 1 |

The KEK, DEK and wrap nonce are the E0-6 inputs (`00..1f`, `20..3f`, `40..4b`).
