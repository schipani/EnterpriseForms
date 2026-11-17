using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.SecurityTests.Harness;
using Xunit;

namespace EnterpriseForms.SecurityTests.Tests;

/// <summary>SEC-P11: business identities cannot reach the signer or key store; the authority cannot be coerced into signing a forged transition.</summary>
[Collection(StageACollection.Name)]
public sealed class SigningBoundaryTests(StageAFixture fixture)
{
    [Fact]
    [Trait("SEC-P", "P11")]
    public async Task SecP11_BusinessIdentitiesCannotSignOrReachKeyMaterial()
    {
        var s = new Scenario(fixture, "SEC-P11", "Unauthorized HSM signing attempt: business API/DB identities cannot call signing operations or obtain raw keys; the authority refuses to sign a forged transition");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();

            // 1. Network/credential plane: the hostile DBA container (business-net) cannot reach Vault, SpiceDB, AuthorityDb or the archive.
            foreach (var target in new[] { "vault:8200", "spicedb:50051", "authority-db:5432", "archive-store:10000" })
            {
                var host = target.Split(':')[0];
                var port = target.Split(':')[1];
                var probe = fixture.Stack.AttackerShell($"getent hosts {host} && echo RESOLVED || echo UNRESOLVED; (timeout 4 pg_isready -h {host} -p {port} >/dev/null 2>&1 && echo REACHABLE) || echo UNREACHABLE", throwOnError: false);
                s.Evidence.Observe("attacker-sql reachability " + target, new { result = Evidence.Sanitize(probe.Trim()) });
                Assert.Contains("UNRESOLVED", probe, StringComparison.Ordinal);
                Assert.Contains("UNREACHABLE", probe, StringComparison.Ordinal);
            }

            var ipProbe = fixture.Stack.AttackerShell("(timeout 4 pg_isready -h 172.30.1.2 -p 5432 >/dev/null 2>&1 && echo REACHABLE) || echo UNREACHABLE; (timeout 4 pg_isready -h 172.30.1.3 -p 8200 >/dev/null 2>&1 && echo REACHABLE) || echo UNREACHABLE", throwOnError: false);
            s.Evidence.Observe("attacker-sql reachability of authority-net addresses by IP", new { result = Evidence.Sanitize(ipProbe.Trim()) });
            Assert.DoesNotContain("REACHABLE\n", ipProbe.Replace("UNREACHABLE", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

            // 2. The business workload credential cannot use the operator plane (key provisioning, fault injection, gate).
            var workload = TestIdentity.WorkloadToken();
            var operatorAttempt = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/operator/organizations", workload, new { organization_id = Guid.CreateVersion7(), organization_display_name = "forged", identities = Array.Empty<object>() });
            s.Evidence.ObserveHttp("business workload token → operator bootstrap", operatorAttempt);
            Assert.True(operatorAttempt.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, operatorAttempt.Body);
            var faultAttempt = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", workload, new { fault_name = "CrashAfterJournalCommit" });
            Assert.True(faultAttempt.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, faultAttempt.Body);

            // 3. No generic signing endpoint exists on the authority contract.
            foreach (var path in new[] { "/internal/v1/integrity/sign", "/internal/v1/signing/hmac", "/internal/v1/keys" })
            {
                var sign = await fixture.Authority.SendAsync(HttpMethod.Post, path, workload, new { payload = "AAAA" }, s.Token(s.Owner));
                s.Evidence.ObserveHttp("sign-any-bytes probe " + path, sign);
                Assert.Equal(HttpStatusCode.NotFound, sign.Status);
            }

            // 4. Forged transition: the attacker (a bound principal with no rights) asks the authority to sign a candidate for the owner's record.
            //    Since R-53 (C-148) every write to a record with accepted content must carry the complete current snapshot, proven equal
            //    to the accepted head, before any authorization decision. The request is therefore made structurally valid exactly as
            //    the hostile BusinessDb administrator could make it: the accepted rows are read from BusinessDb, the complete snapshot is
            //    re-encoded under a fresh operation, and only the header is forged. Every structural rule passes, so the refusal asserted
            //    below is the authorization decision itself (403 FORBIDDEN "actor may not change this record"), not input validation.
            var head = await fixture.Authority.SendAsync(HttpMethod.Get, $"/internal/v1/content-revisions/{s.OrganizationId:D}/{s.FormRecordId:D}", workload, null, s.Token(s.Attacker));
            s.Evidence.ObserveHttp("attacker content-revision lookup", head, "error_code");
            Assert.Equal(HttpStatusCode.NotFound, head.Status);

            var ownerHead = await fixture.Authority.SendAsync(HttpMethod.Get, $"/internal/v1/content-revisions/{s.OrganizationId:D}/{s.FormRecordId:D}", workload, null, s.Token(s.Owner));
            Assert.True(ownerHead.IsSuccess, ownerHead.Body);
            var keyVersion = (uint)ownerHead.Root.GetProperty("current_signing_key_version").GetInt32();
            var schema = new RowSchemaDefinition("form_records", 2,
            [
                new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, false),
                new RowSignedColumn("created_by_user_id", RowColumnType.Uuid, false),
                new RowSignedColumn("created_form_version_id", RowColumnType.Uuid, false),
                new RowSignedColumn("form_definition_id", RowColumnType.Uuid, false),
                new RowSignedColumn("record_status", RowColumnType.Text, false),
                new RowSignedColumn("submitted_on", RowColumnType.TimestampUtc, true),
                new RowSignedColumn("tenant_id", RowColumnType.Uuid, false),
            ]);
            var acceptedRevision = ownerHead.Root.GetProperty("committed_content_revision").GetInt64();
            var acceptedOperation = ownerHead.Root.GetProperty("committed_operation_id").GetGuid();
            var acceptedRoot = ownerHead.Root.GetProperty("committed_manifest_root_base64").GetString();
            var authorizationRevision = ownerHead.Root.GetProperty("authorization_revision").GetInt64();

            // The complete accepted snapshot, read by the hostile BusinessDb administrator (attacker-sql), never from the host.
            var snapshot = ReadAcceptedSnapshotAsBusinessDba(fixture, s.FormRecordId, acceptedOperation);
            var currentItems = snapshot.Select(r => new
            {
                item_kind = r.ItemKind,
                item_id = r.ItemId,
                canonical_bytes_base64 = Convert.ToBase64String(r.Encode(r.Columns, r.KeyVersion, r.RowRevision)),
                row_signature_base64 = Convert.ToBase64String(r.RowSignature),
            }).ToList();
            // Test oracle (read-only AuthorityDb): the snapshot equals the accepted head item for item, so the request cannot be refused
            // as an incomplete or mismatched snapshot (and cannot raise a snapshot integrity incident).
            var reencoded = snapshot.Select(r => $"{r.KindCode}|{r.ItemId:D}|{Convert.ToHexStringLower(SHA256.HashData(r.Encode(r.Columns, r.KeyVersion, r.RowRevision)))}").Order(StringComparer.Ordinal).ToList();
            Assert.Equal(AcceptedLeaves(fixture, s.OrganizationId, s.FormRecordId), reencoded);
            s.Evidence.Observe("attacker reads the complete accepted snapshot from BusinessDb", new { items = snapshot.Count, matches_accepted_head = true });

            // The forged candidate: the complete snapshot under a fresh operation, header rewritten to claim authorship by the attacker,
            // with a fabricated form version and a status change. Every other row is carried unchanged.
            var attackOperation = Guid.CreateVersion7();
            var candidateItems = snapshot.Select(r =>
            {
                var columns = new Dictionary<string, RowValue?>(r.Columns, StringComparer.Ordinal) { ["content_change_operation_id"] = RowValue.Uuid(attackOperation) };
                if (r.KindCode == HeaderKindCode)
                {
                    columns["created_by_user_id"] = RowValue.Uuid(s.Attacker.AuthorityUserId);
                    columns["created_form_version_id"] = RowValue.Uuid(Guid.CreateVersion7());
                    columns["record_status"] = RowValue.Text("Submitted");
                }

                return new { item_kind = r.ItemKind, item_id = r.ItemId, canonical_bytes_base64 = Convert.ToBase64String(r.Encode(columns, keyVersion, (ulong)acceptedRevision + 1)) };
            }).ToList();
            var operationsBefore = AuthorityOperationCount(fixture, s.OrganizationId, s.FormRecordId);
            var prepare = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/content-changes/prepare", workload, new
            {
                organization_id = s.OrganizationId,
                form_record_id = s.FormRecordId,
                operation_id = attackOperation,
                expected_authorization_revision = authorizationRevision,
                expected_content_revision = acceptedRevision,
                expected_manifest_root_base64 = acceptedRoot,
                candidate_items = candidateItems,
                current_items = currentItems,
            }, s.Token(s.Attacker), "sec-p11-" + Guid.NewGuid().ToString("N"));
            s.Evidence.ObserveHttp("attacker asks the authority to sign a forged header transition", prepare, "error_code", "detail");
            Assert.Equal(HttpStatusCode.Forbidden, prepare.Status);
            Assert.Equal("FORBIDDEN", prepare.Root.GetProperty("error_code").GetString());
            Assert.Equal("actor may not change this record", prepare.Root.GetProperty("detail").GetString());
            foreach (var material in new[] { "row_signature_base64", "signed_items", "candidate_manifest_root_base64", "candidate_content_revision", "signature_key_version" })
            {
                Assert.DoesNotContain(material, prepare.Body, StringComparison.Ordinal);
            }

            // No unauthorized state: the accepted head, the authorization revision and the record's operations are exactly as before,
            // no operation exists under the attacker's id, and the record is not quarantined.
            var headAfter = await fixture.Authority.SendAsync(HttpMethod.Get, $"/internal/v1/content-revisions/{s.OrganizationId:D}/{s.FormRecordId:D}", workload, null, s.Token(s.Owner));
            Assert.True(headAfter.IsSuccess, headAfter.Body);
            Assert.Equal(acceptedRevision, headAfter.Root.GetProperty("committed_content_revision").GetInt64());
            Assert.Equal(acceptedOperation, headAfter.Root.GetProperty("committed_operation_id").GetGuid());
            Assert.Equal(acceptedRoot, headAfter.Root.GetProperty("committed_manifest_root_base64").GetString());
            Assert.Equal(authorizationRevision, headAfter.Root.GetProperty("authorization_revision").GetInt64());
            Assert.Equal(operationsBefore, AuthorityOperationCount(fixture, s.OrganizationId, s.FormRecordId));
            Assert.Equal("0", fixture.Stack.AuthorityObserverSql($"select count(*) from authority.content_change_operations where content_change_operation_id = '{attackOperation:D}';").Trim());
            Assert.Equal("f", fixture.Stack.AuthorityObserverSql($"select content_head_quarantined from authority.record_content_heads where organization_id = '{s.OrganizationId:D}' and form_record_id = '{s.FormRecordId:D}';").Trim());
            s.Evidence.Observe("state after the refused forged transition", new { head_unchanged = true, attacker_operation_recorded = false, record_quarantined = false });

            // 5. Cross-organization binding: a candidate bound to another organization is refused even for the owner.
            var otherOrg = Guid.CreateVersion7();
            var foreignHeader = RowCanonicalizerV1.Encode(schema, new RowSigningContext(keyVersion, otherOrg, s.FormRecordId, 2), new Dictionary<string, RowValue?>
            {
                ["content_change_operation_id"] = RowValue.Uuid(Guid.CreateVersion7()),
                ["created_by_user_id"] = RowValue.Uuid(s.Owner.AuthorityUserId),
                ["created_form_version_id"] = RowValue.Uuid(Guid.CreateVersion7()),
                ["form_definition_id"] = RowValue.Uuid(s.FormDefinitionId),
                ["record_status"] = RowValue.Text("Draft"),
                ["submitted_on"] = null,
                ["tenant_id"] = RowValue.Uuid(otherOrg),
            });
            var foreign = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/content-changes/prepare", workload, new
            {
                organization_id = s.OrganizationId,
                form_record_id = s.FormRecordId,
                operation_id = Guid.CreateVersion7(),
                expected_authorization_revision = ownerHead.Root.GetProperty("authorization_revision").GetInt64(),
                expected_content_revision = ownerHead.Root.GetProperty("committed_content_revision").GetInt64(),
                expected_manifest_root_base64 = ownerHead.Root.GetProperty("committed_manifest_root_base64").GetString(),
                candidate_items = new[] { new { item_kind = "RecordHeader", item_id = s.FormRecordId, canonical_bytes_base64 = Convert.ToBase64String(foreignHeader) } },
            }, s.Token(s.Owner), "sec-p11-foreign-" + Guid.NewGuid().ToString("N"));
            s.Evidence.ObserveHttp("owner submits a candidate bound to another organization", foreign, "error_code");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.Status);

            // 6. Forged user assertions (wrong key) are rejected by both planes.
            var forgedToken = TestIdentity.ForgedUserToken(s.Owner, s.OrganizationId);
            var forgedApi = await fixture.Api.GetAsync($"/api/v1/form-records/{s.FormRecordId:D}", forgedToken);
            s.Evidence.ObserveHttp("forged user assertion → business API", forgedApi);
            Assert.Equal(HttpStatusCode.Unauthorized, forgedApi.Status);
            var forgedAuthority = await fixture.Authority.SendAsync(HttpMethod.Get, $"/internal/v1/content-revisions/{s.OrganizationId:D}/{s.FormRecordId:D}", workload, null, forgedToken);
            s.Evidence.ObserveHttp("forged user assertion → authority", forgedAuthority);
            Assert.Equal(HttpStatusCode.Unauthorized, forgedAuthority.Status);

            // 7. The business containers carry no signer, key-store or SpiceDB credentials (environment inspection).
            var apiContainer = fixture.Stack.Compose("ps -q api").Trim();
            var env = ComposeStack.Run("docker", $"inspect --format \"{{{{json .Config.Env}}}}\" {apiContainer}", 30);
            var leaked = new[] { "VAULT", "Signing__", "SpiceDb__", "SecurityArchive__", "AuthorityDb", "OPERATOR" }.Where(marker => env.Contains(marker, StringComparison.OrdinalIgnoreCase)).ToList();
            s.Evidence.Observe("business api container environment keys", new { leaked_markers = leaked, key_count = env.Count(c => c == ',') + 1 });
            Assert.Empty(leaked);
            var gate = await fixture.Authority.GateAsync(s.OrganizationId);
            s.Evidence.AuthorityRevision = gate.GetProperty("authorization_revision").GetInt64();
        });
    }

    private const byte HeaderKindCode = 1;
    private const byte FieldValueKindCode = 3;

    // The business signed schemas of the rows this scenario creates (BusinessSignedSchemas.FormRecordsV3 / FieldStringValuesV1).
    private static readonly RowSchemaDefinition HeaderV3 = new("form_records", 3,
    [
        new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, false),
        new RowSignedColumn("created_by_user_id", RowColumnType.Uuid, false),
        new RowSignedColumn("created_form_version_id", RowColumnType.Uuid, false),
        new RowSignedColumn("form_definition_id", RowColumnType.Uuid, false),
        new RowSignedColumn("form_relationship_definition_id", RowColumnType.Uuid, true),
        new RowSignedColumn("parent_form_record_id", RowColumnType.Uuid, true),
        new RowSignedColumn("record_status", RowColumnType.Text, false),
        new RowSignedColumn("relationship_sort_order", RowColumnType.Int64, true),
        new RowSignedColumn("submitted_on", RowColumnType.TimestampUtc, true),
        new RowSignedColumn("tenant_id", RowColumnType.Uuid, false),
    ]);

    private static readonly RowSchemaDefinition StringValueV1 = new("field_string_values", 1,
    [
        new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, false),
        new RowSignedColumn("field_definition_id", RowColumnType.Uuid, false),
        new RowSignedColumn("field_value_item_id", RowColumnType.Uuid, false),
        new RowSignedColumn("form_record_group_id", RowColumnType.Uuid, true),
        new RowSignedColumn("form_record_id", RowColumnType.Uuid, false),
        new RowSignedColumn("string_value", RowColumnType.Text, false),
        new RowSignedColumn("tenant_id", RowColumnType.Uuid, false),
    ]);

    private sealed record SnapshotRow(byte KindCode, string ItemKind, Guid ItemId, RowSchemaDefinition Schema, Guid TenantId, Guid PrimaryKey, ulong RowRevision, uint KeyVersion, byte[] RowSignature, IReadOnlyDictionary<string, RowValue?> Columns)
    {
        public byte[] Encode(IReadOnlyDictionary<string, RowValue?> columns, uint keyVersion, ulong revision) =>
            RowCanonicalizerV1.Encode(Schema, new RowSigningContext(keyVersion, TenantId, PrimaryKey, revision), columns.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal));
    }

    /// <summary>
    /// The accepted operation's rows exactly as the hostile BusinessDb administrator can read them (attacker-sql). Only the row
    /// types this scenario creates are read; completeness is not assumed but proven against the Authority's accepted leaves.
    /// </summary>
    private static List<SnapshotRow> ReadAcceptedSnapshotAsBusinessDba(StageAFixture fixture, Guid formRecordId, Guid operationId)
    {
        var rows = new List<SnapshotRow>();
        foreach (var header in Json(fixture.Stack.AttackerSql($"select row_to_json(h) from (select * from form_record_revisions where form_record_id = '{formRecordId:D}' and content_change_operation_id = '{operationId:D}') h;")))
        {
            Assert.Equal(3, header.GetProperty("row_signature_schema_version").GetInt32());
            rows.Add(new SnapshotRow(HeaderKindCode, "RecordHeader", formRecordId, HeaderV3, Uuid(header, "tenant_id"), Uuid(header, "form_record_id"), RowRevision(header), KeyVersion(header), Signature(header), new Dictionary<string, RowValue?>(StringComparer.Ordinal)
            {
                ["content_change_operation_id"] = RowValue.Uuid(Uuid(header, "content_change_operation_id")),
                ["created_by_user_id"] = RowValue.Uuid(Uuid(header, "created_by_user_id")),
                ["created_form_version_id"] = RowValue.Uuid(Uuid(header, "created_form_version_id")),
                ["form_definition_id"] = RowValue.Uuid(Uuid(header, "form_definition_id")),
                ["form_relationship_definition_id"] = OptionalUuid(header, "form_relationship_definition_id"),
                ["parent_form_record_id"] = OptionalUuid(header, "parent_form_record_id"),
                ["record_status"] = RowValue.Text(header.GetProperty("record_status").GetString()!),
                ["relationship_sort_order"] = header.GetProperty("relationship_sort_order").ValueKind == JsonValueKind.Null ? null : RowValue.Int64(header.GetProperty("relationship_sort_order").GetInt64()),
                ["submitted_on"] = header.GetProperty("submitted_on").ValueKind == JsonValueKind.Null ? null : RowValue.Timestamp(DateTimeOffset.Parse(header.GetProperty("submitted_on").GetString()!, CultureInfo.InvariantCulture)),
                ["tenant_id"] = RowValue.Uuid(Uuid(header, "tenant_id")),
            }));
        }

        foreach (var value in Json(fixture.Stack.AttackerSql($"select row_to_json(v) from (select * from field_string_values where form_record_id = '{formRecordId:D}' and content_change_operation_id = '{operationId:D}') v;")))
        {
            Assert.Equal(1, value.GetProperty("row_signature_schema_version").GetInt32());
            rows.Add(new SnapshotRow(FieldValueKindCode, "FieldValue", Uuid(value, "field_value_item_id"), StringValueV1, Uuid(value, "tenant_id"), Uuid(value, "field_string_value_id"), RowRevision(value), KeyVersion(value), Signature(value), new Dictionary<string, RowValue?>(StringComparer.Ordinal)
            {
                ["content_change_operation_id"] = RowValue.Uuid(Uuid(value, "content_change_operation_id")),
                ["field_definition_id"] = RowValue.Uuid(Uuid(value, "field_definition_id")),
                ["field_value_item_id"] = RowValue.Uuid(Uuid(value, "field_value_item_id")),
                ["form_record_group_id"] = OptionalUuid(value, "form_record_group_id"),
                ["form_record_id"] = RowValue.Uuid(Uuid(value, "form_record_id")),
                ["string_value"] = RowValue.Text(value.GetProperty("string_value").GetString()!),
                ["tenant_id"] = RowValue.Uuid(Uuid(value, "tenant_id")),
            }));
        }

        Assert.Single(rows, r => r.KindCode == HeaderKindCode);
        return rows;
    }

    /// <summary>Test oracle (read-only AuthorityDb): the accepted head's leaves as kind|item id|SHA-256 of the canonical payload.</summary>
    private static List<string> AcceptedLeaves(StageAFixture fixture, Guid organizationId, Guid formRecordId) =>
        fixture.Stack.AuthorityObserverSql(
                "select l.manifest_item_kind_code || '|' || l.manifest_item_id || '|' || encode(l.canonical_payload_digest, 'hex') from authority.record_manifest_leaves l "
                + $"join authority.record_content_heads h on h.current_record_manifest_commit_id = l.record_manifest_commit_id where h.organization_id = '{organizationId:D}' and h.form_record_id = '{formRecordId:D}';")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToList();

    private static string AuthorityOperationCount(StageAFixture fixture, Guid organizationId, Guid formRecordId) =>
        fixture.Stack.AuthorityObserverSql($"select count(*) from authority.content_change_operations where organization_id = '{organizationId:D}' and form_record_id = '{formRecordId:D}';").Trim();

    private static List<JsonElement> Json(string lines) =>
        lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();

    private static Guid Uuid(JsonElement row, string column) => row.GetProperty(column).GetGuid();

    private static RowValue? OptionalUuid(JsonElement row, string column) => row.GetProperty(column).ValueKind == JsonValueKind.Null ? null : RowValue.Uuid(row.GetProperty(column).GetGuid());

    private static ulong RowRevision(JsonElement row) => (ulong)row.GetProperty("row_revision").GetInt64();

    private static uint KeyVersion(JsonElement row) => (uint)row.GetProperty("row_signature_key_version").GetInt32();

    // row_to_json renders bytea as "\x<hex>".
    private static byte[] Signature(JsonElement row) => Convert.FromHexString(row.GetProperty("row_signature").GetString()!.AsSpan(2));
}
