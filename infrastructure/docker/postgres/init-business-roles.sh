#!/usr/bin/env bash
# BusinessDb bootstrap (runs once on first cluster initialization via /docker-entrypoint-initdb.d).
# Creates the least-privilege runtime role used by the business API (G08). The admin role stays the migration identity
# (JIT in Azure); the runtime role owns nothing, has no DDL, NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS.
set -euo pipefail
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    CREATE ROLE ${BUSINESS_RUNTIME_USER} LOGIN PASSWORD '${BUSINESS_RUNTIME_PASSWORD}'
        NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
    REVOKE ALL ON DATABASE ${POSTGRES_DB} FROM PUBLIC;
    GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO ${BUSINESS_RUNTIME_USER};
    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
EOSQL
