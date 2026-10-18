#!/usr/bin/env bash
# Development-only bootstrap. Do not reuse this token or key material in any shared environment.
set -euo pipefail
VAULT_ADDR="${VAULT_ADDR:-http://127.0.0.1:8200}"
VAULT_TOKEN="${VAULT_TOKEN:-dev-only-token}"
ORG_ID="${DEV_ORGANIZATION_ID:-11111111-1111-4111-8111-111111111111}"
ORG_COMPACT="${ORG_ID//-/}"
KEY_NAME="org-${ORG_COMPACT}-row-integrity-v1"
if [[ "$VAULT_ADDR" != "http://127.0.0.1:8200" && "$VAULT_ADDR" != "http://localhost:8200" ]]; then
  echo 'Refusing to bootstrap a non-loopback development Vault endpoint' >&2
  exit 1
fi
for _ in {1..25}; do
  if curl -sf -H "X-Vault-Token: $VAULT_TOKEN" "$VAULT_ADDR/v1/sys/health" >/dev/null; then break; fi
  sleep 1
done
curl -sf -H "X-Vault-Token: $VAULT_TOKEN" -H 'Content-Type: application/json' \
  -X POST "$VAULT_ADDR/v1/sys/mounts/transit" -d '{"type":"transit"}' >/dev/null || true
curl -sf -H "X-Vault-Token: $VAULT_TOKEN" -H 'Content-Type: application/json' \
  -X POST "$VAULT_ADDR/v1/transit/keys/$KEY_NAME" -d '{"type":"hmac","exportable":false}' >/dev/null
PAYLOAD=$(printf 'integrity-smoke-test' | base64 | tr -d '\n')
curl -sf -H "X-Vault-Token: $VAULT_TOKEN" -H 'Content-Type: application/json' \
  -X POST "$VAULT_ADDR/v1/transit/hmac/$KEY_NAME/sha2-256" \
  -d "{\"input\":\"$PAYLOAD\",\"key_version\":1}"
printf '\nCreated development key: %s (version 1)\n' "$KEY_NAME"
