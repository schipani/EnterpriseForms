# DEVELOPMENT ONLY (C-47) — BOOTSTRAP Vault policy for the Authorization Authority's token in the persistent development
# Vault. Exactly what VaultTransitHmacProvider needs to provision an organization key (org-<uuid>-<purpose>; the transit
# create endpoint is a POST, which Vault authorizes as `update`), HMAC and verify with it, and look up its own token.
# No export, backup, delete, policy, mount or sys access; the root token stays in the operator's custody file.
#
# `devstack.py init` replaces this policy, once the development organization is seeded, with one pinned to that single
# key's exact paths, so the Authority's token cannot reach .../config, .../rotate or any other key afterwards.
path "transit/keys/org-*" {
  capabilities = ["create", "read", "update"]
}

path "transit/hmac/org-*" {
  capabilities = ["update"]
}

path "transit/verify/org-*" {
  capabilities = ["update"]
}

path "auth/token/lookup-self" {
  capabilities = ["read"]
}
