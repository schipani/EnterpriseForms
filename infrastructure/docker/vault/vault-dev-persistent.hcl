# DEVELOPMENT ONLY (C-47) — persistent local Vault for the Vault Transit signing provider of the Authorization Authority.
#
# Replaces `vault server -dev` (in-memory, auto-unsealed) for the long-lived development stack only. Integrated storage
# (Raft) on a dedicated named volume keeps the transit keys across container restarts, Docker Desktop restarts and host
# reboots; Vault starts SEALED after every restart and is unsealed with the custody material held outside the
# repository (docs/infrastructure/DEV_ENVIRONMENT_PERSISTENT_VAULT.md). The Authority fails closed while Vault is sealed.
#
# Not a production configuration: production signing uses Azure Managed HSM (ADR-PB-005, G04); the host policy refuses
# the Vault Transit provider in Staging/Production. Plain HTTP is acceptable only because the listener is reachable
# solely on the internal authority-net Docker network (no host port is published).
storage "raft" {
  path    = "/vault/file"
  node_id = "enterpriseforms-dev-vault"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true
}

api_addr      = "http://127.0.0.1:8200"
cluster_addr  = "http://127.0.0.1:8201"
disable_mlock = true
ui            = false

# The Authority's token is a long-lived development token (no renewal loop in the provider); bounded here, not unlimited.
default_lease_ttl = "768h"
max_lease_ttl     = "87600h"
