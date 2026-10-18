#pragma warning disable CA1720 // Identifiers intentionally mirror the wire type names used in the row-integrity vectors and format document.
namespace EnterpriseForms.Integrity.Canonical;

/// <summary>Logical column types of the canonical row format v1. One-byte tags are fixed by the format document.</summary>
public enum RowColumnType : byte
{
    Boolean = 0x01,
    Int32 = 0x02,
    Int64 = 0x03,
    Decimal = 0x04,
    Date = 0x05,
    TimestampUtc = 0x06,
    Uuid = 0x07,
    Text = 0x08,
    Bytes = 0x09,
    Json = 0x0A,
}

/// <summary>A signed column entry in the (entity_kind, schema_version) registry.</summary>
public sealed record RowSignedColumn(string ColumnName, RowColumnType Type, bool Nullable);

/// <summary>
/// Immutable signed-column registry entry. Columns must be unique, lowercase ASCII snake_case and
/// ordinal-sorted so every implementation enumerates them identically.
/// </summary>
public sealed class RowSchemaDefinition
{
    public RowSchemaDefinition(string entityKind, int schemaVersion, IReadOnlyList<RowSignedColumn> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKind);
        ArgumentNullException.ThrowIfNull(columns);
        if (schemaVersion is < 1 or > ushort.MaxValue)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.SchemaVersionRange, "schema_version must be 1..65535");
        }

        ValidateIdentifier(entityKind);
        for (var i = 0; i < columns.Count; i++)
        {
            ValidateIdentifier(columns[i].ColumnName);
            if (i > 0 && string.CompareOrdinal(columns[i - 1].ColumnName, columns[i].ColumnName) >= 0)
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.RegistryNotSorted, $"'{columns[i].ColumnName}' breaks ordinal order or duplicates");
            }
        }

        EntityKind = entityKind;
        SchemaVersion = schemaVersion;
        Columns = columns;
    }

    public string EntityKind { get; }

    public int SchemaVersion { get; }

    public IReadOnlyList<RowSignedColumn> Columns { get; }

    private static void ValidateIdentifier(string name)
    {
        if (name.Length == 0 || name.Length > ushort.MaxValue)
        {
            throw new IntegrityFormatException(IntegrityFormatErrorCode.IdentifierTooLong, name);
        }

        foreach (var ch in name)
        {
            if (!(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.InvalidColumnName, $"'{name}' must be lowercase ASCII snake_case");
            }
        }
    }
}
