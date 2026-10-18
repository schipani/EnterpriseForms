#!/usr/bin/env python3
"""Check approved pre-build specs; --strict also blocks unimplemented Stage 0 deliverables."""
from pathlib import Path
import argparse,hashlib,json,re,sys
ROOT=Path(__file__).resolve().parents[1]
KNOWN_STAGE0={
 "docs/dynamic-forms/architecture.md",
 "docs/dynamic-forms/row-integrity-format.md",
 "trust_authority/merkle-v1.test-vectors.json",
}

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--strict",action="store_true",help="require all Stage 0 outputs too")
    args=parser.parse_args()
    failures=[]; warnings=[]
    data=json.loads((ROOT/"spec-files.json").read_text(encoding="utf-8"))
    listed=set()
    for entry in data["entries"]:
        path=entry["path"]
        if path in listed: failures.append("Duplicate: "+path)
        listed.add(path)
        dest=ROOT/path
        if not dest.is_file(): failures.append("Missing tracked file: "+path); continue
        digest=hashlib.sha256(dest.read_bytes()).hexdigest()
        if digest!=entry["sha256"]: failures.append("Digest mismatch: "+path)
    for n in range(1,15):
        paths=list((ROOT/"docs/decisions").glob(f"ADR-PB-{n:03d}-*.md"))
        if len(paths)!=1:failures.append(f"Expected 1 ADR-PB-{n:03d}, got {len(paths)}")
    decision=(ROOT/"docs/decisions/PREBUILD_DECISION_REGISTER.md").read_text(encoding="utf-8")
    for n in range(1,15):
        if not re.search(r"^\| PB-%02d:.*?\| \*\*Accepted\*\*"%n,decision,re.M):
            failures.append("Decision not accepted in register: PB-%02d"%n)
    design=(ROOT/"CLAUDE-DESIGN.MD").read_text(encoding="utf-8")
    owner_ids=set(int(v) for v in re.findall(r"^\| PG-(\d{3}) \| `[^`]+` \|",design,re.M))
    if owner_ids!=set(range(1,70)): failures.append("69-page owner matrix incomplete")
    paths=set()
    for name in ("CLAUDE.md","CLAUDE-DESIGN.MD"):
        txt=(ROOT/name).read_text(encoding="utf-8")
        # Longest extensions first so ".csv" is not misread as ".cs".
        refs=re.findall(r"(?<![A-Za-z0-9_])(?:docs|security_hardening|security_validation|form_model|trust_authority)/[A-Za-z0-9_./-]+\.(?:json|yaml|yml|csv|css|sql|md|MD|cs|ts|sh)",txt)
        paths.update(refs)
    for ref in sorted(paths):
        if (ROOT/ref).is_file(): continue
        if ref in KNOWN_STAGE0 and not args.strict:
            warnings.append("Pending Stage 0 output: "+ref)
        else: failures.append("Broken reference: "+ref)
    for path in sorted(KNOWN_STAGE0):
        if not (ROOT/path).exists() and args.strict: failures.append("Missing mandatory Gate 0 output: "+path)
    print(f"Tracked: {len(listed)}; approved ADRs: 14; owned pages: {len(owner_ids)}")
    for x in warnings: print("PENDING:",x)
    for x in failures: print("ERROR:",x,file=sys.stderr)
    if failures: return 1
    print("BASELINE VALIDATION PASS" if not args.strict else "STRICT VALIDATION PASS")
    return 0
if __name__=="__main__": raise SystemExit(main())
