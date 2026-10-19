using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.FormRecords.GetFormRecord;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.TransferOwnership;
using EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord;
using EnterpriseForms.Application.Features.Identity.EnsureProjection;
using EnterpriseForms.Application.Features.Sharing.CreateShare;
using EnterpriseForms.Application.Features.Sharing.RevokeShare;
using EnterpriseForms.Application.Features.Teams.ManageTeam;
using EnterpriseForms.Infrastructure.Authority;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseForms.Infrastructure;

public static class BusinessInfrastructureRegistration
{
    public static IServiceCollection AddBusinessInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("BusinessDb");
        services.AddDbContextFactory<BusinessDbContext>(options => options.UseNpgsql(connectionString ?? "Host=unconfigured.invalid;Database=unconfigured;Username=unconfigured;Password=unconfigured"));
        services.AddSingleton<IBusinessStore, BusinessStore>();

        services.Configure<AuthorityClientOptions>(configuration.GetSection(AuthorityClientOptions.SectionName));
        services.AddHttpClient(AuthorityHttpClient.HttpClientName);
        services.AddSingleton<IAuthorizationAuthorityClient, AuthorityHttpClient>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuthorityCommandAwaiter>();
        services.AddSingleton<RecordCandidateStager>();
        services.AddSingleton<EnsureIdentityProjectionHandler>();
        services.AddSingleton<CreateFormDefinitionHandler>();
        services.AddSingleton<CreateFormRecordHandler>();
        services.AddSingleton<GetFormRecordHandler>();
        services.AddSingleton<UpdateFormRecordHandler>();
        services.AddSingleton<TransferOwnershipHandler>();
        services.AddSingleton<CreateShareHandler>();
        services.AddSingleton<RevokeShareHandler>();
        services.AddSingleton<CreateTeamHandler>();
        services.AddSingleton<ChangeTeamMembershipHandler>();
        return services;
    }
}
