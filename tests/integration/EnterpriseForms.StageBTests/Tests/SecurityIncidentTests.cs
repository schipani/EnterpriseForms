using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Security monitoring (C-90) on the real stack: a hostile business-database write is refused (unchanged fail-closed
/// behaviour) and becomes one durable, deduplicated incident that only Security Administrators can see; investigation is
/// audited; closing the incident never restores access; another organization sees nothing.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class SecurityIncidentTests(StageBFixture fixture)
{
    [Fact]
    public async Task ATamperedRecordBecomesOneAuditedIncidentThatOnlySecurityAdministratorsSeeAndClosingItRestoresNothing()
    {
        var s = new StageBScenario(fixture, "incidents");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var accused = await s.CreateAccusedAsync(s.Clerk);
        var admin = s.Token(s.Administrator);

        var before = await fixture.Api.GetAsync("/api/v1/security/incidents", admin);
        Assert.True(before.IsSuccess, before.Body);
        Assert.Equal(0, before.Root.GetProperty("totalCount").GetInt32());

        // Hostile write (signature untouched), exactly as the existing tamper regression.
        fixture.Stack.AttackerSql($"UPDATE field_date_values SET date_value = DATE '2001-01-01' WHERE form_record_id = '{accused:D}';");
        var first = await s.ProjectionAsync(s.Clerk, accused);
        Assert.Equal(HttpStatusCode.Locked, first.Status);
        Assert.Equal("INTEGRITY_VERIFICATION_FAILED", first.ErrorCode);
        Assert.DoesNotContain("2001-01-01", first.Body, StringComparison.Ordinal);
        // Repeated reads stay refused (now by the Authority's quarantine) and must not create more incidents.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Locked, (await s.ProjectionAsync(s.Clerk, accused)).Status);
        }

        var incidents = fixture.Stack.AttackerSql($"SELECT count(*) || '|' || max(occurrence_count) || '|' || max(incident_type_name) || '|' || max(incident_severity_name) FROM security_incidents WHERE tenant_id = '{s.OrganizationId:D}';").Trim().Split('|');
        Assert.Equal("1", incidents[0]);
        Assert.True(int.Parse(incidents[1], CultureInfo.InvariantCulture) >= 4, "every detection is counted: " + string.Join('|', incidents));
        Assert.Equal("ConfirmedIntegrityMismatch", incidents[2]);
        Assert.Equal("Critical", incidents[3]);
        // No record content, values or keys in the triage store.
        var stored = fixture.Stack.AttackerSql($"SELECT string_agg(row_to_json(i)::text, ' ') FROM security_incidents i WHERE tenant_id = '{s.OrganizationId:D}';");
        Assert.DoesNotContain("2001-01-01", stored, StringComparison.Ordinal);

        // Security Administrator: listed, record withheld (the administrator cannot discover the clerk's record).
        var list = await fixture.Api.GetAsync("/api/v1/security/incidents", admin);
        Assert.True(list.IsSuccess, list.Body);
        Assert.Equal(1, list.Root.GetProperty("totalCount").GetInt32());
        Assert.Equal(15, list.Root.GetProperty("pageSize").GetInt32());
        var item = list.Root.GetProperty("items")[0];
        Assert.True(item.GetProperty("recordWithheld").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("formRecordId").ValueKind);
        Assert.DoesNotContain(accused.ToString("D"), list.Body, StringComparison.OrdinalIgnoreCase);
        var incidentId = item.GetProperty("securityIncidentId").GetGuid();
        // Searching for the record id the administrator cannot discover finds nothing.
        var probe = await fixture.Api.GetAsync($"/api/v1/security/incidents?q={accused:D}", admin);
        Assert.Equal(0, probe.Root.GetProperty("totalCount").GetInt32());

        var summary = await fixture.Api.GetAsync("/api/v1/security/incidents/summary?periodDays=7&timeZone=America/Toronto", admin);
        Assert.True(summary.IsSuccess, summary.Body);
        Assert.Equal(1, summary.Root.GetProperty("openIncidentCount").GetInt32());
        Assert.Equal(1, summary.Root.GetProperty("confirmedIntegrityFailureCount").GetInt32());
        Assert.Equal(1, summary.Root.GetProperty("detections").EnumerateArray().Sum(b => b.GetProperty("integrityIncidentCount").GetInt32()));

        // Organization owner, designer, clerk and reader: 403 everywhere, no counts.
        foreach (var user in new[] { s.Designer, s.DesignerOnly, s.Clerk, s.Reader })
        {
            foreach (var path in new[] { "/api/v1/security/incidents", "/api/v1/security/incidents/summary", $"/api/v1/security/incidents/{incidentId:D}" })
            {
                var denied = await fixture.Api.GetAsync(path, s.Token(user));
                Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
                Assert.DoesNotContain("totalCount", denied.Body, StringComparison.Ordinal);
            }
        }

        // Another organization's Security Administrator sees nothing of this organization.
        var other = new StageBScenario(fixture, "incidents-other");
        await other.BootstrapAsync();
        var foreign = other.Token(other.Administrator);
        Assert.Equal(0, (await fixture.Api.GetAsync("/api/v1/security/incidents", foreign)).Root.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.GetAsync($"/api/v1/security/incidents/{incidentId:D}", foreign)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Api.PatchAsync($"/api/v1/security/incidents/{incidentId:D}", foreign, new { expectedIncidentRevision = 1, incidentStatusName = "Resolved", resolutionExplanation = "not mine" })).Status);

        // Investigation: note, stale revision refused, assignment and resolution audited.
        var detail = await fixture.Api.GetAsync($"/api/v1/security/incidents/{incidentId:D}", admin);
        Assert.True(detail.IsSuccess, detail.Body);
        Assert.DoesNotContain("2001-01-01", detail.Body, StringComparison.Ordinal);
        var revision = detail.Root.GetProperty("incidentRevision").GetInt64();
        var note = await fixture.Api.PostAsync($"/api/v1/security/incidents/{incidentId:D}/notes", admin, new { investigationNote = "Compared with the Authority journal entry; DBA change window under review." });
        Assert.True(note.IsSuccess, note.Body);
        var stale = await fixture.Api.PatchAsync($"/api/v1/security/incidents/{incidentId:D}", admin, new { expectedIncidentRevision = revision + 5, incidentStatusName = "Investigating" });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("INCIDENT_MODIFIED_CONCURRENTLY", stale.ErrorCode);
        var noExplanation = await fixture.Api.PatchAsync($"/api/v1/security/incidents/{incidentId:D}", admin, new { expectedIncidentRevision = revision, incidentStatusName = "Resolved" });
        Assert.Equal((HttpStatusCode)422, noExplanation.Status);
        var assigned = await fixture.Api.PatchAsync($"/api/v1/security/incidents/{incidentId:D}", admin, new { expectedIncidentRevision = revision, incidentStatusName = "Investigating", assignedToUserId = s.Administrator.AuthorityUserId });
        Assert.True(assigned.IsSuccess, assigned.Body);
        var notAdministrator = await fixture.Api.PatchAsync($"/api/v1/security/incidents/{incidentId:D}", admin, new { expectedIncidentRevision = assigned.Root.GetProperty("incidentRevision").GetInt64(), assignedToUserId = s.Clerk.AuthorityUserId });
        Assert.Equal((HttpStatusCode)422, notAdministrator.Status);
        var resolved = await fixture.Api.PatchAsync($"/api/v1/security/incidents/{incidentId:D}", admin, new { expectedIncidentRevision = assigned.Root.GetProperty("incidentRevision").GetInt64(), incidentStatusName = "Resolved", resolutionExplanation = "Hostile test write; record stays quarantined pending recovery." });
        Assert.True(resolved.IsSuccess, resolved.Body);
        Assert.Equal("Resolved", resolved.Root.GetProperty("incident").GetProperty("incidentStatusName").GetString());
        var actions = resolved.Root.GetProperty("auditTrail").EnumerateArray().Select(e => e.GetProperty("eventAction").GetString()).ToList();
        foreach (var expected in new[] { "SECURITY_INCIDENT_DETECTED", "SECURITY_INCIDENT_VIEWED", "SECURITY_INCIDENT_NOTE_ADDED", "SECURITY_INCIDENT_ASSIGNED", "SECURITY_INCIDENT_STATUS_CHANGED" })
        {
            Assert.Contains(expected, actions);
        }

        var noteAudit = fixture.Stack.AttackerSql($"SELECT event_metadata_json::text FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action = 'SECURITY_INCIDENT_NOTE_ADDED';");
        Assert.DoesNotContain("DBA change window", noteAudit, StringComparison.Ordinal);

        // Closing the incident restores nothing: the record stays refused, and the refusal is counted on the closed incident.
        var after = await s.ProjectionAsync(s.Clerk, accused);
        Assert.Equal(HttpStatusCode.Locked, after.Status);
        var final = await fixture.Api.GetAsync("/api/v1/security/incidents", admin);
        Assert.Equal(1, final.Root.GetProperty("totalCount").GetInt32());
        Assert.Equal("Resolved", final.Root.GetProperty("items")[0].GetProperty("incidentStatusName").GetString());
        Assert.True(final.Root.GetProperty("items")[0].GetProperty("occurrenceCount").GetInt32() > item.GetProperty("occurrenceCount").GetInt32());

        // Durable: the guard trigger refuses rewinding history even to the runtime role's own UPDATE grant.
        var rewind = fixture.Stack.AttackerSql($"DO $$ BEGIN BEGIN UPDATE security_incidents SET occurrence_count = 1 WHERE security_incident_id = '{incidentId:D}'; RAISE NOTICE 'updated'; EXCEPTION WHEN check_violation THEN RAISE NOTICE 'refused'; END; END $$; SELECT occurrence_count FROM security_incidents WHERE security_incident_id = '{incidentId:D}';").Trim();
        Assert.NotEqual("1", rewind.Split('\n').Last().Trim());
    }
}
