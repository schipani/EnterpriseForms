#pragma warning disable CA1720 // Identifiers intentionally mirror the wire type names used in the row-integrity vectors and format document.
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace EnterpriseForms.Integrity.Canonical;

/// <summary>Typed value for one signed column. A C# <c>null</c> in the value map encodes the explicit NULL marker.</summary>
public abstract record RowValue
{
    public abstract RowColumnType Type { get; }

    public static RowValue Boolean(bool value) => new BooleanValue(value);

    public static RowValue Int32(int value) => new Int32Value(value);

    public static RowValue Int64(long value) => new Int64Value(value);

    public static RowValue Decimal(decimal value) => DecimalValue.From(value);

    /// <summary>Parses an invariant decimal literal (digits, optional sign, fraction and exponent) with arbitrary precision.</summary>
    public static RowValue Decimal(string invariantLiteral) => DecimalValue.Parse(invariantLiteral);

    public static RowValue Date(DateOnly value) => new DateValue(value);

    public static RowValue Timestamp(DateTimeOffset value) => new TimestampValue(value);

    public static RowValue Uuid(Guid value) => new UuidValue(value);

    public static RowValue Text(string value) => new TextValue(value);

    public static RowValue Bytes(ReadOnlyMemory<byte> value) => new BytesValue(value);

    public static RowValue Json(JsonElement value) => new JsonValue(value.Clone());

    public sealed record BooleanValue(bool Value) : RowValue
    {
        public override RowColumnType Type => RowColumnType.Boolean;
    }

    public sealed record Int32Value(int Value) : RowValue
    {
        public override RowColumnType Type => RowColumnType.Int32;
    }

    public sealed record Int64Value(long Value) : RowValue
    {
        public override RowColumnType Type => RowColumnType.Int64;
    }

    /// <summary>Normalized decimal: minimal non-negative scale, zero is (0, 0).</summary>
    public sealed record DecimalValue(BigInteger Coefficient, int Scale) : RowValue
    {
        public override RowColumnType Type => RowColumnType.Decimal;

        public static DecimalValue From(decimal value)
        {
            var bits = decimal.GetBits(value);
            var scale = (bits[3] >> 16) & 0xFF;
            var negative = (bits[3] & unchecked((int)0x80000000)) != 0;
            var coefficient = new BigInteger((uint)bits[2]) << 64 | new BigInteger((uint)bits[1]) << 32 | new BigInteger((uint)bits[0]);
            return Normalize(negative ? -coefficient : coefficient, scale);
        }

        public static DecimalValue Parse(string literal)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(literal);
            var text = literal.Trim();
            var negative = false;
            if (text[0] is '+' or '-')
            {
                negative = text[0] == '-';
                text = text[1..];
            }

            var exponent = 0;
            var exponentIndex = text.IndexOfAny(['e', 'E']);
            if (exponentIndex >= 0)
            {
                exponent = int.Parse(text[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                text = text[..exponentIndex];
            }

            var dot = text.IndexOf('.', StringComparison.Ordinal);
            var fraction = dot >= 0 ? text[(dot + 1)..] : string.Empty;
            var digits = dot >= 0 ? text[..dot] + fraction : text;
            if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
            {
                throw new IntegrityFormatException(IntegrityFormatErrorCode.DecimalRange, "invalid decimal literal");
            }

            var coefficient = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
            return Normalize(negative ? -coefficient : coefficient, fraction.Length - exponent);
        }

        private static DecimalValue Normalize(BigInteger coefficient, int scale)
        {
            if (coefficient.IsZero)
            {
                return new DecimalValue(BigInteger.Zero, 0);
            }

            while (scale > 0 && coefficient % 10 == 0)
            {
                coefficient /= 10;
                scale--;
            }

            if (scale < 0)
            {
                coefficient *= BigInteger.Pow(10, -scale);
                scale = 0;
            }

            return new DecimalValue(coefficient, scale);
        }
    }

    public sealed record DateValue(DateOnly Value) : RowValue
    {
        public override RowColumnType Type => RowColumnType.Date;
    }

    public sealed record TimestampValue(DateTimeOffset Value) : RowValue
    {
        public override RowColumnType Type => RowColumnType.TimestampUtc;
    }

    public sealed record UuidValue(Guid Value) : RowValue
    {
        public override RowColumnType Type => RowColumnType.Uuid;
    }

    public sealed record TextValue(string Value) : RowValue
    {
        public override RowColumnType Type => RowColumnType.Text;
    }

    public sealed record BytesValue(ReadOnlyMemory<byte> Value) : RowValue
    {
        public override RowColumnType Type => RowColumnType.Bytes;
    }

    public sealed record JsonValue(JsonElement Value) : RowValue
    {
        public override RowColumnType Type => RowColumnType.Json;
    }
}
