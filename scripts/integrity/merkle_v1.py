#!/usr/bin/env python3
"""Reference implementation of the Merkle record manifest v1 wire format.

Normative source: trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md, sections 2.1 and 2.2.

This module is intentionally dependency-free and written for clarity, so a reviewer can
compare each byte against the protocol text. It is one of three independent implementations
(C#: src/shared/EnterpriseForms.Integrity, Node: scripts/integrity/verify-vectors.mjs).

The row signatures used in the published vectors are INVENTED FIXED FIXTURES, not real
HMAC tags, exactly as the protocol's section 8 requires. Nothing here grants permission to
bypass signature verification in a real system.
"""
from __future__ import annotations

import hashlib
import struct
import uuid
from dataclasses import dataclass

LEAF_DOMAIN = b"DynamicForms/MerkleLeaf/v1"
NODE_DOMAIN = b"DynamicForms/MerkleNode/v1"
EMPTY_DOMAIN = b"DynamicForms/MerkleEmpty/v1"
INDEX_DOMAIN = b"DynamicForms/ManifestIndex/v1"

ITEM_KEY_LENGTH = 17  # 1-byte kind code + 16-byte RFC 4122 UUID
HASH_LENGTH = 32
UINT64_MAX = (1 << 64) - 1

KIND_CODES = {
    "RecordHeader": 1,
    "RepeatedGroup": 2,
    "FieldValue": 3,
    "FieldSelection": 4,
    "Attachment": 5,
    "AttachmentLifecycle": 6,
}


class MerkleFormatError(ValueError):
    """Raised on any non-canonical or invalid input. Callers must fail closed."""


def uuid_bytes(value: str | uuid.UUID) -> bytes:
    """RFC 4122 network byte order (identical to the canonical hex string order)."""
    return uuid.UUID(str(value)).bytes


def item_key(kind_code: int, item_id: str | uuid.UUID) -> bytes:
    if kind_code not in KIND_CODES.values():
        raise MerkleFormatError(f"unknown item kind code {kind_code}")
    return bytes([kind_code]) + uuid_bytes(item_id)


@dataclass(frozen=True)
class LeafInput:
    kind_code: int
    item_id: str
    row_signature: bytes
    canonical_payload_digest: bytes
    committed_content_revision: int
    committed_operation_id: str

    @property
    def key(self) -> bytes:
        return item_key(self.kind_code, self.item_id)


def leaf_bytes(leaf: LeafInput) -> bytes:
    """Exact framing from protocol section 2.1."""
    if len(leaf.row_signature) != HASH_LENGTH:
        raise MerkleFormatError("row_signature must be exactly 32 bytes")
    if len(leaf.canonical_payload_digest) != HASH_LENGTH:
        raise MerkleFormatError("canonical_payload_digest must be exactly 32 bytes")
    if not 0 <= leaf.committed_content_revision <= UINT64_MAX:
        raise MerkleFormatError("committed_content_revision out of UInt64 range")
    key = leaf.key
    if key[0] != leaf.kind_code:
        raise MerkleFormatError("item_key kind code must equal trailing item_kind_code")
    return (
        b"\x00"
        + LEAF_DOMAIN
        + struct.pack(">H", ITEM_KEY_LENGTH)
        + key
        + leaf.row_signature
        + leaf.canonical_payload_digest
        + bytes([leaf.kind_code])
        + struct.pack(">Q", leaf.committed_content_revision)
        + uuid_bytes(leaf.committed_operation_id)
    )


def leaf_hash(leaf: LeafInput) -> bytes:
    return hashlib.sha256(leaf_bytes(leaf)).digest()


def node_hash(left: bytes, right: bytes) -> bytes:
    if len(left) != HASH_LENGTH or len(right) != HASH_LENGTH:
        raise MerkleFormatError("node children must be 32-byte hashes")
    return hashlib.sha256(b"\x01" + NODE_DOMAIN + left + right).digest()


def empty_root() -> bytes:
    return hashlib.sha256(b"\x02" + EMPTY_DOMAIN).digest()


def sort_leaves(leaves: list[LeafInput]) -> list[LeafInput]:
    """Sort by unsigned bytes of item_key; reject duplicates and mixed operation/revision."""
    ordered = sorted(leaves, key=lambda l: l.key)
    for a, b in zip(ordered, ordered[1:]):
        if a.key == b.key:
            raise MerkleFormatError("duplicate item_key in leaf set")
    if ordered:
        rev = ordered[0].committed_content_revision
        op = ordered[0].committed_operation_id.lower()
        for l in ordered:
            if l.committed_content_revision != rev or l.committed_operation_id.lower() != op:
                raise MerkleFormatError("all leaves of one manifest share one revision and operation")
    return ordered


def build_levels(sorted_leaves: list[LeafInput]) -> list[list[bytes]]:
    """Bottom-up levels. Odd trailing node is promoted unchanged (never duplicated)."""
    if not sorted_leaves:
        return [[empty_root()]]
    level = [leaf_hash(l) for l in sorted_leaves]
    levels = [level]
    while len(level) > 1:
        nxt = []
        for i in range(0, len(level) - 1, 2):
            nxt.append(node_hash(level[i], level[i + 1]))
        if len(level) % 2 == 1:
            nxt.append(level[-1])
        level = nxt
        levels.append(level)
    return levels


def merkle_root(sorted_leaves: list[LeafInput]) -> bytes:
    return build_levels(sorted_leaves)[-1][0]


def leaf_index_digest(sorted_leaves: list[LeafInput]) -> bytes:
    body = b"\x03" + INDEX_DOMAIN + struct.pack(">Q", len(sorted_leaves))
    for l in sorted_leaves:
        body += struct.pack(">H", ITEM_KEY_LENGTH) + l.key + leaf_hash(l)
    return hashlib.sha256(body).digest()
