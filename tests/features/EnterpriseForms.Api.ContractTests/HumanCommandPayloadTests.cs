using System.Text.Json;
using System.Text.Json.Nodes;
using EnterpriseForms.Application.Common.Authority;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

public sealed class HumanCommandPayloadTests
{
    [Theory]
    [InlineData("""{"record_share_id":"share","form_record_id":"record"}""")]
    [InlineData("""{"form_record_id":"record","new_owner_user_id":"user","new_owner_team_id":null}""")]
    [InlineData("""{"record_share_id":"share","form_record_id":"record","recipient_user_id":"user","recipient_team_id":null,"recipient_role":"Reader","field_item_ids":["item"]}""")]
    public void DefaultPayloadKeepsTheLegacyRequestBytes(string legacy)
    {
        var fields = JsonNode.Parse(legacy)!.AsObject();
        var payload = HumanCommandPayload.Serialize(fields, useDevelopmentStepUp: false);
        Assert.Equal(legacy, payload.GetRawText());
        Assert.False(payload.TryGetProperty("step_up_evidence", out _));
    }

    [Fact]
    public void ExplicitSyntheticIntentProducesAStableBodyWithoutMutatingTheFields()
    {
        var fields = new { record_share_id = "share", form_record_id = "record" };
        var first = HumanCommandPayload.Serialize(fields, useDevelopmentStepUp: true);
        var replay = HumanCommandPayload.Serialize(fields, useDevelopmentStepUp: true);

        Assert.Equal("synthetic-development-step-up", first.GetProperty("step_up_evidence").GetString());
        Assert.Equal(first.GetRawText(), replay.GetRawText());
        Assert.False(JsonSerializer.SerializeToElement(fields).TryGetProperty("step_up_evidence", out _));
    }
}
