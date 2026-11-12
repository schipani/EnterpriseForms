using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace EnterpriseForms.Experiments.ManagedHsmProbe;

internal interface IDigestSigner
{
    /// <summary>Documented SDK path: <see cref="CryptographyClient.SignAsync"/> with <see cref="SignatureAlgorithm.HS256"/> over a pre-hashed digest.</summary>
    Task<HsmCallResult> SignDigestAsync(string versionId, byte[] digest, CancellationToken cancellationToken);
}

/// <summary>
/// Azure SDK cross-check for Experiment A only. The SDK documents <c>SignAsync</c> as signing "the pre-hashed digest", and its
/// <c>SignDataAsync</c> hashes the data locally with SHA-256 for HS256 (SignatureAlgorithm.GetHashAlgorithm), so the SDK is
/// suitable for the digest path but cannot express Experiment B (raw input); that runs only through <see cref="ManagedHsmRestClient"/>.
/// The SDK uses its own challenge-based authentication, which independently exercises the token-scope assumption.
/// </summary>
internal sealed class SdkDigestSigner(ProbeConfiguration configuration, TokenCredential credential, HttpPipelineTransport? transport = null) : IDigestSigner
{
    public async Task<HsmCallResult> SignDigestAsync(string versionId, byte[] digest, CancellationToken cancellationToken)
    {
        var options = new CryptographyClientOptions(ServiceVersion(configuration.ApiVersion));
        options.Retry.MaxRetries = 0; // throttling must surface as Inconclusive, not be absorbed
        options.Diagnostics.IsLoggingContentEnabled = false;
        if (transport is not null)
        {
            options.Transport = transport;
        }

        var keyId = ManagedHsmUrls.KeyIdentifier(configuration.EndpointUri, configuration.KeyName!, versionId);
        var client = new CryptographyClient(keyId, credential, options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.OperationTimeoutSeconds, 1, 60)));
        try
        {
            var result = await client.SignAsync(SignatureAlgorithm.HS256, digest, timeout.Token).ConfigureAwait(false);
            return result.KeyId is { } kid && kid.EndsWith($"/keys/{configuration.KeyName}/{versionId}", StringComparison.Ordinal)
                ? new HsmCallResult(HsmOutcome.Success, 200) { KeyIdentifier = kid, Bytes = result.Signature }
                : new HsmCallResult(HsmOutcome.WrongKeyIdentifier) { KeyIdentifier = result.KeyId };
        }
        catch (RequestFailedException ex)
        {
            var outcome = ex.Status switch
            {
                401 => HsmOutcome.Unauthenticated,
                403 => HsmOutcome.Forbidden,
                404 => HsmOutcome.NotFound,
                429 => HsmOutcome.Throttled,
                >= 500 => HsmOutcome.ServiceError,
                0 => HsmOutcome.Unreachable,
                _ => HsmOutcome.Rejected,
            };
            return new HsmCallResult(outcome, ex.Status == 0 ? null : ex.Status, ex.ErrorCode, "Azure SDK RequestFailedException");
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
        {
            return new HsmCallResult(HsmOutcome.Unauthenticated, ErrorMessage: "credential could not obtain a token (" + ex.GetType().Name + ")");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HsmCallResult(HsmOutcome.Timeout, ErrorMessage: "SDK call timed out");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return new HsmCallResult(HsmOutcome.Rejected, ErrorMessage: "Azure SDK refused the operation locally (" + ex.GetType().Name + ")");
        }
    }

    internal static CryptographyClientOptions.ServiceVersion ServiceVersion(string apiVersion) => apiVersion switch
    {
        "7.6" => CryptographyClientOptions.ServiceVersion.V7_6,
        "2025-07-01" => CryptographyClientOptions.ServiceVersion.V2025_07_01,
        _ => throw new ProbeConfigurationException([$"unsupported ApiVersion '{apiVersion}'"]),
    };
}
