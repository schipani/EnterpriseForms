#!/usr/bin/env python3
"""Regenerate canonical handoff checksum manifest after reviewed specification changes.

The manifest is a detection mechanism, not a substitute for signed Git review.
"""
from pathlib import Path
import hashlib, json
ROOT=Path(__file__).resolve().parents[1]
TOP=["CLAUDE.md","CLAUDE-DESIGN.MD","README.md","SECURITY_THREAT_MODEL.md","SECURITY_GAP_ASSESSMENT.md","ENTERPRISE_SECURITY_REDUNDANCY_AUDIT.md","PREBUILD_READINESS_AUDIT.md","SECURITY_SIMPLIFICATION_RELEASE_NOTES.md"]
SCOPES=["docs","security_hardening","security_validation","trust_authority","form_model","scripts"]
files=[ROOT/x for x in TOP]
for scope in SCOPES:
    files.extend(p for p in (ROOT/scope).rglob("*") if p.is_file() and "__pycache__" not in p.parts and p.suffix not in {".zip",".pyc"})
files=sorted(set(files),key=lambda p:p.relative_to(ROOT).as_posix())
entries=[]
for p in files:
    if not p.exists():raise SystemExit(f"Required baseline missing: {p}")
    entries.append({"path":p.relative_to(ROOT).as_posix(),"sha256":hashlib.sha256(p.read_bytes()).hexdigest(),"bytes":p.stat().st_size})
(ROOT/"spec-files.json").write_text(json.dumps({"schema_version":1,"snapshot_type":"pre-build-14-decisions-approved","entries":entries},indent=2)+"\n",encoding="utf-8")
(ROOT/"SHA256SUMS.txt").write_text("\n".join(x["sha256"]+"  "+x["path"] for x in entries)+"\n",encoding="utf-8")
print(f"Recorded {len(entries)} files. Review diffs; do not auto-accept untrusted changes.")
