#!/usr/bin/env python3
"""SEC-BL-01 RST-1/RST-2 (C-155): read-only check that a compose project's AuthorityDb, security-event archive and
journal-verification checkpoint store volumes have a coupled lifetime.

It only runs `docker volume ls` and `docker volume inspect`; it never creates, removes, resets or writes anything.

Findings (contract docs/security/SEC_BL_01_DR_CONTRACT_CLOSURE.md section 6.3):
  COUPLING_BROKEN      the AuthorityDb data volume exists but the archive volume does not (the archive was lost or never persisted)
  ARCHIVE_NEWER        the archive volume was created well after the AuthorityDb volume (the archive was recreated; AuthorityDb
                       history older than the archive is no longer archived; expect genuine verification findings, RST-2)
  ARCHIVE_WITHOUT_DB   the archive volume exists without the AuthorityDb volume
  CHECKPOINT_STORE_ABSENT
                       informational: the checkpoint store volume does not exist yet (profile journal-verification never started)
  CHECKPOINT_STORE_NEWER
                       the checkpoint store volume was created well after the archive volume (checkpoints were lost; a
                       re-baseline is required, CK-10)
Projects that keep these stores on tmpfs (security-test, integration) have no volumes and are reported as EPHEMERAL.

Limits (OBS-2, 2026-11-17): the check compares volume existence and creation times only. It does not look inside a volume, so it
cannot tell that a volume exists but was never used (before SEC-BL-01 N-1 the archive of compose.yaml and compose.dev.yaml lived in
the container layer while an empty archive-store-data volume was mounted), that a volume was emptied in place, or that its content
diverges from AuthorityDb. "coupled" therefore means "the volumes were created together", not "the archive matches AuthorityDb":
content-level agreement is established only by a baseline (WP-3) and the scheduled verifier (WP-4).

Exit code: 0 when no finding other than informational ones, 1 when a coupling finding exists, 2 when docker is unavailable.
Usage: python -I scripts/dev/check-journal-store-coupling.py [--project NAME ...] [--tolerance-seconds N]
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime

SUFFIX_DB = "_authority-db-data"
SUFFIX_ARCHIVE = "_archive-store-data"
SUFFIX_CHECKPOINTS = "_journal-verification-store-data"


def docker(*args: str) -> str:
    return subprocess.run(["docker", *args], check=True, capture_output=True, text=True, timeout=60).stdout


def created(volume: str) -> datetime:
    info = json.loads(docker("volume", "inspect", volume))[0]
    return datetime.fromisoformat(info["CreatedAt"].replace("Z", "+00:00"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--project", action="append", help="compose project name (default: every project with an AuthorityDb or archive volume)")
    parser.add_argument("--tolerance-seconds", type=int, default=600, help="creation-time difference treated as the same initialization")
    args = parser.parse_args()
    try:
        volumes = set(docker("volume", "ls", "-q").split())
    except (OSError, subprocess.SubprocessError) as error:
        print(f"docker unavailable: {error}", file=sys.stderr)
        return 2

    projects = set(args.project or [])
    if not projects:
        for volume in volumes:
            for suffix in (SUFFIX_DB, SUFFIX_ARCHIVE, SUFFIX_CHECKPOINTS):
                if volume.endswith(suffix):
                    projects.add(volume[: -len(suffix)])

    failing = False
    for project in sorted(projects):
        db, archive, checkpoints = (project + s for s in (SUFFIX_DB, SUFFIX_ARCHIVE, SUFFIX_CHECKPOINTS))
        findings: list[str] = []
        has_db, has_archive, has_checkpoints = db in volumes, archive in volumes, checkpoints in volumes
        if not (has_db or has_archive or has_checkpoints):
            findings.append("EPHEMERAL (no AuthorityDb, archive or checkpoint volume)")
        elif has_db and not has_archive:
            findings.append("COUPLING_BROKEN: AuthorityDb volume without an archive volume")
            failing = True
        elif has_archive and not has_db:
            findings.append("ARCHIVE_WITHOUT_DB: archive volume without an AuthorityDb volume")
            failing = True
        else:
            gap = (created(archive) - created(db)).total_seconds()
            if gap > args.tolerance_seconds:
                findings.append(f"ARCHIVE_NEWER: archive volume created {int(gap)} s after the AuthorityDb volume")
                failing = True
            if not has_checkpoints:
                findings.append("CHECKPOINT_STORE_ABSENT (informational)")
            else:
                checkpoint_gap = (created(checkpoints) - created(archive)).total_seconds()
                if checkpoint_gap > args.tolerance_seconds:
                    findings.append(f"CHECKPOINT_STORE_NEWER: checkpoint volume created {int(checkpoint_gap)} s after the archive volume")
                    failing = True
        print(f"{project}: " + ("; ".join(findings) if findings else "coupled"))

    if not projects:
        print("no compose project with AuthorityDb, archive or checkpoint volumes found")
    return 1 if failing else 0


if __name__ == "__main__":
    sys.exit(main())
