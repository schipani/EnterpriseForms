using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EnterpriseForms.Infrastructure.Persistence.Conventions;

/// <summary>Walks a compiled EF model and reports every naming violation. Run by the architecture tests; CI fails on any violation.</summary>
public static class ModelNamingValidator
{
    public static IReadOnlyList<NamingViolation> Validate(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var violations = new List<NamingViolation>();

        foreach (var entity in model.GetEntityTypes())
        {
            var tableName = entity.GetTableName();
            if (tableName is null)
            {
                continue; // owned/queryless types map elsewhere
            }

            if (!DatabaseColumnNamingRules.IsSnakeCase(tableName))
            {
                violations.Add(new NamingViolation(tableName, "TABLE-SNAKE", "table must be lowercase snake_case"));
            }

            var storeObject = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
            var primaryKey = entity.FindPrimaryKey();
            foreach (var property in entity.GetProperties())
            {
                var columnName = property.GetColumnName(storeObject) ?? property.Name;
                var isKey = property.IsPrimaryKey() || property.IsForeignKey();
                var violation = DatabaseColumnNamingRules.EvaluateColumn(tableName, columnName, isKey);
                if (violation is not null)
                {
                    violations.Add(violation);
                }
            }

            if (primaryKey is { Properties.Count: 1 })
            {
                var pkColumn = primaryKey.Properties[0].GetColumnName(storeObject) ?? primaryKey.Properties[0].Name;
                var pkViolation = DatabaseColumnNamingRules.EvaluatePrimaryKey(tableName, pkColumn);
                if (pkViolation is not null)
                {
                    violations.Add(pkViolation);
                }
            }
        }

        return violations;
    }
}
