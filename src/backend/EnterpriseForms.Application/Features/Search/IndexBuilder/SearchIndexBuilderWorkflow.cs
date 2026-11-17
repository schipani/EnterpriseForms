using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.FormRecords.Ports;

namespace EnterpriseForms.Application.Features.Search.IndexBuilder;

/// <summary>
/// IX-2: the <c>search-index-builder</c> workload credential and pacing (section <c>SearchIndexBuilder</c>, Worker only). All three
/// credential values or none. The rate is a bound on records submitted per second (MAC verifications and one seal signature per
/// record on the Authority side); it is to be sized from the PERF-01 Managed HSM measurements (R1 §C.3 item 5).
/// </summary>
public sealed class SearchIndexBuilderOptions
{
    public const string SectionName = "SearchIndexBuilder";

    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public string? SharedKeyBase64 { get; set; }

    public string WorkloadName { get; set; } = "search-index-builder";

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>A backfill round (every Building coverage of every organization) starts at least this often.</summary>
    public int BackfillIntervalSeconds { get; set; } = 60;

    /// <summary>S-C: a deep-sweep pass over every indexed organization starts at least this often (design: daily).</summary>
    public int DeepSweepIntervalSeconds { get; set; } = 86_400;

    /// <summary>S-C per organization and pass: entries with the oldest deep verification (scheduling target: every record within 20 days).</summary>
    public int DeepSweepOldestPerPass { get; set; } = 5_000;

    /// <summary>S-C per organization and pass: the Authority's random sample (§17.5: at least 1% or 1,000 records).</summary>
    public int DeepSweepSamplePerPass { get; set; } = 1_000;

    /// <summary>Upper bound on records submitted per second (backfill and deep sweep together).</summary>
    public int MaximumRecordsPerSecond { get; set; } = 20;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(Audience) && SharedKeyLength >= 32;

    public bool IsPartlyConfigured => !IsConfigured && (!string.IsNullOrWhiteSpace(Issuer) || !string.IsNullOrWhiteSpace(Audience) || !string.IsNullOrWhiteSpace(SharedKeyBase64));

    public void Validate()
    {
        if (TimeoutSeconds < 1 || BackfillIntervalSeconds < 1 || DeepSweepIntervalSeconds < 1 || DeepSweepOldestPerPass < 1 || DeepSweepSamplePerPass < 0 || MaximumRecordsPerSecond < 1)
        {
            throw new InvalidOperationException("SearchIndexBuilder intervals, pass sizes and the record rate must be positive.");
        }
    }

    private int SharedKeyLength
    {
        get
        {
            try
            {
                return string.IsNullOrWhiteSpace(SharedKeyBase64) ? 0 : Convert.FromBase64String(SharedKeyBase64).Length;
            }
            catch (FormatException)
            {
                return 0;
            }
        }
    }
}

/// <summary>Counts of one builder pass (identifiers and outcome codes only; never values).</summary>
public sealed record SearchIndexBuilderPassResult(int FormsVisited, int RecordsSubmitted, int RecordsIndexed, int RecordsAlreadyIndexed, int HeadChanged, int Refused, int RecordsQuarantined, int CoveragesCompleted,
    IReadOnlyDictionary<string, int> OutcomeCounts, TimeSpan Elapsed);

public sealed record SearchIndexDeepSweepPassResult(int OrganizationsVisited, int EntriesSubmitted, int Passed, int Failed, int Inconclusive, int RecordsQuarantined, TimeSpan Elapsed);

/// <summary>
/// IX-2 (R1 §C.3-C.4, §C.6; design §6.5, §7, §17.5): the builder side. It only moves bytes: it asks the Authority what to build,
/// reads the accepted rows from the business database set-based (O-2), submits them in batches of at most 50 and asks the
/// Authority to attest. It never decides that anything is indexed or complete, and holds no state of its own: every round
/// starts from the Authority's population, so a crashed, restarted or duplicated builder is safe (idempotent per record, head
/// and coverage set). The deep sweep compares the entries the Authority chooses with freshly read business bytes.
/// </summary>
public sealed class SearchIndexBuilderWorkflow(ISearchIndexBuilderAuthority authority, IAcceptedRecordItemsReader records, SearchIndexBuilderOptions options, TimeProvider clock) : ISearchIndexBuilderPasses
{
    public const int BatchSize = 50;
    public const int PopulationPageSize = 500;
    public const int SelectionCallLimit = 200;

    public bool IsConfigured => authority.IsConfigured;

    /// <summary>One backfill round: every form with a Building coverage, population pages, verified backfill, then attestation.</summary>
    public async Task<SearchIndexBuilderPassResult> RunBackfillRoundAsync(CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        int forms = 0, submitted = 0, completed = 0;
        var work = await authority.GetWorkAsync(cancellationToken).ConfigureAwait(false);
        foreach (var form in work.BuildingForms)
        {
            forms++;
            try
            {
                submitted += await BuildFormAsync(form.OrganizationId, form.FormDefinitionId, tally, cancellationToken).ConfigureAwait(false);
                var attestation = await authority.AttestAsync(form.OrganizationId, form.FormDefinitionId, cancellationToken).ConfigureAwait(false);
                completed += attestation.CoveragesCompleted;
                Count(tally, attestation.CoverageComplete ? "ATTESTED_COMPLETE" : "ATTESTED_INCOMPLETE:" + attestation.IncompleteReasonCode);
            }
            catch (AuthorityClientException ex) when (ex.HttpStatusCode is 404 or 409 or 423)
            {
                // Quarantined or removed meanwhile, or the organization gate closed: nothing is built; the next round re-reads.
                Count(tally, "FORM_SKIPPED:" + ex.ErrorCode);
            }
        }

        return new SearchIndexBuilderPassResult(forms, submitted, tally.GetValueOrDefault("Indexed"), tally.GetValueOrDefault("AlreadyIndexed"), tally.GetValueOrDefault("HEAD_CHANGED"),
            tally.Where(t => t.Key is not ("Indexed" or "AlreadyIndexed" or "HEAD_CHANGED") && !t.Key.StartsWith("ATTESTED", StringComparison.Ordinal) && !t.Key.StartsWith("FORM_SKIPPED", StringComparison.Ordinal)).Sum(t => t.Value),
            tally.GetValueOrDefault("RECORD_QUARANTINED"), completed, tally, clock.GetElapsedTime(started));
    }

    /// <summary>Submits every record of the form that the Authority reports as still needing an entry; returns the records submitted.</summary>
    public async Task<int> BuildFormAsync(Guid organizationId, Guid formDefinitionId, Dictionary<string, int> tally, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tally);
        var submitted = 0;
        Guid? after = null;
        do
        {
            var page = await authority.ReadPopulationAsync(organizationId, formDefinitionId, after, PopulationPageSize, cancellationToken).ConfigureAwait(false);
            foreach (var chunk in page.Entries.Where(e => e.NeedsIndexing && e.CommittedOperationId is not null).Chunk(BatchSize))
            {
                var batchStarted = clock.GetTimestamp();
                var contents = await records.ReadAcceptedContentBatchAsync(chunk.Select(e => (e.FormRecordId, e.CommittedOperationId!.Value)).ToList(), cancellationToken).ConfigureAwait(false);
                var items = chunk.Select(e => new SearchIndexBackfillItem(e.FormRecordId, e.CommittedOperationId!.Value, contents[e.FormRecordId].Items)).ToList();
                var response = await authority.BackfillAsync(organizationId, formDefinitionId, items, cancellationToken).ConfigureAwait(false);
                foreach (var result in response.Results)
                {
                    Count(tally, result.Outcome);
                    if (result.RecordQuarantined)
                    {
                        Count(tally, "RECORD_QUARANTINED");
                    }
                }

                submitted += items.Count;
                await PaceAsync(items.Count, batchStarted, cancellationToken).ConfigureAwait(false);
            }

            after = page.NextAfterFormRecordId;
        }
        while (after is not null);
        return submitted;
    }

    /// <summary>S-C pass: for every indexed organization, the entries the Authority selects (oldest first, plus its sample).</summary>
    public async Task<SearchIndexDeepSweepPassResult> RunDeepSweepPassAsync(CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        int organizations = 0, submitted = 0, passed = 0, failed = 0, inconclusive = 0, quarantined = 0;
        var work = await authority.GetWorkAsync(cancellationToken).ConfigureAwait(false);
        foreach (var organization in work.IndexedOrganizationIds)
        {
            organizations++;
            int oldestLeft = options.DeepSweepOldestPerPass, sampleLeft = options.DeepSweepSamplePerPass;
            var seen = new HashSet<Guid>();
            try
            {
                while (oldestLeft > 0 || sampleLeft > 0)
                {
                    var oldest = Math.Clamp(oldestLeft, 1, SelectionCallLimit);
                    var sample = Math.Clamp(sampleLeft, 0, SelectionCallLimit);
                    var selection = await authority.GetDeepSweepWorkAsync(organization, oldest, sample, cancellationToken).ConfigureAwait(false);
                    var fresh = selection.Entries.Where(e => seen.Add(e.FormRecordId)).ToList();
                    oldestLeft -= oldest;
                    sampleLeft -= sample;
                    if (fresh.Count == 0)
                    {
                        break; // every entry of the organization has been visited in this pass
                    }

                    foreach (var chunk in fresh.Chunk(BatchSize))
                    {
                        var batchStarted = clock.GetTimestamp();
                        var contents = await records.ReadAcceptedContentBatchAsync(chunk.Select(e => (e.FormRecordId, e.IndexedOperationId)).ToList(), cancellationToken).ConfigureAwait(false);
                        // Submitted even when the business database holds no row for the operation: the Authority's verification
                        // then finds MISSING_ITEM and quarantines the record (deleted business bytes are tampering, design §6.5).
                        var items = chunk.Select(e => new SearchIndexBackfillItem(e.FormRecordId, e.IndexedOperationId, contents[e.FormRecordId].Items)).ToList();
                        var response = await authority.DeepVerifyAsync(organization, items, cancellationToken).ConfigureAwait(false);
                        submitted += items.Count;
                        passed += response.Results.Count(r => r.UnitOutcome == "Pass");
                        failed += response.Results.Count(r => r.UnitOutcome == "Fail");
                        inconclusive += response.Results.Count(r => r.UnitOutcome == "Inconclusive");
                        quarantined += response.Results.Count(r => r.RecordQuarantined);
                        await PaceAsync(items.Count, batchStarted, cancellationToken).ConfigureAwait(false);
                        if (response.ProjectionStatusName == "Quarantined")
                        {
                            break;
                        }
                    }
                }
            }
            catch (AuthorityClientException ex) when (ex.HttpStatusCode is 404 or 409 or 423)
            {
                inconclusive++; // quarantined, removed or gate closed: the deadline keeps running (no timestamp is written)
            }
        }

        return new SearchIndexDeepSweepPassResult(organizations, submitted, passed, failed, inconclusive, quarantined, clock.GetElapsedTime(started));
    }

    private async Task PaceAsync(int count, long batchStarted, CancellationToken cancellationToken)
    {
        var minimum = TimeSpan.FromSeconds((double)count / options.MaximumRecordsPerSecond);
        var spent = clock.GetElapsedTime(batchStarted);
        if (spent < minimum)
        {
            await Task.Delay(minimum - spent, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Count(Dictionary<string, int> tally, string key) => tally[key] = tally.GetValueOrDefault(key) + 1;
}
