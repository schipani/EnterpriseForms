using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Ports;
using EnterpriseForms.Application.Features.Search.FieldSearch;
using EnterpriseForms.Application.Features.Search.Verification;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C-188 at the business contract: the search capabilities carry the Authority's "no longer in the form" flag for a field searched
/// under a retained-field approval; the not-searchable hint lists fields of the current version only (removed fields are visible to
/// searchers only once approved); and the business client stays compatible with an Authority that does not send the new fields
/// (they default to the pre-C-188 meaning: in the current version, no scope, retained approvals off).
/// </summary>
public sealed class RetainedFieldSearchContractTests
{
    private static readonly RequestContext Context = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid First = Guid.CreateVersion7();
    private static readonly Guid Phone = Guid.CreateVersion7();
    private static readonly Guid Email = Guid.CreateVersion7();

    /// <summary>The business Authority client's wire options (AuthorityHttpClient).</summary>
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower, Converters = { new JsonStringEnumConverter() } };

    private static T Stub<T>()
        where T : class => DispatchProxy.Create<T, PhaseC7ScopeVerificationTests.Stub<T>>();

    private static FieldSearchHandler Handler(AuthorityFieldSearchCapabilities answer)
    {
        var limits = new FieldSearchLimits();
        var authority = Stub<IAuthorizationAuthorityClient>();
        return new FieldSearchHandler(limits, new PhaseC7FieldSearchTests.ApprovingPolicy(_ => answer), new TrustedScopeVerifier(authority, Stub<IAcceptedRecordItemsReader>(), limits, TimeProvider.System),
            new FieldSearchAdmission(limits), authority, Stub<IOpaqueCursorProtector>(), Stub<IRecordDisplayDirectory>(), TimeProvider.System);
    }

    private static AuthorityFieldSearchStatus Status(Guid field, string label, string state, bool inCurrent) =>
        new(field, label, "String", "Internal", "Standard", "Eligible", state, null, state == "Active" ? ["equals"] : [], ["equals", "startsWith"], inCurrent, inCurrent ? 2 : 1, state == "None" ? null : inCurrent ? "CurrentVersion" : "RetainedField");

    [Fact]
    public async Task CapabilitiesMarkARetainedFieldAndTheNotSearchableHintListsOnlyCurrentFields()
    {
        var answer = new AuthorityFieldSearchCapabilities(Form, true, 3, 9,
            [new AuthoritySearchableField(Phone, "Phone", "String", ["equals", "startsWith"], [], 1, InCurrentVersion: false), new AuthoritySearchableField(First, "First name", "String", ["equals"], [], 2)],
            new AuthorityFieldSearchConfiguration(true, true, string.Empty, 2,
                [Status(First, "First name", "Active", true), Status(Email, "Email", "None", true), Status(Phone, "Phone", "Active", false), Status(Guid.CreateVersion7(), "Old note", "None", false)],
                RetainedFieldApprovalsEnabled: true),
            Guid.CreateVersion7());
        var capabilities = await Handler(answer).CapabilitiesAsync(Context, Form, TestContext.Current.CancellationToken);
        Assert.Equal([(Phone, false), (First, true)], capabilities.Fields.Select(f => (f.FieldDefinitionId, f.InCurrentVersion)));
        Assert.Equal([Email], capabilities.NotSearchableFields.Select(f => f.FieldDefinitionId)); // "Old note" is no longer in the form: not hinted
    }

    [Fact]
    public void AnAuthorityWithoutTheC188FieldsIsReadWithTheirPreviousMeaning()
    {
        var searchable = JsonSerializer.Deserialize<AuthoritySearchableField>("""{"field_definition_id":"0199a2b3-c4d5-7e6f-8123-456789abcdef","field_label":"Phone","field_data_type":"String","operators":["equals"],"options":[],"approved_form_version_number":1}""", Wire)!;
        Assert.True(searchable.InCurrentVersion);
        var status = JsonSerializer.Deserialize<AuthorityFieldSearchStatus>("""{"field_definition_id":"0199a2b3-c4d5-7e6f-8123-456789abcdef","field_label":"Phone","field_data_type":"String","field_classification":"Internal","protection_mode":"Standard","eligibility":"Eligible","approval_state":"None","invalidation_reason":null,"approved_operators":[],"permitted_operators":["equals"]}""", Wire)!;
        Assert.Equal((true, (int?)null, (string?)null), (status.InCurrentVersion, status.LastPlacedVersionNumber, status.ApprovalScope));
        var configuration = JsonSerializer.Deserialize<AuthorityFieldSearchConfiguration>("""{"can_approve":true,"approval_operational":true,"approval_unavailable_reason":"","registered_form_version_number":1,"fields":[]}""", Wire)!;
        Assert.False(configuration.RetainedFieldApprovalsEnabled); // an older Authority never enables retained approvals
        var form = JsonSerializer.Deserialize<AuthorityFieldSearchAdministrationForm>("""{"form_definition_id":"0199a2b3-c4d5-7e6f-8123-456789abcdef","form_title":"F","registered_form_version_number":1,"registered_on":"2026-11-24T00:00:00Z","field_count":1,"eligible_field_count":1,"active_approval_count":0,"invalidated_approval_count":0}""", Wire)!;
        Assert.Equal(0, form.RetainedApprovalCount);
    }

    [Fact]
    public void TheNewFieldsTravelOnTheWireInSnakeCase()
    {
        var json = JsonSerializer.Serialize(new AuthoritySearchableField(Phone, "Phone", "String", ["equals"], [], 1, InCurrentVersion: false), Wire);
        Assert.Contains("\"in_current_version\":false", json, StringComparison.Ordinal);
    }
}
