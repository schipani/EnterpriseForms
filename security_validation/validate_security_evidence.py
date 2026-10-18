#!/usr/bin/env python3
"""Validate claimed Gate A evidence manifest structure/completeness and local artifact SHA256.
This does NOT run tests, attest CI provenance, verify recorded assertions, or certify security.
"""
import sys,json,hashlib,re
from pathlib import Path
from datetime import datetime,timezone
EXPECTED={f"SEC-P{i:02d}" for i in range(1,13)}
SHA_RE=re.compile(r"^[0-9a-f]{40}$")
DIGEST_RE=re.compile(r"^[0-9a-f]{64}$")
def fail(reason):
    print(f"BLOCKED: {reason}",file=sys.stderr)
    return 1

def check_iso(s):
    if not isinstance(s,str) or not s.endswith('Z'): return False
    try:return datetime.fromisoformat(s.replace('Z','+00:00')).tzinfo is not None
    except ValueError:return False

def main():
    if len(sys.argv)!=2:return fail('usage: validate_security_evidence.py <manifest.json>')
    manifest=Path(sys.argv[1]).resolve()
    try:data=json.loads(manifest.read_text(encoding='utf-8'))
    except (OSError,ValueError) as e:return fail(f'cannot read manifest: {e}')
    if not isinstance(data,dict):return fail('manifest must be an object')
    if data.get('schema_version')!='1.0' or data.get('gate_id')!='PB-03-STAGE-A' or data.get('environment_kind')!='disposable-docker':return fail('wrong gate/schema/environment')
    commit=data.get('commit_sha')
    img=data.get('image_fingerprint')
    if not isinstance(commit,str) or not SHA_RE.fullmatch(commit):return fail('invalid commit SHA')
    if not isinstance(img,str) or len(img)<10:return fail('missing image fingerprint')
    if not check_iso(data.get('executed_at_utc')):return fail('invalid UTC execution timestamp')
    results=data.get('results')
    if not isinstance(results,list):return fail('results must be an array')
    seen=set()
    root=manifest.parent
    for result in results:
        if not isinstance(result,dict):return fail('invalid result object')
        tid=result.get('test_id')
        if tid not in EXPECTED or tid in seen:return fail(f'duplicate/unknown test {tid!r}')
        seen.add(tid)
        if result.get('outcome')!='PASS':return fail(f'{tid}: outcome not PASS')
        if result.get('commit_sha')!=commit or result.get('image_fingerprint')!=img:return fail(f'{tid}: code/image drift')
        if not check_iso(result.get('executed_at_utc')):return fail(f'{tid}: invalid UTC timestamp')
        artifact=result.get('evidence_path')
        expected=result.get('evidence_sha256')
        if not isinstance(artifact,str) or not artifact or not isinstance(expected,str) or not DIGEST_RE.fullmatch(expected):return fail(f'{tid}: missing artifact path/digest')
        path=(root/artifact).resolve()
        # allow only artifacts inside the manifest directory (no traversal or unrelated /etc files)
        if not path.is_relative_to(root) or not path.is_file():return fail(f'{tid}: artifact absent/outside evidence directory')
        digest=hashlib.file_digest(path.open('rb'),'sha256').hexdigest()
        if digest!=expected:return fail(f'{tid}: artifact digest mismatch')
    if seen!=EXPECTED:return fail(f'missing tests: {sorted(EXPECTED-seen)}')
    print('Manifest structure and local evidence digests: PASS (NOT security test attestation)')
    return 0
if __name__=='__main__':sys.exit(main())
