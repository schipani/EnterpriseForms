#!/bin/sh
# DEVELOPMENT/TESTING ONLY (E-4) - operator action, with the root token: PROVISION one new version of ONE existing business
# encryption key. It does NOT activate it.
#
#   ORGANIZATION_ID          the organization (UUID)
#   KEY_PURPOSE              field-value | document | auditor-reason-note | idempotency-protection
#   EXPECTED_LATEST_VERSION  the key's latest version before this action (guards against a repeated or concurrent run)
#
# The application keeps wrapping under the version named by Encryption:VaultTransit (key_version on every encrypt call) until an
# operator changes that configuration: that change is the activation (docs/security/E4_CONTROLLED_KEY_ACTIVATION.md).
# This script never creates a key, never changes min_decryption_version, min_encryption_version, exportability or
# auto-rotation, and never trims or deletes a version, so every earlier version stays decryptable.
set -eu

: "${VAULT_ADDR:?}" "${VAULT_TOKEN:?}" "${ORGANIZATION_ID:?set ORGANIZATION_ID}" "${KEY_PURPOSE:?set KEY_PURPOSE}" "${EXPECTED_LATEST_VERSION:?set EXPECTED_LATEST_VERSION}"
MOUNT="${BUSINESS_ENCRYPTION_MOUNT:-enterpriseforms-encryption}"
PREFIX="${BUSINESS_ENCRYPTION_KEY_PREFIX:-ef-enc}"

if ! echo "$ORGANIZATION_ID" | grep -Eq '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'; then
  echo "refusing: ORGANIZATION_ID is not a UUID" >&2
  exit 1
fi

case "$KEY_PURPOSE" in
  field-value|document|auditor-reason-note|idempotency-protection) ;;
  *) echo "refusing: KEY_PURPOSE is not a business encryption purpose" >&2; exit 1 ;;
esac

if ! echo "$EXPECTED_LATEST_VERSION" | grep -Eq '^[1-9][0-9]*$'; then
  echo "refusing: EXPECTED_LATEST_VERSION is not a positive integer" >&2
  exit 1
fi

ORG=$(echo "$ORGANIZATION_ID" | tr -d '-' | tr 'A-F' 'a-f')
NAME="$PREFIX-$ORG-$KEY_PURPOSE"

if ! INFO=$(vault read -format=json "$MOUNT/keys/$NAME" 2>/dev/null); then
  echo "refusing: $NAME does not exist (provision the organization's keys first; this script never creates a key)" >&2
  exit 1
fi

echo "$INFO" | grep -q '"type": "aes256-gcm96"' || { echo "refusing: $NAME is not an aes256-gcm96 key" >&2; exit 1; }
echo "$INFO" | grep -q '"exportable": false' || { echo "refusing: $NAME is exportable" >&2; exit 1; }
case "$(vault read -field=auto_rotate_period "$MOUNT/keys/$NAME")" in
  0|0s) ;;
  *) echo "refusing: $NAME has Vault auto-rotation enabled (versions must only be added by this explicit action)" >&2; exit 1 ;;
esac

LATEST=$(vault read -field=latest_version "$MOUNT/keys/$NAME")
MIN_DECRYPTION=$(vault read -field=min_decryption_version "$MOUNT/keys/$NAME")
if [ "$LATEST" != "$EXPECTED_LATEST_VERSION" ]; then
  echo "refusing: $NAME latest version is $LATEST, not the expected $EXPECTED_LATEST_VERSION (nothing changed)" >&2
  exit 1
fi

vault write -f "$MOUNT/keys/$NAME/rotate" >/dev/null

NEW=$(vault read -field=latest_version "$MOUNT/keys/$NAME")
if [ "$NEW" != "$((LATEST + 1))" ] || [ "$(vault read -field=min_decryption_version "$MOUNT/keys/$NAME")" != "$MIN_DECRYPTION" ]; then
  echo "error: $NAME did not move from version $LATEST to exactly $((LATEST + 1)) with min_decryption_version unchanged" >&2
  exit 1
fi

echo "provisioned: $NAME version $NEW (NOT active)."
echo "activate explicitly (this organization only): Encryption:VaultTransit:Organizations:$ORGANIZATION_ID:<Purpose>:CurrentVersion=$NEW,"
echo "keeping every version that still protects data (here $LATEST and earlier) in that table's RetainedVersions."
