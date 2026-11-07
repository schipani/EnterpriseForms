using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using EnterpriseForms.Integrity;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;
using EnterpriseForms.Integrity.Search;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>One derived posting before persistence: the field, its source leaf and the bound value.</summary>
public sealed record DerivedPosting(Guid FieldDefinitionId, Guid FieldSearchApprovalId, byte[] ManifestItemKey, Guid ManifestItemId, string EntityKind, string ValueType, byte[] SourceCanonicalBytes, byte[] SourceCanonicalDigest, SearchSealValue Value);

/// <summary>The output of <see cref="SearchPostingDerivationV1.Derive"/> for one record at one verified head.</summary>
public sealed record DerivedRecordContent(string RecordStatusName, IReadOnlyList<DerivedPosting> Postings, IReadOnlyList<SearchSealAnomaly> Anomalies);

/// <summary>
/// IX-1 (design R1 §C.1, revision 2 §4): <c>SearchPostingDerivation/v1</c>, a pure function of the canonical bytes the content
/// commit has just verified (HMAC, leaf set, root) and the Authority-held Active approvals of the record's form.
/// <list type="bullet">
/// <item>Only top-level values (no repeating-group occurrence) of a field with an Active approval are indexed.</item>
/// <item>An encrypted leaf on an approved field yields an <c>EncryptedValuePresent</c> anomaly and never a posting: neither
/// ciphertext nor anything derived from it is indexed.</item>
/// <item>A leaf whose storage type does not match the approval, or bytes that do not decode, abort the commit (IX-D7).</item>
/// </list>
/// The approval's recorded classification must be Public or Internal and its protection Standard; anything else is refused.
/// </summary>
public static class SearchPostingDerivationV1
{
    public const string Version = "SearchPostingDerivation/v1";

    private static readonly Dictionary<string, string> EntityKindByValueType = new(StringComparer.Ordinal)
    {
        ["String"] = "field_string_values",
        ["Integer"] = "field_integer_values",
        ["Decimal"] = "field_decimal_values",
        ["Date"] = "field_date_values",
        ["DateTime"] = "field_date_time_values",
        ["Boolean"] = "field_boolean_values",
        ["Option"] = "field_option_values",
    };

    public const string EncryptedEntityKind = "field_encrypted_values";
    public const string HeaderEntityKind = "form_records";

    public static bool IsIndexableValueType(string valueType) => EntityKindByValueType.ContainsKey(valueType);

    public static DerivedRecordContent Derive(Guid organizationId, Guid formDefinitionId, Guid formRecordId, IReadOnlyList<StagedContentItem> verifiedItems, IReadOnlyCollection<FieldSearchApproval> activeApprovals)
    {
        ArgumentNullException.ThrowIfNull(verifiedItems);
        ArgumentNullException.ThrowIfNull(activeApprovals);
        var byField = new Dictionary<Guid, FieldSearchApproval>();
        foreach (var approval in activeApprovals)
        {
            if (approval.ApprovalStateName != FieldSearchApprovalStates.Active || approval.OrganizationId != organizationId || approval.FormDefinitionId != formDefinitionId)
            {
                throw Failure("an approval outside the record's organization or form, or not Active, was supplied");
            }

            if (approval.ApprovedFieldClassification is not ("Public" or "Internal") || approval.ApprovedProtectionMode != "Standard" || !IsIndexableValueType(approval.ApprovedFieldDataType))
            {
                throw Failure("an approval that is not Public/Internal, Standard and of a searchable type was supplied");
            }

            if (!byField.TryAdd(approval.FieldDefinitionId, approval))
            {
                throw Failure("two Active approvals for one field");
            }
        }

        string? recordStatus = null;
        var postings = new List<DerivedPosting>();
        var anomalies = new HashSet<(Guid Field, string Kind)>();
        foreach (var item in verifiedItems)
        {
            byte[] bytes;
            RowCanonicalDocument document;
            try
            {
                bytes = Convert.FromBase64String(item.CanonicalBytesBase64);
                document = RowCanonicalDecoder.Decode(bytes);
            }
            catch (Exception ex) when (ex is FormatException or IntegrityFormatException)
            {
                throw Failure("a verified item does not decode");
            }

            if (document.Header.OrganizationId != organizationId)
            {
                throw Failure("item bound to another organization");
            }

            switch (item.ItemKind)
            {
                case ManifestItemKind.RecordHeader:
                    if (document.Header.EntityKind != HeaderEntityKind || Uuid(document, "form_definition_id") != formDefinitionId)
                    {
                        throw Failure("the record header does not belong to the record's registered form");
                    }

                    recordStatus = document.Column("record_status")?.AsText() ?? throw Failure("the record header has no status");
                    break;
                case ManifestItemKind.FieldValue:
                case ManifestItemKind.FieldSelection:
                    var field = Uuid(document, "field_definition_id");
                    if (!byField.TryGetValue(field, out var approval))
                    {
                        break; // not approved: never indexed
                    }

                    if (Uuid(document, "form_record_id") != formRecordId)
                    {
                        throw Failure("a value item bound to another record");
                    }

                    var group = document.Column("form_record_group_id") ?? throw Failure("a value item without a group column");
                    if (!group.IsNull)
                    {
                        break; // repeating-group occurrences are not indexed in V2 (D6)
                    }

                    if (document.Header.EntityKind == EncryptedEntityKind)
                    {
                        anomalies.Add((field, SearchAnomalyKinds.EncryptedValuePresent));
                        break;
                    }

                    var expected = EntityKindByValueType[approval.ApprovedFieldDataType];
                    if (document.Header.EntityKind != expected || (item.ItemKind == ManifestItemKind.FieldSelection) != (approval.ApprovedFieldDataType == "Option"))
                    {
                        throw Failure("the stored type of an approved field does not match its approval");
                    }

                    if (Uuid(document, "field_value_item_id") != item.ItemId)
                    {
                        throw Failure("a value item whose item id differs from its manifest item");
                    }

                    var key = MerkleManifestV1.ItemKey((MerkleItemKind)(byte)item.ItemKind, item.ItemId);
                    postings.Add(new DerivedPosting(field, approval.FieldSearchApprovalId, key, item.ItemId, document.Header.EntityKind, approval.ApprovedFieldDataType, bytes, SHA256.HashData(bytes), Value(document, approval.ApprovedFieldDataType)));
                    break;
                default:
                    break; // groups, attachments and lifecycle events are never indexed
            }
        }

        if (recordStatus is null)
        {
            throw Failure("the verified snapshot has no record header");
        }

        // An encrypted leaf on a field excludes that field for the record entirely (no partial plaintext beside ciphertext).
        foreach (var (encryptedField, _) in anomalies)
        {
            postings.RemoveAll(p => p.FieldDefinitionId == encryptedField);
        }

        return new DerivedRecordContent(recordStatus, postings, anomalies.Select(a => new SearchSealAnomaly(a.Field, a.Kind)).ToList());
    }

    /// <summary>
    /// Sweep (S-B): re-derives the bound value of a stored posting from its stored source canonical bytes, exactly as the commit
    /// did. Returns the decoded document so the caller can check its bindings; throws the derivation failure on any mismatch.
    /// </summary>
    public static (SearchSealValue Value, RowCanonicalDocument Document) ValueFromSource(byte[] sourceCanonicalBytes, string valueType)
    {
        ArgumentNullException.ThrowIfNull(sourceCanonicalBytes);
        RowCanonicalDocument document;
        try
        {
            document = RowCanonicalDecoder.Decode(sourceCanonicalBytes);
        }
        catch (IntegrityFormatException)
        {
            throw Failure("stored source bytes do not decode");
        }

        if (!EntityKindByValueType.TryGetValue(valueType, out var kind) || document.Header.EntityKind != kind)
        {
            throw Failure("stored source bytes are not of the posting's type");
        }

        return (Value(document, valueType), document);
    }

    public static Guid FieldOf(RowCanonicalDocument document) => Uuid(document, "field_definition_id");

    public static Guid RecordOf(RowCanonicalDocument document) => Uuid(document, "form_record_id");

    public static Guid ItemOf(RowCanonicalDocument document) => Uuid(document, "field_value_item_id");

    private static SearchSealValue Value(RowCanonicalDocument document, string valueType)
    {
        try
        {
            return valueType switch
            {
                "String" => new SearchSealValue.NormalizedString(SearchValueNormalizationV1.NormalizeString(Required(document, "string_value").AsText() ?? throw Failure("string value missing"))),
                "Integer" => new SearchSealValue.IntegerValue(Required(document, "integer_value").AsInt64() ?? throw Failure("integer value missing")),
                "Decimal" => Required(document, "decimal_value").AsDecimalParts() is { } parts ? new SearchSealValue.DecimalParts(parts.Scale, parts.Coefficient) : throw Failure("decimal value missing"),
                "Date" => new SearchSealValue.DateValue(Required(document, "date_value").AsDate() ?? throw Failure("date value missing")),
                "DateTime" => new SearchSealValue.Timestamp(Required(document, "date_time_value").AsTimestamp() ?? throw Failure("date-time value missing")),
                "Boolean" => new SearchSealValue.BooleanValue(Required(document, "boolean_value").AsBoolean() ?? throw Failure("boolean value missing")),
                "Option" => new SearchSealValue.OptionValue(Required(document, "field_option_id").AsUuid() ?? throw Failure("option id missing")),
                _ => throw Failure("unsupported value type"),
            };
        }
        catch (IntegrityFormatException)
        {
            throw Failure("a value does not decode");
        }
        catch (OverflowException)
        {
            throw Failure("a value is out of range");
        }
    }

    private static RowCanonicalColumn Required(RowCanonicalDocument document, string name) => document.Column(name) ?? throw Failure($"column {name} missing");

    private static Guid Uuid(RowCanonicalDocument document, string name) => Required(document, name).AsUuid() ?? throw Failure($"column {name} is not a UUID");

    internal static AuthorityException Failure(string detail) => new(AuthorityErrorCodes.SearchIndexDerivationFailed, 409, false, detail);
}
