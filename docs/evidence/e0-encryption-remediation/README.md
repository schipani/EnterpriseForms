# E-0 evidence: known-answer vector cross-check

| File | Content |
|---|---|
| `e0-kat-crosscheck.py.txt` | Independent reproduction of the E0-6 AES-256-GCM v1 vectors (field value and segmented document) with Python `cryptography` 50.0.2 AESGCM, written from the documented layouts. It was run from a scratchpad virtual environment. |
| `e0-kat-output-python.txt` | Its output. Every value is asserted byte for byte by `tests/features/EnterpriseForms.Api.ContractTests/EncryptionKnownAnswerTests.cs` (AAD, ciphertext, tags, wrapped DEKs, plaintext/ciphertext SHA-256 and lengths). |

The F1 wrapped DEK equals the KAT-M1-01 wrapped DEK in `docs/evidence/phase-c8/m4-1-protocol-verification/`, because both use the same KEK, DEK and wrap nonce. This is a consistency check across the two independent reproductions.
