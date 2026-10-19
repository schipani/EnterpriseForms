#!/usr/bin/env python3
"""Regenerate docs/security/stage-a-vault-transit-vectors.json (SIG-11 / review question R6).

For every positive case in trust_authority/row-integrity-v1.test-vectors.json the canonical bytes are HMAC'd three ways:
  1. HashiCorp Vault Transit (throwaway dev container, published TEST key imported as a non-exportable `hmac` key),
  2. Python hmac/hashlib (stands in for .NET HMACSHA256 — identical primitive),
  3. the value recorded in the vector file.
All three must agree byte-for-byte, and a one-bit tamper of the message must fail Vault's verify endpoint.
Exit code is non-zero on any disagreement. The published test key is used for NOTHING else.

Prerequisites (reviewer-run, not CI):
  docker run -d --name sigvec-vault -e VAULT_DEV_ROOT_TOKEN_ID=sigvec-token hashicorp/vault:1.20.4
  docker exec -e VAULT_TOKEN=sigvec-token -e VAULT_ADDR=http://127.0.0.1:8200 sigvec-vault sh -c \
    "vault secrets enable transit && vault transit import transit/keys/published-test-key AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8= type=hmac exportable=false"
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VECTORS = ROOT / "trust_authority" / "row-integrity-v1.test-vectors.json"
OUT = ROOT / "docs" / "security" / "stage-a-vault-transit-vectors.json"
CONTAINER = "sigvec-vault"
KEY_NAME = "published-test-key"


def vault(*args: str) -> dict:
    result = subprocess.run(
        ["docker", "exec", "-e", "VAULT_TOKEN=sigvec-token", "-e", "VAULT_ADDR=http://127.0.0.1:8200", CONTAINER, "vault", *args],
        capture_output=True, text=True, check=True)
    return json.loads(result.stdout)["data"]


def main() -> int:
    vectors = json.loads(VECTORS.read_text(encoding="utf-8"))
    key = bytes.fromhex(vectors["testHmacKeyHex"])
    cases = []
    failures = 0
    for case in vectors["cases"]:
        expected = case["expected"]
        canonical = bytes.fromhex(expected["canonicalBytesHex"])
        token = vault("write", "-format=json", f"transit/hmac/{KEY_NAME}/sha2-256", "input=" + base64.b64encode(canonical).decode(), "key_version=1")["hmac"]
        prefix, version, mac_b64 = token.split(":", 2)
        vault_hex = base64.b64decode(mac_b64).hex()
        local_hex = hmac.new(key, canonical, hashlib.sha256).hexdigest()
        digest_ok = hashlib.sha256(canonical).hexdigest() == expected["canonicalPayloadDigestHex"]
        agree = prefix == "vault" and version == "v1" and vault_hex == local_hex == expected["rowSignatureHexWithTestKey"] and digest_ok
        failures += 0 if agree else 1
        cases.append({
            "case_id": case["caseId"],
            "canonical_byte_length": len(canonical),
            "canonical_payload_digest_hex": hashlib.sha256(canonical).hexdigest(),
            "vault_hmac_token": token,
            "vault_hmac_hex": vault_hex,
            "hmacsha256_local_hex": local_hex,
            "vector_row_signature_hex_with_test_key": expected["rowSignatureHexWithTestKey"],
            "all_three_agree": agree,
        })

    first = vectors["cases"][0]
    tampered = bytearray(bytes.fromhex(first["expected"]["canonicalBytesHex"]))
    tampered[-1] ^= 1
    verify = vault("write", "-format=json", f"transit/verify/{KEY_NAME}/sha2-256", "input=" + base64.b64encode(bytes(tampered)).decode(), "hmac=" + cases[0]["vault_hmac_token"])
    tamper_valid = bool(verify["valid"])
    failures += 1 if tamper_valid else 0

    OUT.write_bytes((json.dumps({
        "generated_on_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "purpose": "SIG-11 / review question R6: provider-independence of HMAC-SHA-256 over RowHmac/v1 canonical bytes (Vault Transit vs HMACSHA256 vs published vectors).",
        "vault": "hashicorp/vault:1.20.4 dev mode, throwaway container, key imported with `vault transit import ... type=hmac exportable=false`, key_version 1",
        "key_source": "trust_authority/row-integrity-v1.test-vectors.json testHmacKeyHex — PUBLISHED TEST KEY, exists only to make vectors reproducible; no deployment key equals or derives from it",
        "method": "POST transit/hmac/<key>/sha2-256 with input=base64(canonical bytes); the `vault:v1:` token is split and base64-decoded to 32 raw bytes exactly as VaultTransitHmacProvider.ExtractRawMac does",
        "tamper_check": {"case_id": first["caseId"], "last_byte_flipped_verify_valid": tamper_valid},
        "cases": cases,
        "result": "PASS" if failures == 0 else "FAIL",
    }, indent=2) + chr(10)).encode("utf-8"))
    print(f"{'PASS' if failures == 0 else 'FAIL'}: {len(cases)} cases, tampered verify valid={tamper_valid}; wrote {OUT.relative_to(ROOT).as_posix()}")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
