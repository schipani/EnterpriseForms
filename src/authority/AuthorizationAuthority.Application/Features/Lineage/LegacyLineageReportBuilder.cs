using AuthorizationAuthority.Domain.Features.Lineage;

namespace AuthorizationAuthority.Application.Features.Lineage;

/// <summary>
/// C10 M1-WP10: builds the legacy lineage classification report from classified entries.
/// Produces only opaque IDs, counts, enum outcomes and keyed pseudonyms. Pure and in-memory: it never writes, submits a
/// command or persists anything. Shares refused for incomplete historical evidence are carried as
/// <see cref="LegacyLineageClassificationReport.EvidenceRefusals"/> with no proposed outcome.
/// </summary>
public sealed class LegacyLineageReportBuilder
{
    private readonly List<LegacyShareClassificationEntry> _entries = [];
    private long _authorizationRevision;
    private DateTimeOffset _generatedOn;

    /// <summary>
    /// Sets the authorization revision at which the snapshot was taken.
    /// </summary>
    public LegacyLineageReportBuilder WithAuthorizationRevision(long revision)
    {
        _authorizationRevision = revision;
        return this;
    }

    /// <summary>
    /// Sets the generation timestamp.
    /// </summary>
    public LegacyLineageReportBuilder WithTimestamp(DateTimeOffset timestamp)
    {
        _generatedOn = timestamp;
        return this;
    }

    /// <summary>
    /// Adds a classified entry to the report.
    /// </summary>
    public LegacyLineageReportBuilder AddEntry(LegacyShareClassificationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
        return this;
    }

    /// <summary>
    /// Adds multiple classified entries to the report.
    /// </summary>
    public LegacyLineageReportBuilder AddEntries(IEnumerable<LegacyShareClassificationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries.AddRange(entries);
        return this;
    }

    /// <summary>
    /// Builds the final report with per-organization summaries.
    /// </summary>
    public LegacyLineageClassificationReport Build()
    {
        var refusals = new List<LineageEvidenceValidationError>();
        foreach (var entry in _entries)
        {
            var refused = entry.Classification == LegacyShareClassificationOutcome.InsufficientEvidence;
            if (refused != entry.EvidenceValidationError is not null)
            {
                throw new InvalidOperationException("an evidence refusal must accompany exactly the refused classifications");
            }

            if (refused && (entry.ProposedParentShareId is not null || entry.ProposedDelegationDepth is not null ||
                            entry.CandidateParentShareIds.Count != 0 ||
                            entry.IndependentCoverage != IndependentCoverageAnnotation.NotEvaluated ||
                            entry.EvidenceValidationError!.RecordShareId != entry.RecordShareId ||
                            entry.EvidenceValidationError.OrganizationId != entry.OrganizationId))
            {
                throw new InvalidOperationException("a refused classification cannot carry an assignment or invalidation claim");
            }

            if (refused)
            {
                refusals.Add(entry.EvidenceValidationError!);
            }
        }

        var summaries = _entries
            .GroupBy(e => e.OrganizationId)
            .Select(BuildSummary)
            .ToList();
        if (summaries.Count > 1)
        {
            throw new InvalidOperationException("a report revision applies to one organization");
        }

        return new LegacyLineageClassificationReport
        {
            AuthorizationRevisionRead = _authorizationRevision,
            ToolVersion = LegacyLineageClassifier.ToolVersion,
            GeneratedOn = _generatedOn == default ? DateTimeOffset.UtcNow : _generatedOn,
            Summaries = summaries,
            Entries = _entries.ToList(),
            EvidenceRefusals = refusals
        };
    }

    private static LegacyShareClassificationSummary BuildSummary(IGrouping<Guid, LegacyShareClassificationEntry> group)
    {
        var entries = group.ToList();
        return new LegacyShareClassificationSummary
        {
            OrganizationId = group.Key,
            TotalSharesEvaluated = entries.Count,
            OwnerGrantedCount = entries.Count(e => e.Classification == LegacyShareClassificationOutcome.OwnerGranted),
            UnambiguousParentCount = entries.Count(e => e.Classification == LegacyShareClassificationOutcome.UnambiguousParent),
            MultipleCandidateParentsCount = entries.Count(e => e.Classification == LegacyShareClassificationOutcome.MultipleCandidateParents),
            NoCoveringParentCount = entries.Count(e => e.Classification == LegacyShareClassificationOutcome.NoCoveringParent),
            ExceedsDepthCount = entries.Count(e => e.Classification == LegacyShareClassificationOutcome.ExceedsDepth),
            IndependentlyCoveredCount = entries.Count(e => e.IndependentCoverage == IndependentCoverageAnnotation.IndependentlyCovered),
            InsufficientEvidenceCount = entries.Count(e => e.Classification == LegacyShareClassificationOutcome.InsufficientEvidence)
        };
    }
}

/// <summary>
/// C10 M1-WP10: validates report output for security compliance.
/// </summary>
public static class LegacyLineageReportValidator
{
    /// <summary>
    /// Validates an entire report contains no PII and meets output constraints.
    /// </summary>
    public static ReportValidationResult Validate(LegacyLineageClassificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var issues = new List<string>();

        if (string.IsNullOrEmpty(report.ToolVersion))
        {
            issues.Add("Missing tool version");
        }

        if (report.AuthorizationRevisionRead <= 0)
        {
            issues.Add("Invalid authorization revision");
        }

        foreach (var entry in report.Entries)
        {
            if (!LineageReportPiiValidator.ValidateNoPii(entry))
            {
                issues.Add($"Entry {entry.RecordShareId} contains invalid or PII-like content");
            }

            // Validate counts are non-negative
            if (entry.FieldItemCount < 0)
            {
                issues.Add($"Entry {entry.RecordShareId} has negative field item count");
            }

            if (entry.DocumentItemCount < 0)
            {
                issues.Add($"Entry {entry.RecordShareId} has negative document item count");
            }

            // Validate proposed depth is in valid range when set
            if (entry.ProposedDelegationDepth.HasValue &&
                (entry.ProposedDelegationDepth < 1 || entry.ProposedDelegationDepth > LegacyLineageClassifier.MaxDelegationDepth))
            {
                issues.Add($"Entry {entry.RecordShareId} has invalid proposed depth {entry.ProposedDelegationDepth}");
            }

            // Validate consistency between classification and proposed fields
            if (entry.Classification == LegacyShareClassificationOutcome.OwnerGranted)
            {
                if (entry.ProposedDelegationDepth != 1)
                {
                    issues.Add($"Entry {entry.RecordShareId} is OwnerGranted but depth is not 1");
                }
                if (entry.ProposedParentShareId.HasValue)
                {
                    issues.Add($"Entry {entry.RecordShareId} is OwnerGranted but has a parent");
                }
            }

            if (entry.Classification == LegacyShareClassificationOutcome.UnambiguousParent)
            {
                if (!entry.ProposedParentShareId.HasValue)
                {
                    issues.Add($"Entry {entry.RecordShareId} is UnambiguousParent but has no parent");
                }
                if (!entry.ProposedDelegationDepth.HasValue)
                {
                    issues.Add($"Entry {entry.RecordShareId} is UnambiguousParent but has no depth");
                }
            }

            if ((entry.Classification is LegacyShareClassificationOutcome.MultipleCandidateParents
                    or LegacyShareClassificationOutcome.NoCoveringParent or LegacyShareClassificationOutcome.ExceedsDepth) &&
                (entry.ProposedParentShareId.HasValue || entry.ProposedDelegationDepth.HasValue))
            {
                issues.Add($"Entry {entry.RecordShareId} is {entry.Classification} but proposes an assignment");
            }

            if (entry.Classification == LegacyShareClassificationOutcome.InsufficientEvidence)
            {
                var error = entry.EvidenceValidationError;
                if (error is null || error.RecordShareId != entry.RecordShareId || error.OrganizationId != entry.OrganizationId)
                {
                    issues.Add($"Entry {entry.RecordShareId} is InsufficientEvidence without its own refusal");
                }

                if (entry.ProposedParentShareId.HasValue || entry.ProposedDelegationDepth.HasValue ||
                    entry.CandidateParentShareIds.Count != 0 || entry.IndependentCoverage != IndependentCoverageAnnotation.NotEvaluated)
                {
                    issues.Add($"Entry {entry.RecordShareId} is InsufficientEvidence but carries an assignment or invalidation claim");
                }

                if (entry.GrantorPseudonymousReference is null && error?.Reason != InsufficientEvidenceReason.UnknownGrantor)
                {
                    issues.Add($"Entry {entry.RecordShareId} omits a known grantor reference");
                }

                if (error is not null && !report.EvidenceRefusals.Contains(error))
                {
                    issues.Add($"Entry {entry.RecordShareId} refusal is not propagated to the report");
                }
            }
            else
            {
                if (entry.EvidenceValidationError is not null)
                {
                    issues.Add($"Entry {entry.RecordShareId} carries a refusal but was classified {entry.Classification}");
                }

                if (entry.GrantorPseudonymousReference is null)
                {
                    issues.Add($"Entry {entry.RecordShareId} was classified without a grantor reference");
                }
            }
        }

        if (report.EvidenceRefusals.Count !=
            report.Entries.Count(e => e.Classification == LegacyShareClassificationOutcome.InsufficientEvidence))
        {
            issues.Add("Evidence refusals do not match the refused entries");
        }

        // Validate summary counts match entries
        foreach (var summary in report.Summaries)
        {
            var orgEntries = report.Entries.Where(e => e.OrganizationId == summary.OrganizationId).ToList();

            if (summary.TotalSharesEvaluated != orgEntries.Count)
            {
                issues.Add($"Summary for {summary.OrganizationId} has incorrect total count");
            }

            int Count(LegacyShareClassificationOutcome outcome) => orgEntries.Count(e => e.Classification == outcome);
            if (summary.OwnerGrantedCount != Count(LegacyShareClassificationOutcome.OwnerGranted) ||
                summary.UnambiguousParentCount != Count(LegacyShareClassificationOutcome.UnambiguousParent) ||
                summary.MultipleCandidateParentsCount != Count(LegacyShareClassificationOutcome.MultipleCandidateParents) ||
                summary.NoCoveringParentCount != Count(LegacyShareClassificationOutcome.NoCoveringParent) ||
                summary.ExceedsDepthCount != Count(LegacyShareClassificationOutcome.ExceedsDepth) ||
                summary.InsufficientEvidenceCount != Count(LegacyShareClassificationOutcome.InsufficientEvidence) ||
                summary.IndependentlyCoveredCount !=
                orgEntries.Count(e => e.IndependentCoverage == IndependentCoverageAnnotation.IndependentlyCovered))
            {
                issues.Add($"Summary for {summary.OrganizationId} has incorrect classification counts");
            }
        }

        return new ReportValidationResult(issues.Count == 0, issues);
    }
}

/// <summary>
/// Result of report validation.
/// </summary>
public sealed record ReportValidationResult(bool IsValid, IReadOnlyList<string> Issues);
