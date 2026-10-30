import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, Checkbox, LoadingBlock, Select, StatusMessage, TextArea } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { ErrorPanel } from "../../support";

const CLASSIFICATIONS = ["Public", "Internal", "Confidential", "Restricted"] as const;

/**
 * Organization data-protection policy (ADR-PB-004): the minimum classification designers may assign and whether
 * Public/Internal fields may be stored as plaintext at all. Until the owner saves it, it is the documented development
 * default and says so. Changing it is audited; it never changes how stored values are protected.
 */
export function SecurityPolicyCard() {
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const policy = useQuery({ queryKey: ["organization", "security-policy"], queryFn: () => api.organization.securityPolicy().then((r) => r.result) });
  const [minimum, setMinimum] = useState("Internal");
  const [plaintext, setPlaintext] = useState(true);
  const [downgrade, setDowngrade] = useState(false);
  const [searchable, setSearchable] = useState(false);
  const [statement, setStatement] = useState("");
  const [loaded, setLoaded] = useState(false);
  useEffect(() => {
    if (policy.data && !loaded) {
      setMinimum(policy.data.minimumFieldClassification);
      setPlaintext(policy.data.plaintextInternalFieldsPermitted);
      setDowngrade(policy.data.plaintextDowngradePermitted);
      setSearchable(policy.data.plaintextSearchPermitted);
      setStatement(policy.data.isUnreviewedDefault ? "" : policy.data.policyStatementText);
      setLoaded(true);
    }
  }, [policy.data, loaded]);
  const save = useMutation({
    mutationFn: () => api.organization.updateSecurityPolicy({ minimumFieldClassification: minimum, plaintextInternalFieldsPermitted: plaintext, plaintextDowngradePermitted: downgrade, plaintextSearchPermitted: searchable, policyStatementText: statement.trim(), expectedPolicyRevision: Number(policy.data!.securityPolicyRevision) }).then((r) => r.result),
    onSuccess: async () => {
      setLoaded(false);
      await queryClient.invalidateQueries({ queryKey: ["organization", "security-policy"] });
    },
  });
  const canManage = session.hasCapability("organization.manage");
  if (policy.isPending) {
    return <LoadingBlock label="Loading data-protection policy" />;
  }
  if (policy.isError) {
    return <ErrorPanel error={policy.error} onRetry={() => void policy.refetch()} />;
  }
  return (
    <Card title={<span id="data-protection-policy-heading">Data protection policy</span>} aria-labelledby="data-protection-policy-heading">
      <div className="flex flex-wrap gap-2">
        <Badge tone={policy.data.isUnreviewedDefault ? "warning" : "success"}>{policy.data.isUnreviewedDefault ? "Development default — not yet reviewed" : `Reviewed ${new Date(policy.data.policyReviewedOn!).toLocaleDateString()}`}</Badge>
        <Badge tone="neutral">Policy revision {String(policy.data.securityPolicyRevision)}</Badge>
      </div>
      <p className="text-sm text-muted">New free text and documents are always Confidential and encrypted by default. This policy decides what designers may choose beyond that default. It never decrypts or re-protects stored values.</p>
      <Select label="Minimum field classification" value={minimum} disabled={!canManage} options={CLASSIFICATIONS.map((value) => ({ value, label: value }))} onValueChange={setMinimum} helpText="Designers cannot classify a field below this. Confidential or higher means every new field is encrypted." />
      <Checkbox label="Permit plaintext storage for Public/Internal fields" checked={plaintext} disabled={!canManage} onCheckedChange={(checked) => setPlaintext(checked === true)} helpText="Plaintext values are searchable within authorization and readable by a database administrator. Turning this off requires encryption for every new field." />
      <Checkbox label="Permit removal of encryption from Public/Internal fields (governance)" checked={downgrade} disabled={!canManage || !plaintext} onCheckedChange={(checked) => setDowngrade(checked === true)} helpText="ADR-PB-004 Amendment A1. Lets the organization owner or a system administrator release an already encrypted Public/Internal field to plaintext through an audited background migration. Confidential or Restricted fields first need a declassification with a governance approval reference by one administrator and the release by a different one. The policy is published to the Authorization Authority, which enforces it." />
      <Checkbox label="Permit search approvals for plaintext Public/Internal fields" checked={searchable} disabled={!canManage || !plaintext} onCheckedChange={(checked) => setSearchable(checked === true)} helpText="Phase C7 (AC-6). Lets a security administrator approve individual plaintext Public/Internal fields for Advanced Search. Encrypted, Confidential and Restricted fields can never be approved. Turning this off makes every existing approval unusable and invalidates it in the Authorization Authority. Searching itself stays off until trusted search is enabled." />
      <TextArea label="Policy statement" value={statement} rows={3} maxLength={2000} disabled={!canManage} required helpText="Why plaintext is or is not acceptable for this organization (10 to 2000 characters); recorded with the audit event." onChange={(event) => setStatement(event.target.value)} />
      {!canManage ? <StatusMessage tone="info" title="Read-only">Only the organization owner can change this policy.</StatusMessage> : null}
      {save.isError ? <ErrorPanel error={save.error} /> : null}
      {save.isSuccess ? <StatusMessage tone="success" title="Policy saved" /> : null}
      {canManage ? (
        <div>
          <Button busy={save.isPending} disabled={statement.trim().length < 10} onClick={() => save.mutate()}>
            Save data protection policy
          </Button>
        </div>
      ) : null}
    </Card>
  );
}
