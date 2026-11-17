"""C-149 (R-02): pooled analysis of paired commit-latency runs (IX-1 evidence section 6.5).

Reads the raw per-commit CSVs written by SearchIndexFoundationTests.MeasurePairedInterleavedCommitLatencyWithAndWithoutTheIndex
(artifacts/ix-5/r02/p5-paired-<label>.csv), excludes only the flagged warm-up pairs, and reports:
  - per run: p50/p95/p99 of each series, the p95 difference and its 95% percentile-bootstrap interval (pairs resampled);
  - pooled: the p95 difference over all runs' pairs and its 95% interval by a stratified bootstrap (pairs resampled within each run);
  - the section 6.5 pass criterion: pooled upper bound <= 20 ms AND no run's interval entirely above 20 ms; otherwise FAIL or
    INCONCLUSIVE (a point estimate under 20 ms whose interval reaches above 20 ms is not a pass).
Nearest-rank percentiles. Standard library only. Usage: python p5_paired_pooled.py <out.json> <csv> [<csv> ...]
"""
import csv
import json
import math
import random
import statistics
import sys

GATE_MS = 20.0
RESAMPLES = 10_000


def nearest(sorted_values, p):
    return sorted_values[min(len(sorted_values) - 1, max(0, math.ceil(p * len(sorted_values)) - 1))]


def p95_delta(pairs):
    return nearest(sorted(p[0] for p in pairs), 0.95) - nearest(sorted(p[1] for p in pairs), 0.95)


def load(path):
    rows = {}
    with open(path, newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            if row["warmup"] == "1" or not row["elapsed_ms"]:
                continue
            rows.setdefault(int(row["pair"]), {})[row["series"]] = float(row["elapsed_ms"])
    return [(v["indexed"], v["plain"]) for _, v in sorted(rows.items()) if "indexed" in v and "plain" in v]


def summary(values):
    s = sorted(values)
    return {"p50": round(nearest(s, 0.50), 2), "p95": round(nearest(s, 0.95), 2), "p99": round(nearest(s, 0.99), 2), "max": round(s[-1], 2)}


def main():
    out, paths = sys.argv[1], sys.argv[2:]
    rng = random.Random(149)
    runs = []
    for path in paths:
        pairs = load(path)
        boot = sorted(p95_delta([pairs[rng.randrange(len(pairs))] for _ in pairs]) for _ in range(RESAMPLES))
        runs.append({"csv": path, "pairs": len(pairs), "indexed_ms": summary([p[0] for p in pairs]), "plain_ms": summary([p[1] for p in pairs]),
                     "delta_p50_ms": round(nearest(sorted(p[0] for p in pairs), 0.5) - nearest(sorted(p[1] for p in pairs), 0.5), 2),
                     "delta_p95_ms": round(p95_delta(pairs), 2), "delta_p99_ms": round(nearest(sorted(p[0] for p in pairs), 0.99) - nearest(sorted(p[1] for p in pairs), 0.99), 2),
                     "ci95_ms": [round(nearest(boot, 0.025), 2), round(nearest(boot, 0.975), 2)],
                     "paired_difference_ms": summary([p[0] - p[1] for p in pairs]), "_pairs": pairs})
    pooled_pairs = [p for r in runs for p in r["_pairs"]]
    pooled_boot = sorted(p95_delta([q for r in runs for q in (r["_pairs"][rng.randrange(len(r["_pairs"]))] for _ in r["_pairs"])]) for _ in range(RESAMPLES))
    lower, upper = nearest(pooled_boot, 0.025), nearest(pooled_boot, 0.975)
    any_run_above = any(r["ci95_ms"][0] > GATE_MS for r in runs)
    point = p95_delta(pooled_pairs)
    if upper <= GATE_MS and not any_run_above:
        verdict = "PASS"
    elif lower > GATE_MS or any_run_above:
        verdict = "FAIL"
    else:
        verdict = "INCONCLUSIVE" if point <= GATE_MS else "FAIL"
    deltas = [r["delta_p95_ms"] for r in runs]
    report = {
        "kind": "C-149 R-02 pooled paired commit-latency analysis (section 6.5 criterion; local Docker; indicative, not staging)",
        "gate_p95_delta_ms": GATE_MS, "resamples": RESAMPLES, "runs": [{k: v for k, v in r.items() if k != "_pairs"} for r in runs],
        "pooled": {"pairs": len(pooled_pairs), "indexed_ms": summary([p[0] for p in pooled_pairs]), "plain_ms": summary([p[1] for p in pooled_pairs]),
                   "delta_p95_ms": round(point, 2), "ci95_ms": [round(lower, 2), round(upper, 2)],
                   "paired_difference_ms": summary([p[0] - p[1] for p in pooled_pairs]),
                   "note": "delta_p95_ms is p95(indexed) - p95(plain), the gate statistic; paired_difference_ms gives percentiles of (indexed - plain) per pair"},
        "run_to_run_delta_p95_ms": {"mean": round(statistics.mean(deltas), 2), "stdev": round(statistics.stdev(deltas), 2) if len(deltas) > 1 else None, "min": min(deltas), "max": max(deltas)},
        "any_run_interval_entirely_above_gate": any_run_above,
        "verdict": verdict,
    }
    with open(out, "w", newline="", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
    print(json.dumps({k: report[k] for k in ("pooled", "run_to_run_delta_p95_ms", "any_run_interval_entirely_above_gate", "verdict")}, indent=2))


if __name__ == "__main__":
    main()
