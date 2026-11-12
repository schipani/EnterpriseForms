"""C-149: summarizes authority_db_wait_sampler.sh output (connection counts and active-backend wait events).
Usage: python summarize_wait_samples.py <samples.txt> [<out.json>]"""
import collections
import json
import statistics
import sys


def main():
    path = sys.argv[1]
    totals, actives, idle_tx, waits = [], [], [], collections.Counter()
    samples = 0
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            parts = line.strip().split("|")
            if len(parts) != 5 or not parts[1].isdigit():
                continue
            samples += 1
            totals.append(int(parts[1]))
            actives.append(int(parts[2]))
            idle_tx.append(int(parts[3]))
            for w in filter(None, parts[4].split(";")):
                waits[w] += 1
    active_samples = sum(waits.values())
    report = {
        "samples": samples,
        "client_connections": {"mean": round(statistics.mean(totals), 2), "max": max(totals)} if totals else None,
        "active_backends": {"mean": round(statistics.mean(actives), 2), "max": max(actives)} if actives else None,
        "idle_in_transaction": {"mean": round(statistics.mean(idle_tx), 2), "max": max(idle_tx)} if idle_tx else None,
        "active_backend_samples_by_wait": {k: {"samples": v, "share": round(v / active_samples, 3)} for k, v in waits.most_common()},
        "note": "0.5 s sampling of pg_stat_activity (client backends of AuthorityDb, excluding the sampler); 'CPU:running' means active with no wait event",
    }
    text = json.dumps(report, indent=2)
    if len(sys.argv) > 2:
        with open(sys.argv[2], "w", newline="", encoding="utf-8") as out:
            out.write(text)
    print(text)


if __name__ == "__main__":
    main()
