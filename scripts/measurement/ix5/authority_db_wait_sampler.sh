#!/usr/bin/env bash
# C-149 (R-02, R-55): samples AuthorityDb client-backend activity about once per second while a measurement runs (security-test stack
# only): active backends by wait event, and connection counts (total, active, idle in transaction). Output: one line per sample,
# "epoch|total|active|idle_in_transaction|wait_event_type:wait_event;..." appended to <output>. Stops after <seconds> or when
# <stop-file> appears. Read-only; one short query per sample (each sample is its own psql connection, excluded from the counts).
# Usage: authority_db_wait_sampler.sh <output> <seconds> [<stop-file>]
set -uo pipefail
out="$1"; seconds="$2"; stop="${3:-}"
db=enterpriseforms-sectest-authority-db-1
query="SELECT extract(epoch FROM clock_timestamp())::numeric(16,3) || '|' || count(*) || '|' || count(*) FILTER (WHERE state = 'active') || '|' || count(*) FILTER (WHERE state = 'idle in transaction') || '|' || coalesce(string_agg(w, ';'), '') FROM (SELECT state, CASE WHEN state = 'active' THEN coalesce(wait_event_type, 'CPU') || ':' || coalesce(wait_event, 'running') END AS w FROM pg_stat_activity WHERE datname = current_database() AND backend_type = 'client backend' AND pid <> pg_backend_pid()) a"
end=$(( $(date +%s) + seconds ))
while [ "$(date +%s)" -lt "$end" ]; do
  if [ -n "$stop" ] && [ -f "$stop" ]; then break; fi
  docker exec "$db" sh -c "PGPASSWORD=\"\$POSTGRES_PASSWORD\" psql -U \"\$POSTGRES_USER\" -d \"\$POSTGRES_DB\" -Atq -c \"$query\"" >> "$out" 2>/dev/null
  sleep 1
done
exit 0
