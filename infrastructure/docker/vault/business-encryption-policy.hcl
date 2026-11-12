# DEVELOPMENT/TESTING ONLY (E-2) — the Business API and Worker encryption token's whole policy.
#
# Wrap and unwrap data keys on the dedicated business encryption mount, under the business key-name prefix, and nothing else:
# - `update` only. Transit's encrypt endpoint creates a missing key ("upsert") only for a token with `create`, so this token can
#   never create a key; keys are provisioned by an operator (vault/provision-business-encryption-keys.sh).
# - No access to transit/keys/* (read, create, rotate, config, trim, delete), export/*, backup/*, restore/*, datakey/*, rewrap/*,
#   sys/*, auth/*, or the Authority's HMAC mount (`transit`).
# The token carries this policy alone (-no-default-policy), so it cannot even look itself up.
path "enterpriseforms-encryption/encrypt/ef-enc-*" {
  capabilities = ["update"]
}

path "enterpriseforms-encryption/decrypt/ef-enc-*" {
  capabilities = ["update"]
}
