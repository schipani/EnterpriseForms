using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnterpriseForms.DevSeed;

/// <summary>
/// C8 onboarding (C-104) development procedure, driven by <c>scripts/devstack/devstack.py onboarding</c>. DEVELOPMENT ONLY. Reads one
/// JSON request on stdin.
/// <list type="bullet">
/// <item><c>key-custody</c>: the Authority operator plane's read-only key custody status of one onboarding request (which single key
/// may be provisioned and whether now). The Authority never creates the key; the custody credential lives with devstack, not here.</item>
/// <item><c>personas</c>: merges development login personas for the initial owners of an initialized organization into dev-personas.json
/// (persona key <c>onb-&lt;subject&gt;</c>); existing personas are never changed. The owners' ids come from the Authority's own records,
/// supplied by devstack.</item>
/// </list>
/// </summary>
internal static class OrganizationOnboardingTool
{
    private static readonly JsonSerializerOptions Output = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string authority, string personasPath)
    {
        var request = JsonNode.Parse(await Console.In.ReadToEndAsync().ConfigureAwait(false))?.AsObject() ?? throw new InvalidOperationException("a JSON request is required on stdin");
        switch (request["action"]?.GetValue<string>())
        {
            case "key-custody":
                var requestId = Guid.Parse(request["request_id"]?.GetValue<string>() ?? throw new InvalidOperationException("request_id is required"));
                var bearer = Program.Bearer(Program.Env("DEVSEED_OPERATOR_ISSUER", "authority-operator"), Program.Env("DEVSEED_OPERATOR_AUDIENCE", "authorization-authority-operator"), Program.Env("DEVSEED_OPERATOR_SHARED_KEY_BASE64", string.Empty));
                using (var client = new HttpClient { BaseAddress = new Uri(authority), Timeout = TimeSpan.FromSeconds(60) })
                using (var message = new HttpRequestMessage(HttpMethod.Get, new Uri($"internal/v1/operator/organization-onboarding/{requestId:D}/key-custody", UriKind.Relative)))
                {
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    using var response = await client.SendAsync(message).ConfigureAwait(false);
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var parsed = text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null;
                    var result = response.IsSuccessStatusCode ? parsed ?? [] : new JsonObject { ["http_status"] = (int)response.StatusCode, ["error_code"] = parsed?["error_code"]?.GetValue<string>() ?? "HTTP_" + (int)response.StatusCode, ["detail"] = parsed?["detail"]?.GetValue<string>() };
                    Console.WriteLine(result.ToJsonString(Output));
                    return result["error_code"] is null ? 0 : 3;
                }

            case "personas":
                var merged = File.Exists(personasPath) ? JsonNode.Parse(await File.ReadAllTextAsync(personasPath).ConfigureAwait(false))!.AsArray() : [];
                var present = merged.Select(n => n!["personaKey"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                var added = 0;
                foreach (var owner in request["owners"]?.AsArray() ?? throw new InvalidOperationException("owners are required"))
                {
                    var subject = owner!["subject_identifier"]!.GetValue<string>();
                    var key = "onb-" + subject;
                    if (present.Contains(key))
                    {
                        continue;
                    }

                    merged.Add(new JsonObject
                    {
                        ["personaKey"] = key,
                        ["displayName"] = owner["display_name"]!.GetValue<string>() + " (onboarded owner)",
                        ["authorityUserId"] = owner["authority_user_id"]!.GetValue<string>(),
                        ["subjectIdentifier"] = subject,
                        ["organizationId"] = request["organization_id"]!.GetValue<string>(),
                        ["organizationDisplayName"] = request["organization_display_name"]!.GetValue<string>(),
                        ["organizationRoles"] = new JsonArray("organization_owner"),
                    });
                    added++;
                }

                await File.WriteAllTextAsync(personasPath, merged.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })).ConfigureAwait(false);
                Console.WriteLine(new JsonObject { ["added"] = added, ["personas"] = merged.Count }.ToJsonString(Output));
                Console.Error.WriteLine("Personas now in " + personasPath + ": " + merged.Count.ToString(CultureInfo.InvariantCulture));
                return 0;

            default:
                Console.Error.WriteLine("action must be key-custody or personas");
                return 64;
        }
    }
}
