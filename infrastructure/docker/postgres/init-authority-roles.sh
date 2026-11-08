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

# IX-3 (design §5.1): the indexed-search reader login, with its own credential. Optional: stacks that do not set it have no
# query path (fail closed). INHERIT so it receives the SELECT-only grants of authority_search_reader (granted by the migration
# step, which also sets its read-only default and logging settings). It holds no other privilege.
if [ -n "${AUTHORITY_SEARCH_READER_USER:-}" ]; then
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    CREATE ROLE ${AUTHORITY_SEARCH_READER_USER} LOGIN PASSWORD '${AUTHORITY_SEARCH_READER_PASSWORD:?AUTHORITY_SEARCH_READER_PASSWORD required}'
        NOSUPERUSER NOCREATEDB NOCREATEROLE INHERIT NOREPLICATION NOBYPASSRLS;
    GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO ${AUTHORITY_SEARCH_READER_USER};
EOSQL
fi
