#!/usr/bin/env bash
# AuthorityDb bootstrap (runs once on first cluster initialization). Separate cluster, separate admin, separate runtime role.
# The authority runtime role has no DDL and no DELETE on journal tables (grants applied by the migration step).
set -euo pipefail
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    CREATE ROLE ${AUTHORITY_RUNTIME_USER} LOGIN PASSWORD '${AUTHORITY_RUNTIME_PASSWORD}'
        NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
    REVOKE ALL ON DATABASE ${POSTGRES_DB} FROM PUBLIC;
    GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO ${AUTHORITY_RUNTIME_USER};
    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
EOSQL
