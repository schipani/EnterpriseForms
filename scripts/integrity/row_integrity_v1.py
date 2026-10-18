#!/usr/bin/env python3
"""Reference implementation of the canonical row HMAC format `DynamicForms/RowHmac/v1`.

Normative source: docs/dynamic-forms/row-integrity-format.md (which this module must match
byte for byte). CLAUDE.md section 4L.2 lists the mandatory properties.

Design goals: language independence, explicit lengths, explicit NULL markers, no reliance on
default JSON serialization, column enumeration order, locale or time-zone display settings.
"""
from __future__ import annotations

import datetime as dt
import decimal
import hashlib
import hmac
import json
import struct
import unicodedata
import uuid
from dataclasses import dataclass

DOMAIN = b"DynamicForms/RowHmac/v1"
FRAMING_MARKER = b"\x10"

# Type tags (one byte). Null is a value state, not a type: a nullable column of any type is
# encoded with TAG_NULL when the value is absent.
TAG_NULL = 0x00
TAG_BOOLEAN = 0x01
TAG_INT32 = 0x02
TAG_INT64 = 0x03
TAG_DECIMAL = 0x04
TAG_DATE = 0x05
TAG_TIMESTAMP_UTC = 0x06
TAG_UUID = 0x07
TAG_TEXT = 0x08
TAG_BYTES = 0x09
TAG_JSON = 0x0A

TYPE_NAMES = {
    "Boolean": TAG_BOOLEAN,
    "Int32": TAG_INT32,
    "Int64": TAG_INT64,
    "Decimal": TAG_DECIMAL,
    "Date": TAG_DATE,
    "TimestampUtc": TAG_TIMESTAMP_UTC,
    "Uuid": TAG_UUID,
    "Text": TAG_TEXT,
    "Bytes": TAG_BYTES,
    "Json": TAG_JSON,
}

EPOCH_DATE = dt.date(1970, 1, 1)
EPOCH_DT = dt.datetime(1970, 1, 1, tzinfo=dt.timezone.utc)
MAX_SAFE_JSON_INT = (1 << 53) - 1


class RowFormatError(ValueError):
    """Any violation of the canonical contract. Signing/verification must fail closed."""


@dataclass(frozen=True)
class SignedColumn:
    column_name: str
    type_name: str
    nullable: bool


@dataclass(frozen=True)
class RowSchema:
    """One (entity_kind, schema_version) registry entry. Columns must be ASCII-sorted."""

    entity_kind: str
    schema_version: int
    columns: tuple[SignedColumn, ...]

    def __post_init__(self) -> None:
        names = [c.column_name for c in self.columns]
        if names != sorted(names) or len(set(names)) != len(names):
            raise RowFormatError("signed columns must be unique and ASCII-sorted by column_name")
        for n in names:
            if not n.isascii() or not n or n.lower() != n:
                raise RowFormatError(f"column name must be lowercase ASCII snake_case: {n!r}")


def _ascii_len16(value: str) -> bytes:
    data = value.encode("ascii")
    if len(data) > 0xFFFF:
        raise RowFormatError("identifier too long")
    return struct.pack(">H", len(data)) + data


def _uuid(value: str | uuid.UUID) -> bytes:
    return uuid.UUID(str(value)).bytes


def encode_decimal(value: decimal.Decimal) -> bytes:
    """Normalized (minimal scale, scale >= 0) two's-complement coefficient + Int32 scale."""
    if not value.is_finite():
        raise RowFormatError("decimal must be finite")
    sign, digits, exponent = value.as_tuple()
    coefficient = int("".join(map(str, digits))) if digits else 0
    if sign:
        coefficient = -coefficient
    scale = -exponent
    # Normalize: remove trailing zeros while scale > 0; zero becomes (0, 0).
    if coefficient == 0:
        scale = 0
    else:
        while scale > 0 and coefficient % 10 == 0:
            coefficient //= 10
            scale -= 1
    if scale < 0:
        # Positive exponents (e.g. 1E+2) are expanded into the coefficient.
        coefficient *= 10 ** (-scale)
        scale = 0
    if scale > 0x7FFFFFFF:
        raise RowFormatError("decimal scale out of range")
    length = max(1, (coefficient.bit_length() + 8) // 8)  # minimal two's complement length
    coef_bytes = coefficient.to_bytes(length, "big", signed=True)
    # Ensure minimal encoding (strip redundant sign-extension bytes).
    while len(coef_bytes) > 1 and (
        (coef_bytes[0] == 0x00 and coef_bytes[1] < 0x80) or (coef_bytes[0] == 0xFF and coef_bytes[1] >= 0x80)
    ):
        coef_bytes = coef_bytes[1:]
    return struct.pack(">i", scale) + struct.pack(">H", len(coef_bytes)) + coef_bytes


def encode_text(value: str) -> bytes:
    if any(0xD800 <= ord(ch) <= 0xDFFF for ch in value):
        raise RowFormatError("text contains unpaired surrogate")
    data = unicodedata.normalize("NFC", value).encode("utf-8")
    return struct.pack(">I", len(data)) + data


def canonical_json(value: object) -> bytes:
    """RFC 8785 subset accepted by v1: objects with ASCII keys, arrays, strings, booleans,
    null and integers within +/-(2^53-1). Floats and non-ASCII keys are rejected."""

    def check(node: object) -> None:
        if isinstance(node, bool) or node is None or isinstance(node, str):
            return
        if isinstance(node, float):
            raise RowFormatError("JSON floating-point numbers are not permitted in v1")
        if isinstance(node, int):
            if abs(node) > MAX_SAFE_JSON_INT:
                raise RowFormatError("JSON integer exceeds 2^53-1")
            return
        if isinstance(node, list):
            for item in node:
                check(item)
            return
        if isinstance(node, dict):
            for key, item in node.items():
                if not isinstance(key, str) or not key.isascii():
                    raise RowFormatError("JSON object keys must be ASCII strings in v1")
                check(item)
            return
        raise RowFormatError(f"unsupported JSON node type {type(node).__name__}")

    check(value)
    text = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    data = text.encode("utf-8")
    return struct.pack(">I", len(data)) + data


def encode_value(column: SignedColumn, value: object) -> bytes:
    tag = TYPE_NAMES.get(column.type_name)
    if tag is None:
        raise RowFormatError(f"unknown type {column.type_name}")
    if value is None:
        if not column.nullable:
            raise RowFormatError(f"column {column.column_name} is not nullable")
        return bytes([TAG_NULL])
    if tag == TAG_BOOLEAN:
        if not isinstance(value, bool):
            raise RowFormatError("boolean expected")
        return bytes([tag, 1 if value else 0])
    if tag == TAG_INT32:
        if isinstance(value, bool) or not isinstance(value, int) or not -(1 << 31) <= value < (1 << 31):
            raise RowFormatError("int32 expected")
        return bytes([tag]) + struct.pack(">i", value)
    if tag == TAG_INT64:
        if isinstance(value, bool) or not isinstance(value, int) or not -(1 << 63) <= value < (1 << 63):
            raise RowFormatError("int64 expected")
        return bytes([tag]) + struct.pack(">q", value)
    if tag == TAG_DECIMAL:
        if isinstance(value, float):
            raise RowFormatError("decimal must be supplied as Decimal or string, never float")
        return bytes([tag]) + encode_decimal(decimal.Decimal(value))
    if tag == TAG_DATE:
        if isinstance(value, str):
            value = dt.date.fromisoformat(value)
        if not isinstance(value, dt.date) or isinstance(value, dt.datetime):
            raise RowFormatError("date expected")
        return bytes([tag]) + struct.pack(">i", (value - EPOCH_DATE).days)
    if tag == TAG_TIMESTAMP_UTC:
        if isinstance(value, str):
            value = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        if not isinstance(value, dt.datetime) or value.tzinfo is None:
            raise RowFormatError("timestamp must carry an explicit UTC offset")
        micros = round((value - EPOCH_DT).total_seconds() * 1_000_000)
        # Recompute exactly from components to avoid float rounding.
        delta = value.astimezone(dt.timezone.utc) - EPOCH_DT
        micros = delta.days * 86_400_000_000 + delta.seconds * 1_000_000 + delta.microseconds
        return bytes([tag]) + struct.pack(">q", micros)
    if tag == TAG_UUID:
        return bytes([tag]) + _uuid(str(value))
    if tag == TAG_TEXT:
        if not isinstance(value, str):
            raise RowFormatError("text expected")
        return bytes([tag]) + encode_text(value)
    if tag == TAG_BYTES:
        if isinstance(value, str):
            value = bytes.fromhex(value)
        return bytes([tag]) + struct.pack(">I", len(value)) + bytes(value)
    if tag == TAG_JSON:
        return bytes([tag]) + canonical_json(value)
    raise RowFormatError("unreachable")


def canonical_row_bytes(
    schema: RowSchema,
    *,
    key_version: int,
    organization_id: str,
    primary_key_id: str,
    row_revision: int,
    values: dict[str, object],
) -> bytes:
    if set(values) != {c.column_name for c in schema.columns}:
        raise RowFormatError("values must supply exactly the registered signed columns")
    if not 1 <= schema.schema_version <= 0xFFFF:
        raise RowFormatError("schema_version out of range")
    if not 1 <= key_version <= 0xFFFFFFFF:
        raise RowFormatError("key_version out of range")
    if not 1 <= row_revision <= (1 << 64) - 1:
        raise RowFormatError("row_revision must be >= 1")
    out = bytearray()
    out += FRAMING_MARKER
    out += _ascii_len16(DOMAIN.decode("ascii"))
    out += struct.pack(">H", schema.schema_version)
    out += struct.pack(">I", key_version)
    out += _ascii_len16(schema.entity_kind)
    out += _uuid(organization_id)
    out += _uuid(primary_key_id)
    out += struct.pack(">Q", row_revision)
    out += struct.pack(">I", len(schema.columns))
    for column in schema.columns:
        out += _ascii_len16(column.column_name)
        out += encode_value(column, values[column.column_name])
    return bytes(out)


def canonical_payload_digest(canonical: bytes) -> bytes:
    return hashlib.sha256(canonical).digest()


def row_signature(canonical: bytes, key: bytes) -> bytes:
    """HMAC-SHA-256 over canonical bytes. Production keys live only in the signing authority."""
    if len(key) < 32:
        raise RowFormatError("integrity key must be at least 256 bits")
    return hmac.new(key, canonical, hashlib.sha256).digest()
