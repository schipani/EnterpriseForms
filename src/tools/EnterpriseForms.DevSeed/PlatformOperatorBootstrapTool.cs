using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnterpriseForms.DevSeed;

/// <summary>
/// C8 M2 Stage 3 development procedure, driven by <c>scripts/devstack/devstack.py platform-console</c>. DEVELOPMENT ONLY.
/// Reads one JSON request on stdin (custodian private keys travel only there) and calls the Authority operator plane:
/// <c>status</c> returns the Platform Operator registry and platform journal state; <c>bootstrap</c> runs the recovery ceremony
/// (always a dry run first; executes only when asked, with the two DR-C7-3 synthetic custodian approvals signed over the dry run's
/// statement). The Authority itself refuses the ceremony while two or more operators are active, so it can never add a third
/// operator beside an existing quorum or reinstate anyone. Output never contains keys or approvals.
/// </summary>
internal static class PlatformOperatorBootstrapTool
{
    private static readonly JsonSerializerOptions Output = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string authority)
    {
        var request = JsonNode.Parse(await Console.In.ReadToEndAsync().ConfigureAwait(false))?.AsObject() ?? throw new InvalidOperationException("a JSON request is required on stdin");
        var bearer = Program.Bearer(Program.Env("DEVSEED_OPERATOR_ISSUER", "authority-operator"), Program.Env("DEVSEED_OPERATOR_AUDIENCE", "authorization-authority-operator"), Program.Env("DEVSEED_OPERATOR_SHARED_KEY_BASE64", string.Empty));
        using var client = new HttpClient { BaseAddress = new Uri(authority), Timeout = TimeSpan.FromSeconds(60) };
        JsonObject result;
        switch (request["action"]?.GetValue<string>())
        {
            case "status":
                result = await SendAsync(client, HttpMethod.Get, "internal/v1/operator/platform-operators", bearer, null).ConfigureAwait(false);
                break;
            case "bootstrap":
                const string path = "internal/v1/operator/platform-operators/bootstrap";
                var body = new JsonObject
                {
                    ["operation_id"] = request["operation_id"]?.GetValue<string>() ?? throw new InvalidOperationException("operation_id is required"),
                    ["operators"] = request["operators"]?.DeepClone() ?? throw new InvalidOperationException("operators are required"),
                    ["justification"] = request["justification"]?.GetValue<string>() ?? throw new InvalidOperationException("justification is required"),
                    ["incident_reference"] = request["incident_reference"]?.GetValue<string>(),
                    ["dry_run"] = true,
                };
                var preview = await SendAsync(client, HttpMethod.Post, path, bearer, body).ConfigureAwait(false);
                if (request["execute"]?.GetValue<bool>() != true || preview["error_code"] is not null || preview["eligible"]?.GetValue<bool>() != true)
                {
                    Console.WriteLine(preview.ToJsonString(Output));
                    return preview["error_code"] is null ? 0 : 3;
                }

                var approvals = new JsonArray();
                foreach (var approver in request["approvers"]?.AsArray() ?? throw new InvalidOperationException("custodian approver keys are required to execute"))
                {
                    approvals.Add(OperatorSecurityAdministratorTool.Sign(approver!["approver_id"]!.GetValue<string>(), approver["private_key_pkcs8_base64"]!.GetValue<string>(), preview));
                }

                body["dry_run"] = false;
                body["approvals"] = approvals;
                result = await SendAsync(client, HttpMethod.Post, path, bearer, body).ConfigureAwait(false);
                break;
            default:
                Console.Error.WriteLine("action must be status or bootstrap");
                return 64;
        }

        Console.WriteLine(result.ToJsonString(Output));
        return result["error_code"] is null ? 0 : 3;
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
        return response.IsSuccessStatusCode
            ? parsed ?? []
            : new JsonObject { ["http_status"] = (int)response.StatusCode, ["error_code"] = parsed?["error_code"]?.GetValue<string>() ?? "HTTP_" + (int)response.StatusCode, ["detail"] = parsed?["detail"]?.GetValue<string>() };
    }
}
