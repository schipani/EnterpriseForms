using System.Net;
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
            var forgedHeader = RowCanonicalizerV1.Encode(schema, new RowSigningContext(keyVersion, s.OrganizationId, s.FormRecordId, 2), new Dictionary<string, RowValue?>
            {
                ["content_change_operation_id"] = RowValue.Uuid(Guid.CreateVersion7()),
                ["created_by_user_id"] = RowValue.Uuid(s.Attacker.AuthorityUserId),
                ["created_form_version_id"] = RowValue.Uuid(Guid.CreateVersion7()),
                ["form_definition_id"] = RowValue.Uuid(s.FormDefinitionId),
                ["record_status"] = RowValue.Text("Submitted"),
                ["submitted_on"] = null,
                ["tenant_id"] = RowValue.Uuid(s.OrganizationId),
            });
            var prepare = await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/content-changes/prepare", workload, new
            {
                organization_id = s.OrganizationId,
                form_record_id = s.FormRecordId,
                operation_id = Guid.CreateVersion7(),
                expected_authorization_revision = ownerHead.Root.GetProperty("authorization_revision").GetInt64(),
                expected_content_revision = ownerHead.Root.GetProperty("committed_content_revision").GetInt64(),
                expected_manifest_root_base64 = ownerHead.Root.GetProperty("committed_manifest_root_base64").GetString(),
                candidate_items = new[] { new { item_kind = "RecordHeader", item_id = s.FormRecordId, canonical_bytes_base64 = Convert.ToBase64String(forgedHeader) } },
            }, s.Token(s.Attacker), "sec-p11-" + Guid.NewGuid().ToString("N"));
            s.Evidence.ObserveHttp("attacker asks the authority to sign a forged header transition", prepare, "error_code");
            Assert.Equal(HttpStatusCode.Forbidden, prepare.Status);
            Assert.DoesNotContain("row_signature_base64", prepare.Body, StringComparison.Ordinal);

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
}
