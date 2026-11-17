using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using Microsoft.OpenApi;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md §7, §9, §12.2): composes the shared business envelope capability floor from the
/// host's final deployment configuration (<see cref="EnvelopeCapabilityFloor.ConfigurationKey"/>, resolved once, lazily, so test-host
/// overrides apply), its startup guard (an invalid value refuses the start) and the CAP-E4 operational signal. The floor is injected into
/// <see cref="ContentService"/>; the protection census and the search-index builder use that same instance.
/// </summary>
public static class EnvelopeCapabilityComposition
{
    public static IServiceCollection AddEnvelopeCapabilityFloor(this IServiceCollection services)
    {
        services.AddSingleton<IEnvelopeCapabilityObserver, EnvelopeCapabilityObserver>();
        services.AddSingleton(provider => EnvelopeCapabilityFloor.Create(
            provider.GetRequiredService<IConfiguration>()[EnvelopeCapabilityFloor.ConfigurationKey],
            provider.GetRequiredService<IEnvelopeCapabilityObserver>()));
        services.AddHostedService<EnvelopeCapabilityFloorStartupGuard>();
        return services;
    }

    /// <summary>
    /// CAP-E1: documents the capability header and the <c>409 CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT</c> refusal on one of the seven
    /// enforced content endpoints in the internal OpenAPI document. Documentation only; enforcement is in the application service.
    /// </summary>
    public static RouteHandlerBuilder WithEnvelopeCapabilityContract(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = ClientEnvelopeCapability.HeaderName,
                    In = ParameterLocation.Header,
                    Required = false,
                    Description = "E3-M2f: the business envelope capability of the calling workload build. Exactly one instance, exactly \"1\" or \"2\"; "
                                  + "absent, repeated or any other value is level 1. Not part of the request digest or idempotency identity.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("1"), JsonValue.Create("2")] },
                });
                if (operation.Responses?.TryGetValue("409", out var conflict) == true && conflict is OpenApiResponse response)
                {
                    response.Description = "Conflict. Among other codes: " + AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient
                                           + " (not retryable): the declared envelope capability is below the effective floor. The refused request's transaction was ended before any verification and wrote nothing. "
                                           + "Independently of that transaction, the Authority may record bounded refusal evidence on the organization's audit stream.";
                }

                return Task.CompletedTask;
            });
    }
}

/// <summary>
/// CAP-E4: one counter per evaluation (meter <see cref="MeterName"/>; tags outcome, client level and organization identifier) and a log
/// event for each refusal or unavailable evaluation (organization identifier and level only). Not durable refusal evidence (E3-M2f-3).
/// </summary>
public sealed partial class EnvelopeCapabilityObserver : IEnvelopeCapabilityObserver, IDisposable
{
    public const string MeterName = "AuthorizationAuthority.EnvelopeCapability";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> evaluations;
    private readonly ILogger<EnvelopeCapabilityObserver> logger;

    public EnvelopeCapabilityObserver(ILogger<EnvelopeCapabilityObserver> logger)
    {
        this.logger = logger;
        evaluations = meter.CreateCounter<long>("envelope_capability_evaluations", description: "E3-M2f-2 business envelope capability floor evaluations by outcome");
    }

    public void Evaluated(Guid organizationId, int clientLevel, string outcome)
    {
        evaluations.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("client_level", clientLevel),
            new KeyValuePair<string, object?>("organization_id", organizationId));
        if (outcome == EnvelopeCapabilityOutcomes.Refused)
        {
            LogRefused(logger, organizationId, clientLevel);
        }
        else if (outcome == EnvelopeCapabilityOutcomes.Unavailable)
        {
            LogUnavailable(logger, organizationId, clientLevel);
        }
    }

    public void Dispose() => meter.Dispose();

    [LoggerMessage(EventId = 9250, Level = LogLevel.Warning, Message = "Envelope capability refused: organization={OrganizationId} client_level={ClientLevel} outcome=refused")]
    private static partial void LogRefused(ILogger logger, Guid organizationId, int clientLevel);

    [LoggerMessage(EventId = 9251, Level = LogLevel.Error, Message = "Envelope capability floor unavailable: organization={OrganizationId} client_level={ClientLevel} outcome=unavailable")]
    private static partial void LogUnavailable(ILogger logger, Guid organizationId, int clientLevel);
}

/// <summary>§9: resolves the floor at host start (an invalid configured value throws, so the host refuses to start) and logs the effective deployment floor.</summary>
public sealed partial class EnvelopeCapabilityFloorStartupGuard(IServiceProvider services, ILogger<EnvelopeCapabilityFloorStartupGuard> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return Task.CompletedTask;
        }

        var floor = services.GetRequiredService<EnvelopeCapabilityFloor>(); // throws on a refused configuration
        LogResolved(logger, floor.DeploymentFloor);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 9252, Level = LogLevel.Information, Message = "Envelope capability floor enforced: deployment floor {DeploymentFloor}")]
    private static partial void LogResolved(ILogger logger, int deploymentFloor);
}
