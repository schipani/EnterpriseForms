using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnterpriseForms.DevSeed;

/// <summary>
/// DR-C7-3 development procedure, driven by <c>scripts/devstack/devstack.py security-admin</c>. DEVELOPMENT ONLY.
/// <list type="bullet">
/// <item><c>generate-approver-keys</c>: two P-256 key pairs for the development-only synthetic operator approvers. Printed to
/// stdout for the devstack custody file only (devstack never echoes them); the Authority receives the public halves.</item>
/// <item><c>operator-security-admin</c>: reads one JSON request on stdin (approver private keys travel only there) and calls
/// the Authority operator plane: status, or a first appointment / recovery that always starts with a dry run and executes
/// only when the request says so, with two approvals signed by the two synthetic approver keys over the dry run's
/// statement. Output never contains keys or approvals.</item>
/// </list>
/// The approvals are synthetic (one person holds both development keys); the Authority records them as such.
/// </summary>
internal static class OperatorSecurityAdministratorTool
{
    private static readonly JsonSerializerOptions Output = new() { WriteIndented = true };

    public static int GenerateApproverKeys()
    {
        var approvers = new JsonArray();
        for (var index = 1; index <= 2; index++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            approvers.Add(new JsonObject
            {
                ["approver_id"] = "devstack-synthetic-approver-" + index,
                ["private_key_pkcs8_base64"] = Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
                ["public_key_spki_base64"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            });
        }

        Console.WriteLine(new JsonObject { ["purpose"] = "DEVELOPMENT ONLY synthetic operator approver keys (DR-C7-3); not two-human approval evidence", ["approvers"] = approvers }.ToJsonString(Output));
        return 0;
    }

    public static async Task<int> RunAsync(string authority, string personasPath)
    {
        var request = JsonNode.Parse(await Console.In.ReadToEndAsync().ConfigureAwait(false))?.AsObject() ?? throw new InvalidOperationException("a JSON request is required on stdin");
        var bearer = Program.Bearer(Program.Env("DEVSEED_OPERATOR_ISSUER", "authority-operator"), Program.Env("DEVSEED_OPERATOR_AUDIENCE", "authorization-authority-operator"), Program.Env("DEVSEED_OPERATOR_SHARED_KEY_BASE64", string.Empty));
        var personas = File.Exists(personasPath) ? JsonNode.Parse(await File.ReadAllTextAsync(personasPath).ConfigureAwait(false))!.AsArray() : [];
        var ids = File.Exists(personasPath + ".ids") ? JsonNode.Parse(await File.ReadAllTextAsync(personasPath + ".ids").ConfigureAwait(false))!.AsObject() : null;
        var organizationId = request["organization_id"]?.GetValue<string>() ?? ids?["organization"]?.GetValue<string>() ?? throw new InvalidOperationException("organization_id unknown (no dev-seed ids file)");
        using var client = new HttpClient { BaseAddress = new Uri(authority), Timeout = TimeSpan.FromSeconds(60) };

        var status = await SendAsync(client, HttpMethod.Get, $"internal/v1/operator/organizations/{organizationId}/security-administrators", bearer, null).ConfigureAwait(false);
        if (status["error_code"] is not null)
        {
            Console.WriteLine(status.ToJsonString(Output));
            return 3;
        }

        foreach (var member in status["members"]!.AsArray())
        {
            var persona = personas.FirstOrDefault(p => p!["authorityUserId"]?.GetValue<string>() == member!["authority_user_id"]!.GetValue<string>());
            member!["persona_key"] = persona?["personaKey"]?.GetValue<string>();
            member["display_name"] = persona?["displayName"]?.GetValue<string>();
        }

        var action = request["action"]?.GetValue<string>();
        if (action == "status")
        {
            Console.WriteLine(status.ToJsonString(Output));
            return 0;
        }

        if (action is not ("first-appointment" or "recovery"))
        {
            Console.Error.WriteLine("action must be status, first-appointment or recovery");
            return 64;
        }

        // The target: a persona key (resolved through the seed file) or an explicit user id; identity always from the Authority.
        var targetId = request["target_authority_user_id"]?.GetValue<string>()
                       ?? personas.FirstOrDefault(p => p!["personaKey"]?.GetValue<string>() == request["persona"]?.GetValue<string>())?["authorityUserId"]?.GetValue<string>()
                       ?? throw new InvalidOperationException("persona or target_authority_user_id required");
        var target = status["members"]!.AsArray().FirstOrDefault(m => m!["authority_user_id"]!.GetValue<string>() == targetId)
                     ?? throw new InvalidOperationException("the target is not a member of the organization at the Authority");
        var body = new JsonObject
        {
            ["operation_id"] = request["operation_id"]?.GetValue<string>() ?? throw new InvalidOperationException("operation_id required"),
            ["confirm_organization_id"] = request["confirm_organization_id"]?.GetValue<string>() ?? organizationId,
            ["target_authority_user_id"] = targetId,
            ["target_issuer_uri"] = target["issuer_uri"]!.GetValue<string>(),
            ["target_subject_identifier"] = target["subject_identifier"]!.GetValue<string>(),
            ["justification"] = request["justification"]?.GetValue<string>() ?? string.Empty,
            ["dry_run"] = true,
        };
        if (action == "recovery")
        {
            body["incident_reference"] = request["incident_reference"]?.GetValue<string>();
            body["removed_administrator_ids"] = request["removed_administrator_ids"]?.DeepClone() ?? new JsonArray();
        }

        var path = $"internal/v1/operator/organizations/{organizationId}/security-administrators/{action}";
        var preview = await SendAsync(client, HttpMethod.Post, path, bearer, body).ConfigureAwait(false);
        preview["target_display_name"] = target["display_name"]?.GetValue<string>();
        if (request["execute"]?.GetValue<bool>() != true || preview["error_code"] is not null || preview["eligible"]?.GetValue<bool>() != true)
        {
            Console.WriteLine(preview.ToJsonString(Output));
            return preview["error_code"] is null ? 0 : 3;
        }

        var approvers = request["approvers"]?.AsArray() ?? throw new InvalidOperationException("approver keys are required to execute");
        var approvals = new JsonArray();
        foreach (var approver in approvers)
        {
            approvals.Add(Sign(approver!["approver_id"]!.GetValue<string>(), approver["private_key_pkcs8_base64"]!.GetValue<string>(), preview));
        }

        body["dry_run"] = false;
        body["approvals"] = approvals;
        var outcome = await SendAsync(client, HttpMethod.Post, path, bearer, body).ConfigureAwait(false);
        outcome["target_display_name"] = target["display_name"]?.GetValue<string>();
        Console.WriteLine(outcome.ToJsonString(Output));
        return outcome["error_code"] is null ? 0 : 3;
    }

    private static string Sign(string approverId, string privateKeyBase64, JsonObject preview)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        var now = DateTimeOffset.UtcNow;
        var claims = new JsonObject
        {
            ["iss"] = "enterpriseforms-operator-approval",
            ["aud"] = "authorization-authority-operator-approval",
            ["sub"] = approverId,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-5).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
            ["statement_sha256"] = preview["approval_statement_sha256"]!.GetValue<string>(),
            ["operation_id"] = preview["operation_id"]!.GetValue<string>(),
            ["operation_kind"] = preview["operation_kind"]!.GetValue<string>(),
            ["organization_id"] = preview["organization_id"]!.GetValue<string>(),
            ["target_authority_user_id"] = preview["target"]!["authority_user_id"]!.GetValue<string>(),
        };
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(new JsonObject { ["alg"] = "ES256", ["typ"] = "JWT", ["kid"] = approverId }.ToJsonString()));
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        return header + "." + payload + "." + Base64Url.EncodeToString(key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256));
    }

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string path, string bearer, JsonObject? body)
    {
        using var message = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(message).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var parsed = text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null;
        if (!response.IsSuccessStatusCode)
        {
            return new JsonObject { ["http_status"] = (int)response.StatusCode, ["error_code"] = parsed?["error_code"]?.GetValue<string>() ?? "HTTP_" + (int)response.StatusCode, ["detail"] = parsed?["detail"]?.GetValue<string>() };
        }

        return parsed ?? [];
    }
}
