using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.GetFormRecord;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// FD-01 â€” Form Designer layout fidelity. The optional <c>columnStart</c> placement property (schema v2, additive) is
/// validated, preserved through the canonical codec that every draft save, publish and restore uses, omitted when absent so
/// legacy layouts serialize byte-for-byte as before, and removed from authorization-reduced (partial) projections so no
/// reserved cell can reveal an unauthorized field.
/// </summary>
public sealed class FD01LayoutFidelityTests
{
    private static readonly Guid FieldA = Guid.Parse("0199a2b3-c4d5-7e6f-8123-000000000001");
    private static readonly Guid FieldB = Guid.Parse("0199a2b3-c4d5-7e6f-8123-000000000002");
    private static readonly Guid FieldC = Guid.Parse("0199a2b3-c4d5-7e6f-8123-000000000003");

    private static string Layout(string placementA, string placementB = """{"columnSpan":1,"startOnNewRow":false}""") =>
        $$$"""
        {"layoutSchemaVersion":2,"title":"Form","sections":[{"sectionKey":"main","title":"Main","gridColumns":4,"elements":[
          {"kind":"field","fieldDefinitionId":"{{{FieldA}}}","control":"text","label":"A","layout":{{{placementA}}}},
          {"kind":"field","fieldDefinitionId":"{{{FieldB}}}","control":"text","label":"B","layout":{{{placementB}}}}
        ]}]}
        """;

    [Fact]
    public void ColumnStartIsParsedAndPreservedThroughCanonicalSerialization()
    {
        var layout = FormLayoutCodec.Parse(Layout("""{"columnSpan":1,"startOnNewRow":false,"columnStart":3}""", """{"columnSpan":2,"startOnNewRow":true,"columnStart":2}"""));
        Assert.Equal(new LayoutPlacement(1, false, 3), layout.Sections[0].Elements[0].Layout);
        Assert.Equal(new LayoutPlacement(2, true, 2), layout.Sections[0].Elements[1].Layout);

        // Save draft, publish and restore all canonicalize through Parse â†’ Serialize; a second pass is a fixed point.
        var canonical = FormLayoutCodec.Serialize(layout);
        Assert.Contains("\"layout\":{\"columnSpan\":1,\"startOnNewRow\":false,\"columnStart\":3}", canonical, StringComparison.Ordinal);
        Assert.Contains("\"layout\":{\"columnSpan\":2,\"startOnNewRow\":true,\"columnStart\":2}", canonical, StringComparison.Ordinal);
        Assert.Equal(canonical, FormLayoutCodec.Serialize(FormLayoutCodec.Parse(canonical)));
    }

    [Fact]
    public void LegacyLayoutsWithoutColumnStartSerializeExactlyAsBefore()
    {
        var layout = FormLayoutCodec.Parse(Layout("""{"columnSpan":2,"startOnNewRow":false}"""));
        Assert.Null(layout.Sections[0].Elements[0].Layout.ColumnStart);
        var canonical = FormLayoutCodec.Serialize(layout);
        Assert.DoesNotContain("columnStart", canonical, StringComparison.Ordinal);
        Assert.Contains("\"layout\":{\"columnSpan\":2,\"startOnNewRow\":false}", canonical, StringComparison.Ordinal);
        // An explicit JSON null is the same as absent (omitted on write).
        var withNull = FormLayoutCodec.Parse(Layout("""{"columnSpan":2,"startOnNewRow":false,"columnStart":null}"""));
        Assert.Equal(canonical, FormLayoutCodec.Serialize(withNull));
    }

    [Fact]
    public void LegacyLayoutsWithoutPlacementKeepTheirDefaults()
    {
        var json = $$$"""{"layoutSchemaVersion":1,"title":"Old","sections":[{"sectionKey":"main","title":"Main","elements":[{"kind":"field","fieldDefinitionId":"{{{FieldA}}}","label":"A"}]}]}""";
        var layout = FormLayoutCodec.Parse(json);
        Assert.Equal(new LayoutPlacement(4, false), layout.Sections[0].Elements[0].Layout);
        Assert.DoesNotContain("columnStart", FormLayoutCodec.Serialize(layout), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"columnSpan":1,"startOnNewRow":false,"columnStart":0}""", "columnStart must be 1..4")]
    [InlineData("""{"columnSpan":1,"startOnNewRow":false,"columnStart":5}""", "columnStart must be 1..4")]
    [InlineData("""{"columnSpan":1,"startOnNewRow":false,"columnStart":2.5}""", "columnStart must be 1..4")]
    [InlineData("""{"columnSpan":1,"startOnNewRow":false,"columnStart":"3"}""", "columnStart must be 1..4")]
    [InlineData("""{"columnSpan":2,"startOnNewRow":false,"columnStart":4}""", "extend beyond the four-column grid")]
    [InlineData("""{"columnSpan":4,"startOnNewRow":false,"columnStart":2}""", "extend beyond the four-column grid")]
    [InlineData("""{"columnSpan":5,"startOnNewRow":false,"columnStart":1}""", "columnSpan must be 1..4")]
    public void InvalidColumnPositionsAndSpansAreRejected(string placement, string message)
    {
        var error = Assert.Throws<BusinessException>(() => FormLayoutCodec.Parse(Layout(placement)));
        Assert.Equal("LAYOUT_INVALID", error.ErrorCode);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryValidColumnAndSpanCombinationIsAccepted()
    {
        for (var span = 1; span <= 4; span++)
        {
            for (var start = 1; start + span - 1 <= 4; start++)
            {
                var layout = FormLayoutCodec.Parse(Layout("{\"columnSpan\":" + span + ",\"startOnNewRow\":false,\"columnStart\":" + start + "}"));
                Assert.Equal(new LayoutPlacement(span, false, start), layout.Sections[0].Elements[0].Layout);
            }
        }
    }

    [Fact]
    public void PartialProjectionRemovesUnauthorizedFieldsAndEveryExplicitPosition()
    {
        var json = $$$"""
        {"layoutSchemaVersion":2,"title":"Form","sections":[{"sectionKey":"main","title":"Main","gridColumns":4,"elements":[
          {"kind":"field","fieldDefinitionId":"{{{FieldA}}}","control":"text","label":"Secret salary","layout":{"columnSpan":1,"startOnNewRow":false,"columnStart":1}},
          {"kind":"field","fieldDefinitionId":"{{{FieldB}}}","control":"text","label":"Name","layout":{"columnSpan":1,"startOnNewRow":false,"columnStart":2}},
          {"kind":"field","fieldDefinitionId":"{{{FieldC}}}","control":"text","label":"City","layout":{"columnSpan":2,"startOnNewRow":true,"columnStart":3}}
        ]}]}
        """;
        var layout = FormLayoutCodec.Parse(json);
        var reduced = GetRecordProjectionHandler.AuthorizedLayout(layout, new HashSet<Guid> { FieldB, FieldC }, new HashSet<Guid>());
        var serialized = FormLayoutCodec.Serialize(reduced);

        // The unauthorized field is gone with its identifier and label, and no remaining element carries a column position
        // from which the vacated cell (column 1) could be inferred: the viewer's layout reflows in source order.
        Assert.DoesNotContain(FieldA.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret salary", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("columnStart", serialized, StringComparison.Ordinal);
        Assert.Equal([FieldB, FieldC], reduced.Sections[0].Elements.Select(e => e.FieldDefinitionId!.Value));
        Assert.All(reduced.Sections[0].Elements, e => Assert.Null(e.Layout.ColumnStart));
        // Spans and row breaks (presentation of the fields the viewer may see) are kept.
        Assert.Equal(new LayoutPlacement(2, true), reduced.Sections[0].Elements[1].Layout);
    }

    [Fact]
    public void CompleteLayoutKeepsPositionsForAuthorizedReaders()
    {
        // The full (complete-projection) read returns the published layout unchanged; only partial projections are reduced.
        var layout = FormLayoutCodec.Parse(Layout("""{"columnSpan":1,"startOnNewRow":false,"columnStart":3}"""));
        Assert.Equal(3, layout.Sections[0].Elements[0].Layout.ColumnStart);
    }
}
