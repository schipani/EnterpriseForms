#!/bin/sh
# DEVELOPMENT/TESTING ONLY (E-2) — operator key provisioning for ONE organization (ORGANIZATION_ID, a UUID), with the root token.
# Creates the four purpose keys the business encryption provider expects, if they do not already exist:
#   ef-enc-<organization, 32 lowercase hex>-{field-value,document,auditor-reason-note,idempotency-protection}
# each `aes256-gcm96`, non-exportable, no plaintext backup, then checks those properties. Existing keys are never changed,
# rotated or deleted. The application itself never creates keys (its token has no `create` capability).
set -eu

: "${VAULT_ADDR:?}" "${VAULT_TOKEN:?}" "${ORGANIZATION_ID:?set ORGANIZATION_ID to the organization id}"
MOUNT="${BUSINESS_ENCRYPTION_MOUNT:-enterpriseforms-encryption}"
PREFIX="${BUSINESS_ENCRYPTION_KEY_PREFIX:-ef-enc}"

if ! echo "$ORGANIZATION_ID" | grep -Eq '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'; then
  echo "refusing: ORGANIZATION_ID is not a UUID" >&2
  exit 1
fi

ORG=$(echo "$ORGANIZATION_ID" | tr -d '-' | tr 'A-F' 'a-f')
for PURPOSE in field-value document auditor-reason-note idempotency-protection; do
  NAME="$PREFIX-$ORG-$PURPOSE"
  if ! vault read "$MOUNT/keys/$NAME" >/dev/null 2>&1; then
    vault write "$MOUNT/keys/$NAME" type=aes256-gcm96 exportable=false allow_plaintext_backup=false >/dev/null
  fi

  INFO=$(vault read -format=json "$MOUNT/keys/$NAME")
  echo "$INFO" | grep -q '"type": "aes256-gcm96"' || { echo "refusing: $NAME is not an aes256-gcm96 key" >&2; exit 1; }
  echo "$INFO" | grep -q '"exportable": false' || { echo "refusing: $NAME is exportable" >&2; exit 1; }
  echo "$INFO" | grep -q '"allow_plaintext_backup": false' || { echo "refusing: $NAME allows plaintext backup" >&2; exit 1; }
  echo "ready: $NAME"
done
