namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>
/// C-150 (R-02 read-stage investigation), Testing environment only: splits the indexed commit's derivation <i>reads</i> stage. Active only
/// while <see cref="SearchIndexWriter"/> has a stage-timing sink (Testing) and only between the start and the end of that stage, in the
/// committing flow. Durations and counts only: never a value, identifier or statement text.
/// <list type="bullet">
/// <item><see cref="RegistryMilliseconds"/>: the registry lookup when the commit did not pass its registered form (normally 0);</item>
/// <item><see cref="FirstScanMilliseconds"/> and <see cref="SecondScanMilliseconds"/>: the change-tracker work before the SQL. Before the
/// C-150 correction these were the two inspections, each running EF Core's automatic change detection over every tracked entity; after
/// it, the first is the single explicit detection and the second the two inspections, which no longer detect again;</item>
/// <item><see cref="QueryMilliseconds"/>: the approvals statement from the call to the last materialized row (round trip, server execution,
/// reading and tracking the rows); <see cref="CommandExecuteMilliseconds"/>: the part EF Core reports as command execution (sent until
/// the first result is available: server execution plus one network round trip);</item>
/// <item><see cref="Commands"/>: database commands issued in the stage; <see cref="TrackedEntities"/> and <see cref="ApprovalRows"/>: sizes.</item>
/// </list>
/// The difference between the query time and the command time is client-side reading, materialization and identity tracking.
/// </summary>
public sealed class SearchDerivationReadProbe
{
    private static readonly AsyncLocal<SearchDerivationReadProbe?> Active = new();

    public static SearchDerivationReadProbe? Current => Active.Value;

    public double RegistryMilliseconds { get; set; }

    public double FirstScanMilliseconds { get; set; }

    public double SecondScanMilliseconds { get; set; }

    public double QueryMilliseconds { get; set; }

    public double CommandExecuteMilliseconds { get; private set; }

    public int Commands { get; private set; }

    public int TrackedEntities { get; set; }

    public int ApprovalRows { get; set; }

    public bool TrackedMergePath { get; set; }

    /// <summary>Starts the probe for the current flow (the commit's derivation).</summary>
    public static SearchDerivationReadProbe Start()
    {
        var probe = new SearchDerivationReadProbe();
        Active.Value = probe;
        return probe;
    }

    /// <summary>Ends attribution: later commands (the writes) are not counted.</summary>
    public static void Stop() => Active.Value = null;

    /// <summary>Called by the Testing command interceptor for each command executed while the probe is active.</summary>
    public void CommandExecuted(TimeSpan duration)
    {
        Commands++;
        CommandExecuteMilliseconds += duration.TotalMilliseconds;
    }
}
