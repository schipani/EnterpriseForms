// E3-M2f-2G historical-client compatibility: BUSINESS AGENT (docs/security/E3_M2F2G_VERIFICATION_RECORD.md). TEST-ONLY.
// Development/Testing only. Never part of the solution; built only in a temporary work directory by
// scripts/dev/e3-m2e2-acceptance-pg-gate.ps1, once per business build, against THAT build's own Release binaries (BusinessBinaries):
//   - 11e15ee (before E3-M2b-2: field_encrypted_values and file_attachment_versions are always encoded as signed schema 1; no header)
//   - 0be5f52 (E3-M2e-2: faithful schema-2 encoding and the verified readers, but no capability header: level 1 by CAP-S5)
//   - this tree (E3-M2f-1 and later: declares level 2)
// The source is identical for every build; it compiles because the members it uses have the same shape in all three (verified when the
// gate builds it). Nothing in a historical build is modified.
//
// One call per process:
//   BusinessAgent encode <input.json> <output.json>   rows (properties by name) -> that build's BusinessSignedSchemas.Encode bytes
//   BusinessAgent send   <input.json> <output.json>   one content request through that build's own Authority client
// The agent never reads BusinessDb: the harness hands it the stored row values (the business would read them from its database).
// It records, per request, the capability header instances that actually left the process (a recording handler below the client).
using System.Reflection;
using System.Text.Json;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Application.Features.Search.IndexBuilder;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Authority;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var command = args[0];
var input = JsonDocument.Parse(await File.ReadAllTextAsync(args[1])).RootElement;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var domain = typeof(EnterpriseForms.Domain.Features.FormRecords.FieldEncryptedValue).Assembly;

object output;
try
{
    output = command switch
    {
        "encode" => Encode(input),
        "send" => await SendAsync(input),
        _ => throw new ArgumentOutOfRangeException(nameof(args), command),
    };
}
catch (Exception ex)
{
    output = new { Ok = false, Error = ex.GetType().Name + ": " + ex.Message };
}

await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(output));
return 0;

object Encode(JsonElement request)
{
    var results = new List<object>();
    foreach (var row in request.GetProperty("Rows").EnumerateArray())
    {
        var typeName = row.GetProperty("Type").GetString()!;
        var type = domain.GetTypes().SingleOrDefault(t => t.Name == typeName && t.Namespace?.StartsWith("EnterpriseForms.Domain.Features", StringComparison.Ordinal) == true)
                   ?? throw new InvalidOperationException("this build has no domain type " + typeName);
        var instance = Activator.CreateInstance(type)!;
        var ignored = new List<string>();
        foreach (var property in row.GetProperty("Properties").EnumerateObject())
        {
            var target = type.GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance);
            if (target is null || !target.CanWrite)
            {
                ignored.Add(property.Name); // a column this build does not know (it would not read it from BusinessDb either)
                continue;
            }

            target.SetValue(instance, property.Value.Deserialize(target.PropertyType, json));
        }

        var bytes = BusinessSignedSchemas.Encode((ISignedContentRow)instance, row.GetProperty("KeyVersion").GetUInt32());
        results.Add(new { Base64 = Convert.ToBase64String(bytes), Ignored = ignored });
    }

    return new { Ok = true, Results = results };
}

async Task<object> SendAsync(JsonElement request)
{
    var baseAddress = request.GetProperty("BaseAddress").GetString()!;
    if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri) || uri.Host != "127.0.0.1" || uri.Scheme != "http")
    {
        throw new InvalidOperationException("the agent only talks to a loopback Authority host of the disposable run");
    }

    var recorder = new Recorder(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    // Header-grammar checks only: "HeaderOverride" replaces the build's own capability header instances (an array of values; [] removes
    // them) AFTER the build's client produced the request, so authentication and the wire body stay exactly the build's own.
    HttpMessageHandler primary = recorder;
    if (request.TryGetProperty("HeaderOverride", out var overrideValues) && overrideValues.ValueKind == JsonValueKind.Array)
    {
        primary = new Override([.. overrideValues.EnumerateArray().Select(v => v.GetString()!)], recorder);
    }

    var services = new ServiceCollection();
    foreach (var name in new[] { AuthorityHttpClient.HttpClientName, ProtectionVerifierHttpClient.HttpClientName, SearchIndexBuilderHttpClient.HttpClientName })
    {
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => primary);
    }

    var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    var authorityOptions = Options.Create(new AuthorityClientOptions
    {
        BaseAddress = baseAddress,
        WorkloadIssuer = Text(request, "WorkloadIssuer"),
        WorkloadAudience = Text(request, "WorkloadAudience"),
        WorkloadSharedKeyBase64 = Text(request, "WorkloadKey"),
    });

    var kind = request.GetProperty("Kind").GetString()!;
    object? result = null;
    try
    {
        switch (kind)
        {
            case "prepare":
            case "commit":
            case "verify":
            case "batch-verify":
            {
                var client = new AuthorityHttpClient(factory, authorityOptions, TimeProvider.System);
                var user = new DelegatedUserContext(Text(request, "UserIssuer"), Text(request, "UserSubject"), request.GetProperty("OrganizationId").GetGuid(), UserAssertion(request));
                var body = request.GetProperty("Request");
                result = kind switch
                {
                    "prepare" => await client.PrepareContentChangeAsync(user, body.Deserialize<PrepareContentChangeRequest>(json)!, Text(request, "IdempotencyKey"), CancellationToken.None),
                    "commit" => await client.CommitContentChangeAsync(user, request.GetProperty("OperationId").GetGuid(), body.Deserialize<CommitContentChangeRequest>(json)!, CancellationToken.None),
                    "verify" => await client.VerifyRecordProjectionAsync(user, body.Deserialize<VerifyRecordProjectionRequest>(json)!, CancellationToken.None),
                    _ => await client.VerifyRecordProjectionsAsync(user, body.Deserialize<VerifyRecordProjectionsRequest>(json)!, CancellationToken.None),
                };
                break;
            }

            case "census":
            {
                var client = new ProtectionVerifierHttpClient(factory, Options.Create(new ProtectionVerifierOptions { Issuer = Text(request, "WorkloadIssuer"), Audience = Text(request, "WorkloadAudience"), SharedKeyBase64 = Text(request, "WorkloadKey") }), authorityOptions, TimeProvider.System);
                result = await client.TakeCensusAsync(request.GetProperty("SnapshotId").GetGuid(), request.GetProperty("Records").Deserialize<List<ProtectionCensusItem>>(json)!, null, CancellationToken.None);
                break;
            }

            case "backfill":
            case "deep-verify":
            {
                var client = new SearchIndexBuilderHttpClient(factory, Options.Create(new SearchIndexBuilderOptions { Issuer = Text(request, "WorkloadIssuer"), Audience = Text(request, "WorkloadAudience"), SharedKeyBase64 = Text(request, "WorkloadKey") }), authorityOptions, TimeProvider.System);
                var records = request.GetProperty("Records").Deserialize<List<SearchIndexBackfillItem>>(json)!;
                result = kind == "backfill"
                    ? await client.BackfillAsync(request.GetProperty("OrganizationId").GetGuid(), request.GetProperty("FormDefinitionId").GetGuid(), records, CancellationToken.None)
                    : await client.DeepVerifyAsync(request.GetProperty("OrganizationId").GetGuid(), records, CancellationToken.None);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(request), kind);
        }

        return new { Ok = true, Status = recorder.LastStatus, Result = result, Sent = recorder.Sent };
    }
    catch (AuthorityClientException ex)
    {
        return new { Ok = false, Status = recorder.LastStatus, Error = "AuthorityClientException", ErrorCode = ex.ErrorCode, ex.HttpStatusCode, ex.Retryable, Sent = recorder.Sent };
    }
}

static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

static string UserAssertion(JsonElement request) =>
    new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Text(request, "UserIssuer"),
        Audience = Text(request, "UserAudience"),
        IssuedAt = DateTime.UtcNow,
        NotBefore = DateTime.UtcNow.AddSeconds(-5),
        Expires = DateTime.UtcNow.AddMinutes(5),
        Claims = new Dictionary<string, object> { ["sub"] = Text(request, "UserSubject") },
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(Text(request, "UserKey"))), SecurityAlgorithms.HmacSha256),
    });

/// <summary>Below the build's own client: records every request's capability header instances exactly as they leave the process.</summary>
internal sealed class Recorder(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public List<object> Sent { get; } = [];

    public int LastStatus { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string[] values = request.Headers.TryGetValues("EnterpriseForms-Envelope-Capability", out var v) ? [.. v] : [];
        var response = await base.SendAsync(request, cancellationToken);
        LastStatus = (int)response.StatusCode;
        Sent.Add(new { Path = request.RequestUri?.AbsolutePath, CapabilityHeaderValues = values, Status = LastStatus });
        return response;
    }
}

/// <summary>Replaces the capability header instances (test-only grammar checks); the recorder below it sees what is actually sent.</summary>
internal sealed class Override(string[] values, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("EnterpriseForms-Envelope-Capability");
        foreach (var value in values)
        {
            request.Headers.TryAddWithoutValidation("EnterpriseForms-Envelope-Capability", value);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
