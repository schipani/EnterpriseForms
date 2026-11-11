"""C-149 (R-02): reconstructs the raw CSV of a stopped paired run from the Authority log and the record-to-series mapping.

The benchmark creates its records strictly sequentially, two per iteration (one indexed, one plain, randomized order), so the
IX1-COMMIT-TIMING lines in log order are the creation order and consecutive lines form the iterations. Every reconstructed pair is
checked to hold exactly one indexed and one plain record; a mismatch stops the reconstruction. The last line may be an
unpaired, interrupted iteration and is dropped. Same CSV columns as the test (created_on empty).
Usage: python p5_reconstruct_partial.py <authority.log> <records.csv: form_record_id,organization_id,indexed t|f> <out.csv> [warmup=50]
"""
import csv
import re
import sys

LINE = re.compile(r"IX1-COMMIT-TIMING record=([0-9a-f-]{36}) elapsed_ms=([0-9.,]+) db_commands=([0-9]+)")
STAGE = re.compile(r"IX1-DERIVE-TIMING record=([0-9a-f-]{36}) reads_ms=([0-9.,]+) derive_ms=([0-9.,]+) sign_ms=([0-9.,]+) writes_ms=([0-9.,]+)")


def main():
    log, mapping, out = sys.argv[1:4]
    warmup = int(sys.argv[4]) if len(sys.argv) > 4 else 50
    series = {}
    with open(mapping, encoding="utf-8") as handle:
        for row in csv.reader(handle):
            if len(row) == 3:
                series[row[0]] = "indexed" if row[2] == "t" else "plain"
    text = open(log, encoding="utf-8", errors="replace").read()
    commits = [(m.group(1), m.group(2).replace(",", "."), m.group(3)) for m in LINE.finditer(text) if m.group(1) in series]
    stages = {m.group(1): [g.replace(",", ".") for g in m.groups()[1:]] for m in STAGE.finditer(text)}
    pairs = len(commits) // 2
    with open(out, "w", newline="", encoding="utf-8") as handle:
        w = csv.writer(handle, lineterminator="\n")
        w.writerow(["pair", "warmup", "series", "order", "form_record_id", "created_on", "elapsed_ms", "db_commands", "reads_ms", "derive_ms", "sign_ms", "writes_ms"])
        for p in range(pairs):
            members = commits[2 * p:2 * p + 2]
            kinds = sorted(series[r] for r, _, _ in members)
            if kinds != ["indexed", "plain"]:
                raise SystemExit(f"pair {p} is not one indexed and one plain record: {members}")
            for order, (record, elapsed, commands) in enumerate(members):
                w.writerow([p, 1 if p < warmup else 0, series[record], order, record, "", elapsed, commands] + stages.get(record, ["", "", "", ""]))
    print(f"{len(commits)} timed commits, {pairs} pairs reconstructed ({max(0, pairs - warmup)} after {warmup} warm-up pairs)")


if __name__ == "__main__":
    main()
