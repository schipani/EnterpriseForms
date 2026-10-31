using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnterpriseForms.DevSeed;

/// <summary>
/// C8 M2 (AC-7.4) development procedure, driven by <c>scripts/devstack/devstack.py membership</c>. DEVELOPMENT ONLY.
/// Reads one JSON request on stdin (approver private keys travel only there) and calls the Authority operator plane:
/// <c>list</c> the organization's membership requests, <c>approve</c> one (always a dry run first; executes only when asked,
/// with two approvals signed by the two synthetic approver keys over the dry run's statement) or <c>reject</c> one. Output
/// never contains keys or approvals. The approvals are synthetic (one person holds both keys); the Authority records them so.
/// </summary>
internal static class OperatorMembershipTool
{
    private static readonly JsonSerializerOptions Output = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string authority, string personasPath)
    {
        var request = JsonNode.Parse(await Console.In.ReadToEndAsync().ConfigureAwait(false))?.AsObject() ?? throw new InvalidOperationException("a JSON request is required on stdin");
        var bearer = Program.Bearer(Program.Env("DEVSEED_OPERATOR_ISSUER", "authority-operator"), Program.Env("DEVSEED_OPERATOR_AUDIENCE", "authorization-authority-operator"), Program.Env("DEVSEED_OPERATOR_SHARED_KEY_BASE64", string.Empty));
        var ids = File.Exists(personasPath + ".ids") ? JsonNode.Parse(await File.ReadAllTextAsync(personasPath + ".ids").ConfigureAwait(false))!.AsObject() : null;
        var organizationId = request["organization_id"]?.GetValue<string>() ?? ids?["organization"]?.GetValue<string>() ?? throw new InvalidOperationException("organization_id unknown (no dev-seed ids file)");
        using var client = new HttpClient { BaseAddress = new Uri(authority), Timeout = TimeSpan.FromSeconds(60) };
        var basePath = $"internal/v1/operator/organizations/{organizationId}/membership-requests";
        JsonObject result;
        switch (request["action"]?.GetValue<string>())
        {
            case "list":
                result = await SendAsync(client, HttpMethod.Get, basePath, bearer, null).ConfigureAwait(false);
                break;
            case "reject":
                result = await SendAsync(client, HttpMethod.Post, $"{basePath}/{Required(request, "request_id")}/reject", bearer,
                    new JsonObject { ["confirm_organization_id"] = organizationId, ["reason"] = Required(request, "reason") }).ConfigureAwait(false);
                break;
            case "approve":
                var path = $"{basePath}/{Required(request, "request_id")}/approve";
                var body = new JsonObject { ["confirm_organization_id"] = organizationId, ["justification"] = Required(request, "justification"), ["dry_run"] = true };
                var preview = await SendAsync(client, HttpMethod.Post, path, bearer, body).ConfigureAwait(false);
                if (request["execute"]?.GetValue<bool>() != true || preview["error_code"] is not null || preview["eligible"]?.GetValue<bool>() != true)
                {
                    Console.WriteLine(preview.ToJsonString(Output));
                    return preview["error_code"] is null ? 0 : 3;
                }

                var approvals = new JsonArray();
                foreach (var approver in request["approvers"]?.AsArray() ?? throw new InvalidOperationException("approver keys are required to execute"))
                {
                    approvals.Add(OperatorSecurityAdministratorTool.Sign(approver!["approver_id"]!.GetValue<string>(), approver["private_key_pkcs8_base64"]!.GetValue<string>(), preview));
                }

                body["dry_run"] = false;
                body["approvals"] = approvals;
                result = await SendAsync(client, HttpMethod.Post, path, bearer, body).ConfigureAwait(false);
                break;
            default:
                Console.Error.WriteLine("action must be list, approve or reject");
                return 64;
        }

        Console.WriteLine(result.ToJsonString(Output));
        return result["error_code"] is null ? 0 : 3;
    }

    private static string Required(JsonObject request, string name) => request[name]?.GetValue<string>() ?? throw new InvalidOperationException(name + " is required");

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
        return response.IsSuccessStatusCode
            ? parsed ?? []
            : new JsonObject { ["http_status"] = (int)response.StatusCode, ["error_code"] = parsed?["error_code"]?.GetValue<string>() ?? "HTTP_" + (int)response.StatusCode, ["detail"] = parsed?["detail"]?.GetValue<string>() };
    }
}
