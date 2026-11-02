using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnterpriseForms.DevSeed;

/// <summary>
/// C8 M2 owner-acceptance fixture (DEVSEED_MODE=seed-acceptance). DEVELOPMENT ONLY. There is deliberately no operator path that
/// adds an owner to an EXISTING organization (owner changes need two owners), so two-owner approval can only be exercised in an
/// organization bootstrapped with several owners. This mode bootstraps ONE additional synthetic organization through the same
/// authority operator plane the normal seed uses, and merges its personas into dev-personas.json without touching the personas or
/// data of the original organization. Idempotent: ids are persisted next to the output and a bootstrapped organization is reused.
/// </summary>
internal static class AcceptanceOrganizationTool
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string authority, string output)
    {
        var operatorIssuer = Program.Env("DEVSEED_OPERATOR_ISSUER", "authority-operator");
        var operatorAudience = Program.Env("DEVSEED_OPERATOR_AUDIENCE", "authorization-authority-operator");
        var operatorKey = Program.Env("DEVSEED_OPERATOR_SHARED_KEY_BASE64", string.Empty);
        var idpIssuer = Program.Env("DEVSEED_TEST_IDP_ISSUER", "https://test-idp.enterpriseforms.invalid");
        var organizationName = Program.Env("DEVSEED_ACCEPTANCE_ORGANIZATION_NAME", "Acceptance Crown Office (synthetic, two owners)");

        var statePath = output + ".acceptance.ids";
        var state = File.Exists(statePath) ? JsonNode.Parse(await File.ReadAllTextAsync(statePath).ConfigureAwait(false))!.AsObject() : new JsonObject();
        Guid Id(string key)
        {
            if (state[key] is { } existing)
            {
                return Guid.Parse(existing.GetValue<string>());
            }

            var created = Guid.CreateVersion7();
            state[key] = created.ToString("D");
            return created;
        }

        var organizationId = Id("organization");
        var personas = new List<Persona>
        {
            new("acc-owner-1", "Alex Owner One (acceptance)", Id("owner1"), "acc-owner-1", organizationId, organizationName, ["organization_owner", "designer"]),
            new("acc-owner-2", "Blair Owner Two (acceptance)", Id("owner2"), "acc-owner-2", organizationId, organizationName, ["organization_owner"]),
            new("acc-owner-3", "Cameron Owner Three (acceptance)", Id("owner3"), "acc-owner-3", organizationId, organizationName, ["organization_owner"]),
            new("acc-designer", "Drew Designer (acceptance)", Id("designer"), "acc-designer", organizationId, organizationName, ["designer"]),
            new("acc-clerk", "Emery Clerk (acceptance)", Id("clerk"), "acc-clerk", organizationId, organizationName, []),
            new("acc-reader", "Finley Reader (acceptance)", Id("reader"), "acc-reader", organizationId, organizationName, []),
            new("acc-security", "Gray Security (acceptance, System Administrator)", Id("security"), "acc-security", organizationId, organizationName, ["system_administrator"]),
        };

        if (Program.Env("DEVSEED_PREPARE_ONLY", string.Empty) == "1")
        {
            // Lets the operator tool name the organization's Vault key before bootstrap (the Authority's Vault policy is pinned per key).
            await File.WriteAllTextAsync(statePath, state.ToJsonString(Json)).ConfigureAwait(false);
            Console.WriteLine("ORGANIZATION_ID=" + organizationId.ToString("D"));
            return 0;
        }

        using var client = new HttpClient { BaseAddress = new Uri(authority), Timeout = TimeSpan.FromSeconds(60) };
        var bearer = Program.Bearer(operatorIssuer, operatorAudience, operatorKey);
        using (var gate = new HttpRequestMessage(HttpMethod.Get, new Uri($"internal/v1/operator/organizations/{organizationId:D}/gate", UriKind.Relative)))
        {
            gate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var gateResponse = await client.SendAsync(gate).ConfigureAwait(false);
            if (gateResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var bootstrap = new
                {
                    organization_id = organizationId,
                    organization_display_name = organizationName,
                    identities = personas.Select(p => new { authority_user_id = p.AuthorityUserId, issuer_uri = idpIssuer, subject_identifier = p.SubjectIdentifier, organization_roles = p.OrganizationRoles }).ToArray(),
                };
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("internal/v1/operator/organizations", UriKind.Relative)) { Content = JsonContent.Create(bootstrap) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine("acceptance bootstrap failed: " + (int)response.StatusCode + " " + await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    return 2;
                }

                Console.WriteLine("Acceptance organization bootstrapped: " + organizationId.ToString("D"));
            }
            else if (gateResponse.IsSuccessStatusCode)
            {
                Console.WriteLine("Acceptance organization already bootstrapped: " + organizationId.ToString("D"));
            }
            else
            {
                Console.Error.WriteLine("operator gate check failed: " + (int)gateResponse.StatusCode);
                return 3;
            }
        }

        // Merge: existing personas are preserved byte-for-meaning; only absent keys are appended.
        var merged = File.Exists(output) ? JsonNode.Parse(await File.ReadAllTextAsync(output).ConfigureAwait(false))!.AsArray() : [];
        var present = merged.Select(n => n!["personaKey"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var persona in personas.Where(p => !present.Contains(p.PersonaKey)))
        {
            merged.Add(JsonSerializer.SerializeToNode(persona, Json));
        }

        await File.WriteAllTextAsync(output, merged.ToJsonString(Json)).ConfigureAwait(false);
        await File.WriteAllTextAsync(statePath, state.ToJsonString(Json)).ConfigureAwait(false);
        Console.WriteLine("Personas now in " + output + ": " + merged.Count.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private sealed record Persona(string PersonaKey, string DisplayName, Guid AuthorityUserId, string SubjectIdentifier, Guid OrganizationId, string OrganizationDisplayName, IReadOnlyList<string> OrganizationRoles);
}
