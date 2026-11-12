#!/bin/sh
# DEVELOPMENT/TESTING ONLY (E-2). Run once against the local dev-mode Vault with the root token (VAULT_ADDR, VAULT_TOKEN):
#   1. enables the dedicated business encryption Transit mount (separate from the Authority's `transit` HMAC mount);
#   2. writes the business-encryption policy (wrap/unwrap only, see business-encryption-policy.hcl);
#   3. creates the dedicated business token with that policy alone (fixed id from BUSINESS_ENCRYPTION_VAULT_TOKEN, .env).
# It creates no keys: keys are provisioned per organization by provision-business-encryption-keys.sh. Idempotent.
set -eu

: "${VAULT_ADDR:?}" "${VAULT_TOKEN:?}" "${BUSINESS_ENCRYPTION_VAULT_TOKEN:?set in .env}"
MOUNT="${BUSINESS_ENCRYPTION_MOUNT:-enterpriseforms-encryption}"
POLICY_FILE="${BUSINESS_ENCRYPTION_POLICY_FILE:-/vault-config/business-encryption-policy.hcl}"

if [ "$MOUNT" = "transit" ]; then
  echo "refusing: the business encryption mount must not be the Authority's transit mount" >&2
  exit 1
fi

if [ "$BUSINESS_ENCRYPTION_VAULT_TOKEN" = "$VAULT_TOKEN" ]; then
  echo "refusing: the business encryption token must not be the root token" >&2
  exit 1
fi

if ! vault secrets list -format=json | grep -q "\"$MOUNT/\""; then
  vault secrets enable -path="$MOUNT" transit >/dev/null
fi

vault policy write business-encryption "$POLICY_FILE" >/dev/null

if ! vault token lookup "$BUSINESS_ENCRYPTION_VAULT_TOKEN" >/dev/null 2>&1; then
  vault token create -id="$BUSINESS_ENCRYPTION_VAULT_TOKEN" -orphan -no-default-policy -policy=business-encryption \
    -ttl=720h -display-name=business-encryption-dev >/dev/null
fi

echo "business encryption mount, policy and token ready"
