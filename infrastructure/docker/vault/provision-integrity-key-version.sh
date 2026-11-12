#!/bin/sh
# DEVELOPMENT/TESTING ONLY (E-5) - operator action, with the root token: PROVISION one new version of ONE organization's existing
# Authority HMAC integrity key (transit/keys/org-<organization, 32 hex>-row-integrity-v1). It does NOT activate it.
#
#   ORGANIZATION_ID          the organization (UUID)
#   EXPECTED_LATEST_VERSION  the key's latest version before this action (guards against a repeated or concurrent run)
#
# The Authority keeps signing with the version its key registration names (key_version on every HMAC call) until an operator
# activates the new version with the Authority's --activate-integrity-key-version command (docs/security/E5_HMAC_SIGNING_KEY_ROTATION.md).
# This script never creates a key, never changes min_decryption_version, min_encryption_version, exportability or auto-rotation,
# and never trims or deletes a version, so every earlier signature stays verifiable. The Authority's own token cannot do this.
set -eu

: "${VAULT_ADDR:?}" "${VAULT_TOKEN:?}" "${ORGANIZATION_ID:?set ORGANIZATION_ID}" "${EXPECTED_LATEST_VERSION:?set EXPECTED_LATEST_VERSION}"
MOUNT="${AUTHORITY_TRANSIT_MOUNT:-transit}"
PURPOSE="row-integrity-v1"

if ! echo "$ORGANIZATION_ID" | grep -Eq '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'; then
  echo "refusing: ORGANIZATION_ID is not a UUID" >&2
  exit 1
fi

if ! echo "$EXPECTED_LATEST_VERSION" | grep -Eq '^[1-9][0-9]*$'; then
  echo "refusing: EXPECTED_LATEST_VERSION is not a positive integer" >&2
  exit 1
fi

ORG=$(echo "$ORGANIZATION_ID" | tr -d '-' | tr 'A-F' 'a-f')
NAME="org-$ORG-$PURPOSE"

if ! INFO=$(vault read -format=json "$MOUNT/keys/$NAME" 2>/dev/null); then
  echo "refusing: $NAME does not exist (key custody provisions the key; this script never creates one)" >&2
  exit 1
fi

echo "$INFO" | grep -q '"type": "hmac"' || { echo "refusing: $NAME is not an HMAC key" >&2; exit 1; }
echo "$INFO" | grep -q '"exportable": false' || { echo "refusing: $NAME is exportable" >&2; exit 1; }
echo "$INFO" | grep -q '"allow_plaintext_backup": false' || { echo "refusing: $NAME allows plaintext backup" >&2; exit 1; }
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
echo "activate explicitly: dotnet AuthorizationAuthority.Api.dll --activate-integrity-key-version --organization $ORGANIZATION_ID --expected-current-version <current> --signing-key-version $NEW --reason \"<text>\""
