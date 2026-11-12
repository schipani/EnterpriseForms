using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseForms.Experiments.ManagedHsmProbe;

internal enum Verdict
{
    /// <summary>Observed bytes or behaviour equal what the locally computed reference requires.</summary>
    Pass,

    /// <summary>The service refused, or answered with something other than what the reference requires.</summary>
    Fail,

    /// <summary>Nothing can be concluded: infrastructure failure, timeout, throttling, or an unknown (HSM-generated) key.</summary>
    Inconclusive,
}

internal sealed record ProbeCheck(string CheckId, string Experiment, Verdict Verdict, string Detail, IReadOnlyDictionary<string, string>? Evidence = null);

internal sealed class ProbeReport
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Schema { get; init; } = "EnterpriseForms/E6a/ManagedHsmHmacProbe/report/v1";

    public string Command { get; init; } = string.Empty;

    public DateTimeOffset GeneratedOnUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Classification { get; init; } = "Development/Testing experiment. Not EnterpriseForms provider evidence; owner-led review.";

    public string? HsmHost { get; init; }

    public string? KeyName { get; init; }

    public string? ApiVersion { get; init; }

    public string? KeyProvenance { get; init; }

    public IReadOnlyDictionary<string, string>? RequestShapes { get; init; }

    public List<ProbeCheck> Checks { get; } = [];

    public Dictionary<string, Verdict> ExperimentVerdicts => Checks.GroupBy(c => c.Experiment, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => Aggregate(g.Select(c => c.Verdict)), StringComparer.Ordinal);

    public Verdict OverallVerdict => Aggregate(Checks.Select(c => c.Verdict));

    public List<string> Notes { get; } = [];

    /// <summary>Any Fail is a Fail; otherwise any Inconclusive is Inconclusive; a Pass needs every check to pass (and at least one check).</summary>
    public static Verdict Aggregate(IEnumerable<Verdict> verdicts)
    {
        var list = verdicts.ToList();
        return list.Count == 0 ? Verdict.Inconclusive : list.Contains(Verdict.Fail) ? Verdict.Fail : list.Contains(Verdict.Inconclusive) ? Verdict.Inconclusive : Verdict.Pass;
    }

    public void Add(string checkId, string experiment, Verdict verdict, string detail, IReadOnlyDictionary<string, string>? evidence = null) =>
        Checks.Add(new ProbeCheck(checkId, experiment, verdict, detail, evidence));

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions).ReplaceLineEndings("\n") + "\n";

    public void WriteSummary(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var check in Checks)
        {
            output.WriteLine($"  [{check.Verdict,-12}] {check.Experiment}/{check.CheckId}: {check.Detail}");
        }

        foreach (var (experiment, verdict) in ExperimentVerdicts)
        {
            output.WriteLine($"Experiment {experiment}: {verdict}");
        }

        foreach (var note in Notes)
        {
            output.WriteLine("Note: " + note);
        }

        output.WriteLine($"OVERALL: {OverallVerdict}");
    }

    public int ExitCode => OverallVerdict switch
    {
        Verdict.Pass => 0,
        Verdict.Fail => 1,
        _ => 3,
    };
}
