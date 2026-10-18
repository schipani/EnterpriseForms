using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Harness;

/// <summary>
/// Per-test organization with the Stage B personas: Designer (organization owner + designer), Clerk (form user),
/// Reader (member, no grants) and, when needed, an Outsider in a second organization. Builds the Accused and
/// AccusedAddress forms through the designer API exactly as the SPA does.
/// </summary>
public sealed class StageBScenario(StageBFixture fixture, string name)
{
    public StageBFixture Fixture { get; } = fixture;

    public string Name { get; } = name;

    public Guid OrganizationId { get; } = Guid.CreateVersion7();

    public TestUser Designer { get; } = TestUser.New("Designer");

    public TestUser Clerk { get; } = TestUser.New("Clerk");

    public TestUser Reader { get; } = TestUser.New("Reader");

    public Guid AccusedFormId { get; private set; }

    public Guid AddressFormId { get; private set; }

    public Guid RelationshipId { get; private set; }

    public Dictionary<string, Guid> AccusedFields { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Guid> AddressFields { get; } = new(StringComparer.Ordinal);

    public string Token(TestUser user) => TestIdentity.UserToken(user, OrganizationId);

    public static string TokenFor(TestUser user, Guid organizationId) => TestIdentity.UserToken(user, organizationId);

    public async Task BootstrapAsync()
    {
        var bootstrap = await Fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/organizations", new
        {
            organization_id = OrganizationId,
            organization_display_name = "Stage B " + Name,
            identities = new object[]
            {
                new { authority_user_id = Designer.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = Designer.Subject, organization_roles = new[] { "organization_owner", "designer" } },
                new { authority_user_id = Clerk.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = Clerk.Subject, organization_roles = Array.Empty<string>() },
                new { authority_user_id = Reader.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = Reader.Subject, organization_roles = Array.Empty<string>() },
            },
        }).ConfigureAwait(false);
        Assert.True(bootstrap.IsSuccess, "bootstrap failed: " + bootstrap.Body);
        await WaitGateReadyAsync().ConfigureAwait(false);
        foreach (var user in new[] { Designer, Clerk, Reader })
        {
            var me = await Fixture.Api.GetAsync("/api/v1/users/me", Token(user)).ConfigureAwait(false);
            Assert.True(me.IsSuccess, "users/me failed for " + user.DisplayName + ": " + me.Body);
        }
    }

    public async Task<JsonElement> WaitGateReadyAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        JsonElement gate;
        do
        {
            gate = await Fixture.Authority.GateAsync(OrganizationId).ConfigureAwait(false);
            if (gate.GetProperty("projection_gate_state").GetString() == "Ready")
            {
                return gate;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException("organization gate did not open: " + gate.GetRawText());
    }

    // ---- designer helpers (mirror the SPA flow) ----

    public async Task<Guid> CreateFormAsync(string formName, params TestUser[] formUsers)
    {
        var created = await Fixture.Api.PostAsync("/api/v1/form-definitions/designer", Token(Designer), new { formDefinitionName = formName, formDefinitionDescription = "synthetic " + Name, formUserUserIds = formUsers.Select(u => u.AuthorityUserId).ToArray(), formUserTeamIds = Array.Empty<Guid>() }).ConfigureAwait(false);
        Assert.True(created.Status == HttpStatusCode.Created, "create form failed: " + created.Body);
        return created.Root.GetProperty("summary").GetProperty("formDefinitionId").GetGuid();
    }

    public async Task<Guid> AddFieldAsync(Guid formId, string key, string name, string dataType, params (string Key, string Text)[] options)
    {
        var body = new { fieldKey = key, fieldDisplayName = name, fieldDescription = string.Empty, fieldDataType = dataType, options = options.Length == 0 ? null : options.Select(o => new { optionKey = o.Key, optionDisplayText = o.Text }).ToArray() };
        var added = await Fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/fields", Token(Designer), body).ConfigureAwait(false);
        Assert.True(added.Status == HttpStatusCode.Created, $"add field {key} failed: " + added.Body);
        return added.Root.GetProperty("fieldDefinitionId").GetGuid();
    }

    public async Task<JsonElement> GetFormAsync(Guid formId, TestUser? user = null)
    {
        var detail = await Fixture.Api.GetAsync($"/api/v1/form-definitions/{formId:D}", Token(user ?? Designer)).ConfigureAwait(false);
        Assert.True(detail.IsSuccess, "get form failed: " + detail.Body);
        return detail.Root.Clone();
    }

    public async Task<long> SaveDraftAsync(Guid formId, string layoutJson, long? expectedDraftRevision = null)
    {
        var expected = expectedDraftRevision ?? await CurrentDraftRevisionAsync(formId).ConfigureAwait(false);
        var saved = await Fixture.Api.SendAsync(HttpMethod.Put, $"/api/v1/form-definitions/{formId:D}/draft", Token(Designer), new { expectedDraftRevision = expected, layoutJson }, null).ConfigureAwait(false);
        Assert.True(saved.IsSuccess, "save draft failed: " + saved.Body);
        return saved.Root.GetProperty("draftRevision").GetInt64();
    }

    public async Task<long> CurrentDraftRevisionAsync(Guid formId)
    {
        var detail = await GetFormAsync(formId).ConfigureAwait(false);
        return detail.GetProperty("draft").ValueKind == JsonValueKind.Object ? detail.GetProperty("draft").GetProperty("draftRevision").GetInt64() : 0;
    }

    public async Task<JsonElement> PublishAsync(Guid formId, string changeReason)
    {
        var detail = await GetFormAsync(formId).ConfigureAwait(false);
        var currentVersion = detail.GetProperty("summary").GetProperty("currentVersionNumber").ValueKind == JsonValueKind.Number ? detail.GetProperty("summary").GetProperty("currentVersionNumber").GetInt32() : 0;
        var published = await Fixture.Api.PostAsync($"/api/v1/form-definitions/{formId:D}/publish", Token(Designer), new { expectedDraftRevision = await CurrentDraftRevisionAsync(formId).ConfigureAwait(false), expectedCurrentVersionNumber = currentVersion, changeReason }).ConfigureAwait(false);
        Assert.True(published.Status == HttpStatusCode.Created, "publish failed: " + published.Body);
        return published.Root.Clone();
    }

    public static string Field(Guid fieldId, string control, string label, int span, bool required = false, bool newRow = false, object? validation = null) =>
        JsonSerializer.Serialize(new { kind = "field", fieldDefinitionId = fieldId, control, label, required, layout = new { columnSpan = span, startOnNewRow = newRow }, validation = validation ?? new { } });

    public static string Section(string key, string title, params string[] elements) => $$"""{"sectionKey":"{{key}}","title":"{{title}}","gridColumns":4,"elements":[{{string.Join(",", elements)}}]}""";

    public static string Layout(string title, params string[] sections) => $$"""{"layoutSchemaVersion":2,"title":"{{title}}","sections":[{{string.Join(",", sections)}}]}""";

    /// <summary>Accused (String/Date/Boolean/Option/File + related AccusedAddress list) and AccusedAddress (String/Option) forms, both published.</summary>
    public async Task BuildAccusedWorkspaceAsync()
    {
        AddressFormId = await CreateFormAsync("AccusedAddress " + Name, Clerk).ConfigureAwait(false);
        AddressFields["street_address"] = await AddFieldAsync(AddressFormId, "street_address", "Street address", "String").ConfigureAwait(false);
        AddressFields["city_name"] = await AddFieldAsync(AddressFormId, "city_name", "City", "String").ConfigureAwait(false);
        AddressFields["address_kind"] = await AddFieldAsync(AddressFormId, "address_kind", "Address kind", "Option", ("home", "Home"), ("work", "Work")).ConfigureAwait(false);
        await SaveDraftAsync(AddressFormId, Layout("AccusedAddress", Section("main", "Address",
            Field(AddressFields["street_address"], "text", "Street address", 4, required: true, validation: new { maxLength = 200 }),
            Field(AddressFields["city_name"], "text", "City", 2),
            Field(AddressFields["address_kind"], "select", "Address kind", 2)))).ConfigureAwait(false);
        await PublishAsync(AddressFormId, "initial AccusedAddress").ConfigureAwait(false);

        AccusedFormId = await CreateFormAsync("Accused " + Name, Clerk).ConfigureAwait(false);
        AccusedFields["given_name"] = await AddFieldAsync(AccusedFormId, "given_name", "Given name", "String").ConfigureAwait(false);
        AccusedFields["family_name"] = await AddFieldAsync(AccusedFormId, "family_name", "Family name", "String").ConfigureAwait(false);
        AccusedFields["date_of_birth"] = await AddFieldAsync(AccusedFormId, "date_of_birth", "Date of birth", "Date").ConfigureAwait(false);
        AccusedFields["in_custody"] = await AddFieldAsync(AccusedFormId, "in_custody", "In custody", "Boolean").ConfigureAwait(false);
        AccusedFields["case_status"] = await AddFieldAsync(AccusedFormId, "case_status", "Case status", "Option", ("open", "Open"), ("closed", "Closed")).ConfigureAwait(false);
        AccusedFields["case_documents"] = await AddFieldAsync(AccusedFormId, "case_documents", "Case documents", "File").ConfigureAwait(false);
        var relationship = await Fixture.Api.PostAsync($"/api/v1/form-definitions/{AccusedFormId:D}/relationships", Token(Designer), new { childFormDefinitionId = AddressFormId, relationshipDefinitionKey = "accused_address_list", relationshipDisplayName = "Addresses", minimumChildCount = 0, maximumChildCount = 10 }).ConfigureAwait(false);
        Assert.True(relationship.Status == HttpStatusCode.Created, "create relationship failed: " + relationship.Body);
        RelationshipId = relationship.Root.GetProperty("formRelationshipDefinitionId").GetGuid();
        await SaveDraftAsync(AccusedFormId, AccusedLayout()).ConfigureAwait(false);
        await PublishAsync(AccusedFormId, "initial Accused").ConfigureAwait(false);
    }

    public string AccusedLayout(int maxFiles = 5, long maxFileSizeBytes = 20L * 1024 * 1024) => Layout("Accused",
        Section("identity", "Identity",
            Field(AccusedFields["given_name"], "text", "Given name", 2, required: true, validation: new { maxLength = 100 }),
            Field(AccusedFields["family_name"], "text", "Family name", 2, required: true, validation: new { maxLength = 100 }),
            Field(AccusedFields["date_of_birth"], "date", "Date of birth", 1),
            Field(AccusedFields["in_custody"], "checkbox", "In custody", 1),
            Field(AccusedFields["case_status"], "select", "Case status", 2)),
        Section("related", "Related",
            JsonSerializer.Serialize(new { kind = "relatedFormList", formRelationshipDefinitionId = RelationshipId, label = "Addresses", layout = new { columnSpan = 4, startOnNewRow = true }, summaryFieldKeys = new[] { "street_address", "city_name" } }),
            JsonSerializer.Serialize(new { kind = "documentCollection", fieldDefinitionId = AccusedFields["case_documents"], label = "Case documents", layout = new { columnSpan = 4, startOnNewRow = true }, upload = new { allowMultiple = true, maxFiles, maxFileSizeBytes, allowedContentTypes = new[] { "application/pdf", "text/plain", "application/octet-stream" } } })));

    // ---- records ----

    public static Dictionary<string, string> AccusedValues(string given = "Avery", string family = "Synthetic") => new(StringComparer.Ordinal)
    {
        ["given_name"] = given,
        ["family_name"] = family,
        ["date_of_birth"] = "1990-02-28",
        ["in_custody"] = "true",
        ["case_status"] = "open",
    };

    public async Task<Guid> CreateAccusedAsync(TestUser user, Dictionary<string, string>? values = null)
    {
        var created = await Fixture.Api.PostAsync($"/api/v1/form-definitions/{AccusedFormId:D}/records", Token(user), new { values = values ?? AccusedValues(), ownerTeamId = (Guid?)null }).ConfigureAwait(false);
        Assert.True(created.Status == HttpStatusCode.Created, "create accused failed: " + created.Body);
        var id = created.Root.GetProperty("formRecordId").GetGuid();
        await WaitProjectionAsync(user, id).ConfigureAwait(false);
        return id;
    }

    public async Task<Guid> CreateAddressAsync(TestUser user, Guid accusedId, string street, string city = "Toronto", string kind = "home")
    {
        var created = await Fixture.Api.PostAsync($"/api/v1/form-records/{accusedId:D}/related", Token(user), new { formRelationshipDefinitionId = RelationshipId, values = new Dictionary<string, string> { ["street_address"] = street, ["city_name"] = city, ["address_kind"] = kind } }).ConfigureAwait(false);
        Assert.True(created.Status == HttpStatusCode.Created, "create address failed: " + created.Body);
        var id = created.Root.GetProperty("formRecordId").GetGuid();
        await WaitProjectionAsync(user, id).ConfigureAwait(false);
        return id;
    }

    public Task<HttpResult> ProjectionAsync(TestUser user, Guid recordId) => Fixture.Api.GetAsync($"/api/v1/form-records/{recordId:D}/projection", Token(user));

    /// <summary>Reads wait for the content checkpoint archive receipt (503 while pending), exactly like Stage A.</summary>
    public async Task<HttpResult> WaitProjectionAsync(TestUser user, Guid recordId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        HttpResult last;
        do
        {
            last = await ProjectionAsync(user, recordId).ConfigureAwait(false);
            if (last.Status != HttpStatusCode.ServiceUnavailable)
            {
                Assert.True(last.IsSuccess, "projection failed: " + last.Body);
                return last;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException("projection stayed unavailable: " + last.Body);
    }

    public Task<HttpResult> ChildrenAsync(TestUser user, Guid accusedId) => Fixture.Api.GetAsync($"/api/v1/form-records/{accusedId:D}/related/{RelationshipId:D}", Token(user));

    // ---- documents ----

    public static byte[] SyntheticBytes(int length, int seed)
    {
        var bytes = new byte[length];
        var rng = new Random(seed);
        rng.NextBytes(bytes);
        return bytes;
    }

    public static byte[] Eicar() => Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    /// <summary>Drives one upload the way the browser upload manager does: begin → chunks → complete → poll → commit → readable.</summary>
    public async Task<JsonElement> UploadAsync(TestUser user, Guid accusedId, string fileName, string contentType, byte[] bytes, bool expectCommit = true, TimeSpan? timeout = null)
    {
        var session = await TransferAsync(user, accusedId, fileName, contentType, bytes, timeout).ConfigureAwait(false);
        return session.GetProperty("sessionState").GetString() == "Processed" && expectCommit ? await CommitAsync(user, accusedId, session.GetProperty("uploadSessionId").GetGuid()).ConfigureAwait(false) : session;
    }

    /// <summary>User-session commit binds the processed version into the signed record content; reads are gated until the archive receipt lands.</summary>
    public async Task<JsonElement> CommitAsync(TestUser user, Guid accusedId, Guid sessionId)
    {
        var committed = await Fixture.Api.PostAsync($"/api/v1/upload-sessions/{sessionId:D}/commit", Token(user), new { }).ConfigureAwait(false);
        Assert.True(committed.IsSuccess, "commit failed: " + committed.Body);
        await WaitProjectionAsync(user, accusedId).ConfigureAwait(false);
        return committed.Root.Clone();
    }

    /// <summary>begin → chunks → complete → poll until the worker settled the session (Processed/Rejected/Failed).</summary>
    public async Task<JsonElement> TransferAsync(TestUser user, Guid accusedId, string fileName, string contentType, byte[] bytes, TimeSpan? timeout = null)
    {
        var begun = await Fixture.Api.PostAsync("/api/v1/upload-sessions", Token(user), new { formRecordId = accusedId, fieldDefinitionId = AccusedFields["case_documents"], originalFileName = fileName, contentType, sizeBytes = bytes.LongLength, replacesFileAttachmentId = (Guid?)null }).ConfigureAwait(false);
        Assert.True(begun.Status == HttpStatusCode.Created, "begin upload failed: " + begun.Body);
        var sessionId = begun.Root.GetProperty("uploadSessionId").GetGuid();
        var chunkSize = begun.Root.GetProperty("chunkSizeBytes").GetInt32();
        var expectedChunks = begun.Root.GetProperty("expectedChunkCount").GetInt32();
        for (var index = 0; index < expectedChunks; index++)
        {
            var slice = bytes.Skip(index * chunkSize).Take(chunkSize).ToArray();
            var receipt = await Fixture.PutBytesAsync($"/api/v1/upload-sessions/{sessionId:D}/chunks/{index}", Token(user), slice).ConfigureAwait(false);
            Assert.True(receipt.IsSuccess, $"chunk {index} failed: " + receipt.Body);
        }

        var completed = await Fixture.Api.PostAsync($"/api/v1/upload-sessions/{sessionId:D}/complete", Token(user), new { }).ConfigureAwait(false);
        Assert.True(completed.IsSuccess, "complete failed: " + completed.Body);
        return await WaitSessionAsync(user, sessionId, state => state is "Processed" or "Rejected" or "Failed", timeout).ConfigureAwait(false);
    }

    public async Task<JsonElement> WaitSessionAsync(TestUser user, Guid sessionId, Func<string, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        JsonElement last = default;
        do
        {
            var result = await Fixture.Api.GetAsync($"/api/v1/upload-sessions/{sessionId:D}", Token(user)).ConfigureAwait(false);
            Assert.True(result.IsSuccess, "session read failed: " + result.Body);
            last = result.Root.Clone();
            if (done(last.GetProperty("sessionState").GetString()!))
            {
                return last;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException("upload session did not settle: " + last.GetRawText());
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
