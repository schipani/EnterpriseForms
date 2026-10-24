using EnterpriseForms.Application.Common;

namespace EnterpriseForms.Api.Composition;

/// <summary>
/// F-C7-01 fault-injection tests only. When <see cref="WriteFaultCheckpoints.ConfigurationKey"/> is "true" — accepted in
/// the Testing environment only; any other environment refuses to start — a request carrying
/// <see cref="WriteFaultCheckpoints.RequestHeader"/> fails with a transient 503 at the named checkpoint. Without the
/// setting the no-op injector is registered and the header means nothing.
/// </summary>
public static class TestingWriteFaults
{
    public static IServiceCollection AddWriteFaultInjection(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var configured = configuration.GetValue<bool>(WriteFaultCheckpoints.ConfigurationKey);
        if (!configured)
        {
            return services.AddSingleton<IWriteFaultInjector>(NoWriteFaults.Instance);
        }

        if (!environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException($"{WriteFaultCheckpoints.ConfigurationKey} is a Testing-only hook and must not be set in the {environment.EnvironmentName} environment. Refusing to start.");
        }

        services.AddHttpContextAccessor();
        return services.AddSingleton<IWriteFaultInjector, HeaderWriteFaultInjector>();
    }

    private sealed class HeaderWriteFaultInjector(IHttpContextAccessor accessor) : IWriteFaultInjector
    {
        public void Checkpoint(string checkpointName)
        {
            if (accessor.HttpContext?.Request.Headers[WriteFaultCheckpoints.RequestHeader].ToString() == checkpointName)
            {
                throw new BusinessException(WriteFaultCheckpoints.FaultErrorCode, 503, "Testing fault injected at " + checkpointName);
            }
        }
    }
}
