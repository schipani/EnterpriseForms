using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Persistence;

/// <summary>Explicit PascalCase → snake_case mapping (CLAUDE.md §0 rules 4–5; Phase 0 decision P0-14 resolved: explicit HasColumnName, no naming package).</summary>
public static class SnakeCaseNaming
{
    public static string ToSnakeCase(string pascalCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pascalCase);
        var builder = new StringBuilder(pascalCase.Length + 8);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var ch = pascalCase[i];
            if (char.IsUpper(ch))
            {
                if (i > 0 && (!char.IsUpper(pascalCase[i - 1]) || (i + 1 < pascalCase.Length && char.IsLower(pascalCase[i + 1]))))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    public static EntityTypeBuilder<TEntity> MapSnakeCase<TEntity>(this EntityTypeBuilder<TEntity> builder, string tableName)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable(tableName);
        foreach (var property in typeof(TEntity).GetProperties().Where(p => p.CanWrite && p.GetMethod?.IsPublic == true))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type.IsPrimitive || type == typeof(string) || type == typeof(Guid) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(byte[]) || type == typeof(decimal))
            {
                builder.Property(property.Name).HasColumnName(ToSnakeCase(property.Name));
            }
        }

        return builder;
    }
}
