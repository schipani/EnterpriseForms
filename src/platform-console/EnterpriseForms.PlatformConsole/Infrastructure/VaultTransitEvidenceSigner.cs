using System.Buffers.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnterpriseForms.PlatformConsole.Composition;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.PlatformConsole.Infrastructure;

/// <summary>Produces one raw ES256 signature (r‖s, 64 bytes) with one named operator key. The private key never leaves the signer.</summary>
public interface IOperatorEvidenceSigner
{
    Task<byte[]> SignAsync(string keyName, byte[] signingInput, CancellationToken cancellationToken);
}

/// <summary>
/// Development/Testing evidence signing in the local Vault Transit (D8-35): one non-exportable ECDSA P-256 key per synthetic operator;
/// <c>POST /v1/transit/sign/{key}/sha2-256</c> with <c>marshaling_algorithm=jws</c> returns the JWS signature. The console's Vault
/// token is limited (Vault policy) to <c>update</c> on the exact sign paths of the configured operator keys: it cannot read, export,
/// create, rotate or delete a key, and cannot use any other key (organization HMAC keys included). This class refuses any key that
/// is not one of the configured operator keys, so a code path that computed another name still could not sign with it. Callers pass
/// only the key of the signed-in operator (<see cref="Features.OperatorApprovals.OperatorDecisionService"/>).
/// </summary>
public sealed class VaultTransitEvidenceSigner(HttpClient http, IOptions<PlatformConsoleOptions> options) : IOperatorEvidenceSigner
{
    public async Task<byte[]> SignAsync(string keyName, byte[] signingInput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signingInput);
        var settings = options.Value;
        if (!PlatformConsoleOptionsValidation.IsKeyName(keyName) || settings.Operators.All(o => !string.Equals(o.VaultKeyName, keyName, StringComparison.Ordinal)))
        {
            throw new ConsoleException(ConsoleErrorCodes.SigningKeyMismatch, 403, "only a configured operator's own key can sign");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"v1/transit/sign/{keyName}/sha2-256", UriKind.Relative))
        {
            Content = JsonContent.Create(new JsonObject { ["input"] = Convert.ToBase64String(signingInput), ["marshaling_algorithm"] = "jws" }),
        };
        message.Headers.Add("X-Vault-Token", settings.Vault.Token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ConsoleException(ConsoleErrorCodes.SigningUnavailable, 503, "the evidence signer (Vault Transit) could not be reached", retryable: true);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Status only: Vault error bodies are not forwarded (they can name paths and policies).
                throw new ConsoleException(ConsoleErrorCodes.SigningUnavailable, 503, $"the evidence signer refused the request (HTTP {(int)response.StatusCode})", retryable: (int)response.StatusCode >= 500);
            }

            string? signature;
            try
            {
                var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
                signature = body?["data"]?["signature"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                signature = null;
            }

            // "vault:v<version>:<base64url r||s>"
            var parts = signature?.Split(':');
            if (parts is not { Length: 3 } || parts[0] != "vault" || !parts[1].StartsWith('v'))
            {
                throw new ConsoleException(ConsoleErrorCodes.SigningUnavailable, 503, "the evidence signer returned an unexpected answer");
            }

            byte[] raw;
            try
            {
                raw = Base64Url.DecodeFromChars(parts[2]);
            }
            catch (FormatException)
            {
                raw = [];
            }

            return raw.Length == 64 ? raw : throw new ConsoleException(ConsoleErrorCodes.SigningUnavailable, 503, "the evidence signer returned a signature of the wrong form (an ES256 key with JWS marshaling is required)");
        }
    }
}
