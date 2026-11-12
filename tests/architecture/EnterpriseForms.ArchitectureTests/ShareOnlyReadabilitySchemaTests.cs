using System.Text.RegularExpressions;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// C-141 (UF-25, U-1b): the share-only readability candidates are read from the Authority's canonical edges with relation names
/// that mirror authority.schema.zed. If the schema ever gives <c>field_item#read</c> (or <c>record_share#read</c>, or team
/// participation) another path, the reader's SQL would silently miss it and records would drop out of results. These fitness
/// functions pin the exact definitions the reader relies on, and the literals it uses, so such a change fails the build and
/// forces the reader (and its RQ-1 differential) to be revised with it.
/// </summary>
public sealed class ShareOnlyReadabilitySchemaTests
{
    private static string Schema() => File.ReadAllText(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Infrastructure", "SpiceDb", "authority.schema.zed"));

    private static string Reader() => File.ReadAllText(RepositoryPaths.Combine("src", "authority", "AuthorizationAuthority.Infrastructure", "Persistence", "SearchQueryReader.cs"));

    private static string Definition(string schema, string name)
    {
        var match = Regex.Match(schema, @"definition\s+" + name + @"\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(match.Success, "definition " + name + " not found");
        return Regex.Replace(match.Groups["body"].Value, @"//[^\n]*", string.Empty);
    }

    private static string[] Lines(string body) =>
        [.. body.Split('\n').Select(l => Regex.Replace(l.Trim(), @"\s+", " ")).Where(l => l.Length > 0)];

    [Fact]
    public void TheSchemaPathsToFieldItemReadAreExactlyTheOnesTheReaderEnumerates()
    {
        var schema = Schema();
        Assert.Equal(
            ["relation parent_record: form_record", "relation shared_via: record_share", "permission read = parent_record->effective_owner + shared_via->read",
             "permission edit = parent_record->effective_owner + shared_via->edit", "permission delegate = parent_record->effective_owner + shared_via->delegate"],
            Lines(Definition(schema, "field_item")));
        Assert.Contains("permission read = reader + contributor + manager", Lines(Definition(schema, "record_share")));
        Assert.Contains("relation reader: user | team#participant", Lines(Definition(schema, "record_share")));
        Assert.Contains("relation contributor: user | team#participant", Lines(Definition(schema, "record_share")));
        Assert.Contains("relation manager: user | team#participant", Lines(Definition(schema, "record_share")));
        Assert.Equal(["relation member: user", "permission participant = member"], Lines(Definition(schema, "team")));
        // A share-only record is one the caller does not effectively own; the parent_record branch therefore never applies to it.
        Assert.Contains("permission read = effective_owner", Lines(Definition(schema, "form_record")));
    }

    [Fact]
    public void TheReaderUsesTheSameRelationLiterals()
    {
        var reader = Reader();
        Assert.Contains("ShareReadRoles = [\"reader\", \"contributor\", \"manager\"]", reader, StringComparison.Ordinal);
        foreach (var literal in new[] { "m.relationship_name = 'member'", "m.resource_type_name = 'team'", "r.resource_type_name = 'record_share'", "r.subject_relation_name = 'participant'",
                     "f.relationship_name = 'shared_via'", "f.resource_type_name = 'form_record'", "i.relationship_name = 'shared_via'", "i.resource_type_name = 'field_item'", "removed_by_security_command_id IS NULL" })
        {
            Assert.Contains(literal, reader, StringComparison.Ordinal);
        }
    }
}
