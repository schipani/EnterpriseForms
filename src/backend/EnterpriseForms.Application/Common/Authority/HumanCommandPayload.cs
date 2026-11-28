using System.Text.Json;

namespace EnterpriseForms.Application.Common.Authority;

internal static class HumanCommandPayload
{
    private const string SyntheticStepUpMarker = "synthetic-development-step-up";

    public static JsonElement Serialize<T>(T fields, bool useDevelopmentStepUp)
    {
        // Preserve the pre-C10 request digest when callers replay without step-up.
        if (!useDevelopmentStepUp)
        {
            return JsonSerializer.SerializeToElement(fields);
        }

        // Acceptance of the synthetic marker is always decided by the Authority.
        var payload = JsonSerializer.SerializeToNode(fields)!.AsObject();
        payload["step_up_evidence"] = SyntheticStepUpMarker;
        return JsonSerializer.SerializeToElement(payload);
    }
}
