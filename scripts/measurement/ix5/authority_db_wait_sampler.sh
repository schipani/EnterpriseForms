#!/usr/bin/env bash
# C-149 (R-02, R-55): samples AuthorityDb client-backend activity every 0.5 s while a measurement runs (security-test stack only):
# active backends by wait event, and connection counts (total, active, idle in transaction). Output: one line per sample,
# "epoch|total|active|idle_in_transaction|wait_event_type:wait_event=n;..." appended to <output>. Stops after <seconds> or when
# <stop-file> appears. Read-only; one short query per sample.
# Usage: authority_db_wait_sampler.sh <output> <seconds> [<stop-file>]
set -uo pipefail
out="$1"; seconds="$2"; stop="${3:-}"
db=enterpriseforms-sectest-authority-db-1
query="SELECT extract(epoch FROM clock_timestamp())::numeric(16,3) || '|' || count(*) || '|' || count(*) FILTER (WHERE state = 'active') || '|' || count(*) FILTER (WHERE state = 'idle in transaction') || '|' || coalesce(string_agg(w, ';'), '') FROM (SELECT state, CASE WHEN state = 'active' THEN coalesce(wait_event_type, 'CPU') || ':' || coalesce(wait_event, 'running') END AS w FROM pg_stat_activity WHERE datname = current_database() AND backend_type = 'client backend' AND pid <> pg_backend_pid()) a"
end=$(( $(date +%s) + seconds ))
# One psql session: \watch keeps a single connection open (no per-sample connection cost).
count=$(( seconds * 2 ))
docker exec "$db" sh -c "PGPASSWORD=\"\$POSTGRES_PASSWORD\" psql -U \"\$POSTGRES_USER\" -d \"\$POSTGRES_DB\" -Atq -c \"$query\" -c '\\watch i=0.5 c=$count'" >> "$out" 2>/dev/null &
pid=$!
while kill -0 "$pid" 2>/dev/null; do
  if [ -n "$stop" ] && [ -f "$stop" ]; then kill "$pid" 2>/dev/null; break; fi
  if [ "$(date +%s)" -ge "$end" ]; then kill "$pid" 2>/dev/null; break; fi
  sleep 2
done
wait "$pid" 2>/dev/null
exit 0
