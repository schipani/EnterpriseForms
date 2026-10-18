namespace EnterpriseForms.Integrity;

/// <summary>Stable error codes for canonicalization and manifest failures. Callers fail closed on every code.</summary>
public enum IntegrityFormatErrorCode
{
    UnknownItemKind,
    DuplicateItemKey,
    KindCodeMismatch,
    InvalidSignatureLength,
    InvalidDigestLength,
    MixedRevisionOrOperation,
    RegistryNotSorted,
    InvalidColumnName,
    UnregisteredColumn,
    MissingColumn,
    NullNotPermitted,
    TypeMismatch,
    JsonFloatNotPermitted,
    JsonIntegerRange,
    JsonKeyNotAscii,
    JsonUnsupportedNode,
    TimestampPrecision,
    DecimalRange,
    TextInvalidUnicode,
    SchemaVersionRange,
    KeyVersionRange,
    RowRevisionRange,
    IdentifierTooLong,
    UnknownSchemaVersion,
}

/// <summary>Thrown for any non-canonical input. Never caught to continue a protected operation.</summary>
public sealed class IntegrityFormatException : Exception
{
    public IntegrityFormatException(IntegrityFormatErrorCode code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public IntegrityFormatErrorCode Code { get; }
}
