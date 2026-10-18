using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Infrastructure.Faults;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AuthorizationAuthority.Api.Features.OperatorPlane;

/// <summary>Operator plane: requires the operator credential (never the business workload credential).</summary>
public static class OperatorEndpoints
{
    public static IEndpointRouteBuilder MapOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(InternalApi.Prefix + "/operator").WithTags("Operator").RequireAuthorization(InternalAuthentication.OperatorPolicy);

        group.MapPost("/organizations", static async Task<Ok<BootstrapOrganizationResponse>> (BootstrapOrganizationRequest request, HttpContext context, OrganizationBootstrapService service, CancellationToken cancellationToken) =>
            {
                var operatorName = context.User.FindFirst("workload_name")?.Value ?? "operator";
                return TypedResults.Ok(await service.BootstrapAsync(request, operatorName, InternalAuthentication.Correlation(context), cancellationToken).ConfigureAwait(false));
            })
            .WithName("BootstrapOrganization")
            .WithSummary("Provisions organization state, identity bindings, organization roles and the organization integrity key (Stage A stand-in for the PIM/dual-control workflow).");

        group.MapGet("/organizations/{organizationId:guid}/gate", static async Task<Ok<OrganizationGateStatus>> (Guid organizationId, OrganizationBootstrapService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.GetGateStatusAsync(organizationId, cancellationToken).ConfigureAwait(false)))
            .WithName("GetOrganizationGate");

        group.MapPost("/fault-injection", static Results<Ok<FaultInjectionRequest>, ProblemHttpResult> (FaultInjectionRequest request, IServiceProvider services, IHostEnvironment environment) =>
            {
                var injector = services.GetService<ProcessCrashFaultInjector>();
                if (injector is null || !environment.IsEnvironment("Testing"))
                {
                    return TypedResults.Problem(title: "Fault injection disabled", detail: "FaultInjection:Enabled is honoured only in the Testing environment.", statusCode: StatusCodes.Status403Forbidden);
                }

                if (request.FaultName is not (FaultNames.None or FaultNames.CrashAfterJournalCommit or FaultNames.CrashAfterProjectionWrite or FaultNames.CrashAfterContentHeadCommit))
                {
                    throw AuthorityException.Invalid("unknown fault name");
                }

                injector.Arm(request.FaultName == FaultNames.None ? string.Empty : request.FaultName);
                return TypedResults.Ok(new FaultInjectionRequest(injector.Armed.Length == 0 ? FaultNames.None : injector.Armed));
            })
            .WithName("ArmFaultInjection")
            .WithSummary("Testing only: arms a one-shot process crash at a named protocol transition (SEC-P09).");

        return endpoints;
    }
}
