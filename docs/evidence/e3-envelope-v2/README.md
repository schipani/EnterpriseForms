# E3-M1 evidence: Envelope v2 known-answer vectors

| File | Content |
|---|---|
| `e3m1-kat-crosscheck.py.txt` | Independent reproduction of the Envelope v2 vectors with Python `cryptography` 50.0.2 (HKDF-SHA256, AESGCM). It was written from the specification in `docs/security/E3_ENVELOPE_V2_KEY_HIERARCHY_DESIGN.md` §3, not from the C# code. It ran from a scratchpad virtual environment (`python -I`). |
| `e3m1-kat-output-python.txt` | Its output, verbatim. `tests/features/EnterpriseForms.Api.ContractTests/EnvelopeV2KnownAnswerTests.cs` asserts every value byte for byte. |

**What the tests assert from the output:**
- H0;
- the key commitment;
- item keys and the document key;
- the full AAD;
- ciphertext and tags;
- the portable-envelope SHA-256 and length;
- the document portable header;
- ciphertext length and SHA-256 with per-segment tags;
- the full ciphertext for the two short documents.

The script was re-run after the files were copied here, and its output reproduced the committed file exactly.

## Vectors

| Vector | Kind | Inputs |
|---|---|---|
| B1.I1–I3 | FieldValueItem, one batch of three items (`String`, `Decimal`, Unicode `String`) | seed `60..7f`, data key `019a5b2c-a0d1-…`, reference `vault-transit:{org}:field-value:3`, synthetic wrapped seed `c0..fb`, nonces `90..`, `a0..`, `b0..` |
| S1.I1 | SealedKey (creation-idempotency fingerprint key) | seed `10..2f`, reference `…:idempotency-protection:1`, wrapped `08..43`, nonce `d0..` |
| D0–D4 | DocumentVersion: 0 bytes, 37 bytes, exactly one segment, one segment + 5 bytes, two segments | seed `30..4f`, nonce prefix `70..77`, reference `…:document:2`, wrapped `20..5b`, version 2 |

Notes on the inputs:
- **Plaintext pattern.** Document plaintext is byte *i* = (7*i* + 3) mod 256, as in E0-6. The plaintext SHA-256 values therefore equal the E0-6 v1 values, which is a consistency check across the two reproductions.
- **Fixed values are test-only.** Seeds, nonces, nonce prefixes and data-key identifiers are fixed only here. Production code takes them from `RandomNumberGenerator` and `Guid.CreateVersion7()`.
- **Synthetic wrapped seeds.** The wrapped seeds are synthetic 60-byte values. The wrap is provider-native and outside the v2 format; the E-1 K1–K4 vectors pin the keyring wrap.

Development/Testing evidence only; owner-led review. Not an independent cryptographic review (V2-IND-01 is outstanding).
