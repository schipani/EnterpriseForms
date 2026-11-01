import { Button, Card, Checkbox, LoadingBlock, Select, StatusMessage, TextArea, TextInput } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useMemo, useRef, useState } from "react";
import { consoleApi, ConsoleApiError, type RecoveryCreateInput, type RecoveryCreateResponse } from "../api/client";
import { describeError } from "../app/errors";
import { identitySummary } from "../app/format";
import { navigate, recoveryPath } from "../app/router";
import { useConsoleSession } from "../app/session";
import { verificationMethods } from "./OnboardingNewPage";

export const MIN_REPLACEMENTS = 1;
export const MAX_REPLACEMENTS = 3;
const REFERENCE_MIN = 3;
const REFERENCE_MAX = 100;
const SUMMARY_MIN = 10;
const SUMMARY_MAX = 500;
const JUSTIFICATION_MIN = 10;
const JUSTIFICATION_MAX = 500;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export const unavailabilityReasons: Array<{ value: string; label: string }> = [
  { value: "LeftOrganization", label: "Left the organization" },
  { value: "AccountDeleted", label: "Account deleted" },
  { value: "Deceased", label: "Deceased" },
  { value: "LostAuthenticator", label: "Lost authenticator" },
  { value: "Unresponsive", label: "Unresponsive" },
];

const sourceOptions = [
  { value: "existing", label: "Existing member (by user ID)" },
  { value: "new", label: "New identity" },
];

interface DeclaredRow {
  declared: boolean;
  reason: string;
  evidenceReference: string;
  evidenceSummary: string;
}
interface ReplacementRow {
  rowKey: number;
  source: "existing" | "new";
  existingAuthorityUserId: string;
  issuerUri: string;
  subjectIdentifier: string;
  displayNameHint: string;
  verificationMethod: string;
  verificationReference: string;
}

const blankReplacement = (rowKey: number, issuerUri: string): ReplacementRow => ({
  rowKey,
  source: "new",
  existingAuthorityUserId: "",
  issuerUri,
  subjectIdentifier: "",
  displayNameHint: "",
  verificationMethod: "",
  verificationReference: "",
});
const blankDeclared = (): DeclaredRow => ({ declared: false, reason: "", evidenceReference: "", evidenceSummary: "" });
const newKey = () => `rec-${crypto.randomUUID()}`;

const lengthError = (value: string, min: number, max: number): string | undefined => {
  const length = value.trim().length;
  return length < min || length > max ? `Enter ${min} to ${max} characters.` : undefined;
};

/** PG-075 new recovery case: the operator proposes replacement owners; creating only opens the case for three-operator approval. */
export function RecoveryNewPage() {
  const { touch, handleError } = useConsoleSession();
  const organizationId = new URLSearchParams(window.location.search).get("organizationId") ?? "";
  const validId = UUID.test(organizationId);
  const organization = useQuery({
    queryKey: ["recovery-organization", organizationId],
    queryFn: () => consoleApi.organization(organizationId),
    enabled: validId,
    staleTime: Infinity,
    gcTime: 0,
  });
  const owners = useMemo(() => organization.data?.owners ?? [], [organization.data]);
  const enabledOwners = owners.filter((o) => o.enabled);
  const issuerList = useMemo(() => [...new Set(owners.map((o) => o.issuerUri))], [owners]);
  const nextRow = useRef(1);
  const [incident, setIncident] = useState("");
  const [justification, setJustification] = useState("");
  const [declared, setDeclared] = useState<Record<string, DeclaredRow>>({});
  const [replacements, setReplacements] = useState<ReplacementRow[]>(() => [blankReplacement(0, "")]);
  const [showErrors, setShowErrors] = useState(false);
  const [busy, setBusy] = useState<"check" | "create" | null>(null);
  const [check, setCheck] = useState<RecoveryCreateResponse | null>(null);
  const [failure, setFailure] = useState<ReturnType<typeof describeError> | null>(null);
  // One idempotency key per form content: a retry of the same submission replays, an edited form gets a fresh key.
  const keyRef = useRef(newKey());
  const headingRef = useRef<HTMLHeadingElement>(null);

  useEffect(() => headingRef.current?.focus(), []);
  useEffect(() => {
    if (organization.isSuccess) {
      touch();
    }
  }, [organization.isSuccess, organization.dataUpdatedAt, touch]);
  useEffect(() => {
    if (organization.isError) {
      handleError(organization.error);
    }
  }, [organization.isError, organization.error, handleError]);
  // A single issuer is the obvious default for every new-identity row that has none yet.
  useEffect(() => {
    if (issuerList.length === 1) {
      setReplacements((rows) => (rows.every((r) => r.issuerUri) ? rows : rows.map((r) => (r.issuerUri ? r : { ...r, issuerUri: issuerList[0]! }))));
    }
  }, [issuerList]);

  const changed = () => {
    keyRef.current = newKey();
    setCheck(null);
    setFailure(null);
  };
  const declaredOf = (id: string): DeclaredRow => declared[id] ?? blankDeclared();
  const updateDeclared = (id: string, patch: Partial<DeclaredRow>) => {
    changed();
    setDeclared((current) => ({ ...current, [id]: { ...declaredOf(id), ...patch } }));
  };
  const updateReplacement = (rowKey: number, patch: Partial<ReplacementRow>) => {
    changed();
    setReplacements((rows) => rows.map((r) => (r.rowKey === rowKey ? { ...r, ...patch } : r)));
  };

  const incidentError = lengthError(incident, REFERENCE_MIN, REFERENCE_MAX);
  const justificationError = lengthError(justification, JUSTIFICATION_MIN, JUSTIFICATION_MAX);
  const declaredErrors = enabledOwners.map((o) => {
    const d = declaredOf(o.authorityUserId);
    return d.declared
      ? { reason: d.reason ? undefined : "Choose a reason.", reference: lengthError(d.evidenceReference, REFERENCE_MIN, REFERENCE_MAX), summary: lengthError(d.evidenceSummary, SUMMARY_MIN, SUMMARY_MAX) }
      : {};
  });
  const replacementErrors = replacements.map((r) => ({
    user: r.source === "existing" ? (UUID.test(r.existingAuthorityUserId.trim()) ? undefined : "Enter the member's user ID.") : undefined,
    issuer: r.source === "new" ? (r.issuerUri.trim() ? undefined : "Enter the identity issuer.") : undefined,
    subject: r.source === "new" ? (r.subjectIdentifier.trim() ? (r.subjectIdentifier.includes("@") ? "Enter the stable subject, never an email address." : undefined) : "Enter the subject identifier.") : undefined,
    display: r.source === "new" ? (r.displayNameHint.trim() ? undefined : "Enter a display name.") : undefined,
    method: r.verificationMethod ? undefined : "Choose how the owner was verified.",
    reference: lengthError(r.verificationReference, REFERENCE_MIN, REFERENCE_MAX),
  }));
  const valid =
    validId &&
    !incidentError &&
    !justificationError &&
    replacements.length >= MIN_REPLACEMENTS &&
    declaredErrors.every((e) => Object.values(e).every((v) => !v)) &&
    replacementErrors.every((e) => Object.values(e).every((v) => !v));

  const send = async (dryRun: boolean) => {
    setShowErrors(true);
    setFailure(null);
    if (!valid || !organization.isSuccess) {
      return;
    }
    setBusy(dryRun ? "check" : "create");
    const body: RecoveryCreateInput = {
      idempotencyKey: keyRef.current,
      organizationId,
      incidentReference: incident.trim(),
      justification: justification.trim(),
      declaredUnavailable: enabledOwners
        .filter((o) => declaredOf(o.authorityUserId).declared)
        .map((o) => {
          const d = declaredOf(o.authorityUserId);
          return { authorityUserId: o.authorityUserId, reason: d.reason, evidenceReference: d.evidenceReference.trim(), evidenceSummary: d.evidenceSummary.trim() };
        }),
      replacements: replacements.map((r) =>
        r.source === "existing"
          ? { existingAuthorityUserId: r.existingAuthorityUserId.trim(), issuerUri: null, subjectIdentifier: null, displayNameHint: null, verificationMethod: r.verificationMethod, verificationReference: r.verificationReference.trim() }
          : {
              existingAuthorityUserId: null,
              issuerUri: r.issuerUri.trim(),
              subjectIdentifier: r.subjectIdentifier.trim(),
              displayNameHint: r.displayNameHint.trim(),
              verificationMethod: r.verificationMethod,
              verificationReference: r.verificationReference.trim(),
            },
      ),
      dryRun,
    };
    try {
      const response = await consoleApi.createRecovery(body);
      touch();
      if (dryRun) {
        setCheck(response);
      } else if (response.case) {
        navigate(recoveryPath(response.case.caseId));
      } else if (!response.eligible) {
        setCheck(response);
      } else {
        setFailure({
          title: "The case could not be confirmed",
          message: "The Authority did not return the created case. Check the recovery queue before trying again.",
          tone: "warning",
          reload: false,
          retryable: false,
        });
      }
    } catch (error) {
      if (!handleError(error)) {
        setFailure(describeError(error));
      }
    } finally {
      setBusy(null);
    }
  };

  const refusal = check && !check.eligible ? describeError(new ConsoleApiError(409, check.refusalCode ?? "", check.refusalDetail ?? "", false)) : null;
  const loadFailure = organization.isError ? describeError(organization.error) : null;
  const disabled = !validId || !organization.isSuccess;
  return (
    <div className="flex flex-col gap-4">
      <div>
        <Button variant="ghost" size="sm" onClick={() => navigate("/platform/recovery")}>
          ← Back to owner recovery
        </Button>
        <h1 ref={headingRef} tabIndex={-1} className="mt-1 text-2xl font-semibold">
          New recovery case
        </h1>
        <p className="mt-1 text-sm text-muted">Propose replacement owners for an organization that lost its owners. Creating the case does not approve it and changes nothing until three operators approve and the waiting period ends.</p>
      </div>
      {!validId ? (
        <StatusMessage tone="warning" title="Choose an organization first">
          Start from an organization that needs owners on the owner recovery page.
        </StatusMessage>
      ) : null}
      {organization.isLoading ? <LoadingBlock label="Loading organization" /> : null}
      {loadFailure ? (
        <StatusMessage tone={loadFailure.tone} title={loadFailure.title}>
          {loadFailure.message}
          {loadFailure.reference ? ` Reference ${loadFailure.reference}.` : ""}
        </StatusMessage>
      ) : null}
      <form
        noValidate
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          void send(false);
        }}
      >
        <Card title="Organization and incident">
          {organization.data ? (
            <p className="text-sm">
              Organization: <strong className="break-words">{organization.data.summary.organizationDisplayName}</strong> · {Number(organization.data.summary.enabledOwnerCount)} enabled{" "}
              {Number(organization.data.summary.enabledOwnerCount) === 1 ? "owner" : "owners"}
            </p>
          ) : null}
          <TextInput
            label="Incident reference"
            required
            value={incident}
            maxLength={REFERENCE_MAX + 50}
            onChange={(event) => {
              changed();
              setIncident(event.target.value);
            }}
            error={showErrors || incident.length > 0 ? incidentError : undefined}
            helpText={`A ticket or case number, ${REFERENCE_MIN} to ${REFERENCE_MAX} characters.`}
            disabled={disabled}
            autoComplete="off"
          />
          <TextArea
            label="Your justification"
            required
            rows={3}
            value={justification}
            maxLength={JUSTIFICATION_MAX + 100}
            onChange={(event) => {
              changed();
              setJustification(event.target.value);
            }}
            error={showErrors || justification.length > 0 ? justificationError : undefined}
            helpText={`${justification.trim().length} of ${JUSTIFICATION_MAX} characters. Recorded with your name and shown to the approving operators and the organization's owners.`}
            disabled={disabled}
          />
        </Card>
        <Card title="Current owners">
          {owners.length === 0 && organization.isSuccess ? <p className="text-sm text-muted">This organization has no owners.</p> : null}
          <ul aria-label="Current owners" className="flex list-none flex-col gap-3 p-0">
            {owners.map((owner) => {
              const identity = identitySummary(owner);
              const d = declaredOf(owner.authorityUserId);
              const index = enabledOwners.findIndex((o) => o.authorityUserId === owner.authorityUserId);
              const errors = index >= 0 ? declaredErrors[index]! : {};
              return (
                <li key={owner.authorityUserId} className="flex flex-col gap-3 rounded-control border border-line p-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="min-w-0">
                      <p className="break-words font-medium">{identity.subject}</p>
                      <p className="break-words text-help text-muted">{identity.issuer}</p>
                    </div>
                    <span className="text-help">{owner.enabled ? "Enabled" : "Disabled"}</span>
                  </div>
                  {owner.enabled ? (
                    <>
                      <Checkbox
                        label={`Declare ${identity.subject} unavailable`}
                        checked={d.declared}
                        onCheckedChange={(checked) => updateDeclared(owner.authorityUserId, { declared: checked })}
                        disabled={disabled}
                      />
                      {d.declared ? (
                        <div className="grid gap-3 sm:grid-cols-2">
                          <Select
                            label={`Reason (${identity.subject})`}
                            required
                            value={d.reason || undefined}
                            onValueChange={(value) => updateDeclared(owner.authorityUserId, { reason: value })}
                            options={unavailabilityReasons}
                            error={showErrors ? errors.reason : undefined}
                          />
                          <TextInput
                            label={`Evidence reference (${identity.subject})`}
                            required
                            value={d.evidenceReference}
                            maxLength={REFERENCE_MAX + 50}
                            onChange={(event) => updateDeclared(owner.authorityUserId, { evidenceReference: event.target.value })}
                            error={showErrors || d.evidenceReference.length > 0 ? errors.reference : undefined}
                            helpText={`${REFERENCE_MIN} to ${REFERENCE_MAX} characters.`}
                            autoComplete="off"
                          />
                          <TextArea
                            label={`Evidence summary (${identity.subject})`}
                            required
                            rows={2}
                            value={d.evidenceSummary}
                            maxLength={SUMMARY_MAX + 100}
                            onChange={(event) => updateDeclared(owner.authorityUserId, { evidenceSummary: event.target.value })}
                            error={showErrors || d.evidenceSummary.length > 0 ? errors.summary : undefined}
                            helpText={`${SUMMARY_MIN} to ${SUMMARY_MAX} characters. Describe what was checked; do not paste personal details.`}
                            className="sm:col-span-2"
                          />
                        </div>
                      ) : null}
                    </>
                  ) : null}
                </li>
              );
            })}
          </ul>
        </Card>
        <Card title="Replacement owners">
          <p className="text-sm text-muted">
            Enter between {MIN_REPLACEMENTS} and {MAX_REPLACEMENTS} replacement owners, each confirmed to you by the organization. A new identity is an owner that signs in for the first time.
          </p>
          <ol className="flex list-none flex-col gap-4 p-0">
            {replacements.map((row, index) => {
              const errors = replacementErrors[index]!;
              const n = index + 1;
              return (
                <li key={row.rowKey}>
                  <fieldset className="flex flex-col gap-3 rounded-control border border-line p-4">
                    <legend className="px-1 text-sm font-semibold">Replacement {n}</legend>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <Select
                        label={`Source (replacement ${n})`}
                        required
                        value={row.source}
                        onValueChange={(value) => updateReplacement(row.rowKey, { source: value === "existing" ? "existing" : "new" })}
                        options={sourceOptions}
                        disabled={disabled}
                      />
                      {row.source === "existing" ? (
                        <TextInput
                          label={`User ID (replacement ${n})`}
                          required
                          value={row.existingAuthorityUserId}
                          onChange={(event) => updateReplacement(row.rowKey, { existingAuthorityUserId: event.target.value })}
                          error={showErrors ? errors.user : undefined}
                          helpText="The member's Authority user ID."
                          disabled={disabled}
                          autoComplete="off"
                          className="sm:col-span-2"
                        />
                      ) : (
                        <>
                          {issuerList.length > 0 ? (
                            <Select
                              label={`Identity issuer (replacement ${n})`}
                              required
                              value={row.issuerUri || undefined}
                              onValueChange={(value) => value && updateReplacement(row.rowKey, { issuerUri: value })}
                              options={issuerList.map((i) => ({ value: i, label: i }))}
                              error={showErrors ? errors.issuer : undefined}
                              disabled={disabled}
                            />
                          ) : (
                            <TextInput
                              label={`Identity issuer (replacement ${n})`}
                              required
                              value={row.issuerUri}
                              onChange={(event) => updateReplacement(row.rowKey, { issuerUri: event.target.value })}
                              error={showErrors ? errors.issuer : undefined}
                              disabled={disabled}
                              autoComplete="off"
                            />
                          )}
                          <TextInput
                            label={`Subject identifier (replacement ${n})`}
                            required
                            value={row.subjectIdentifier}
                            onChange={(event) => updateReplacement(row.rowKey, { subjectIdentifier: event.target.value })}
                            error={showErrors || row.subjectIdentifier.includes("@") ? errors.subject : undefined}
                            helpText="The identity provider's stable subject, never an email address."
                            disabled={disabled}
                            autoComplete="off"
                          />
                          <TextInput
                            label={`Display name (replacement ${n})`}
                            required
                            value={row.displayNameHint}
                            maxLength={200}
                            onChange={(event) => updateReplacement(row.rowKey, { displayNameHint: event.target.value })}
                            error={showErrors ? errors.display : undefined}
                            disabled={disabled}
                            autoComplete="off"
                          />
                        </>
                      )}
                      <Select
                        label={`Verification method (replacement ${n})`}
                        required
                        value={row.verificationMethod || undefined}
                        onValueChange={(value) => updateReplacement(row.rowKey, { verificationMethod: value })}
                        options={verificationMethods}
                        error={showErrors ? errors.method : undefined}
                        disabled={disabled}
                      />
                      <TextInput
                        label={`Verification reference (replacement ${n})`}
                        required
                        value={row.verificationReference}
                        maxLength={REFERENCE_MAX + 50}
                        onChange={(event) => updateReplacement(row.rowKey, { verificationReference: event.target.value })}
                        error={showErrors || row.verificationReference.length > 0 ? errors.reference : undefined}
                        helpText={`A ticket or contract number, ${REFERENCE_MIN} to ${REFERENCE_MAX} characters.`}
                        disabled={disabled}
                        autoComplete="off"
                      />
                    </div>
                    {replacements.length > MIN_REPLACEMENTS ? (
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        className="self-start"
                        onClick={() => {
                          changed();
                          setReplacements((rows) => rows.filter((r) => r.rowKey !== row.rowKey));
                        }}
                      >
                        Remove replacement {n}
                      </Button>
                    ) : null}
                  </fieldset>
                </li>
              );
            })}
          </ol>
          <Button
            type="button"
            variant="secondary"
            size="sm"
            className="self-start"
            disabled={replacements.length >= MAX_REPLACEMENTS || disabled}
            onClick={() => {
              changed();
              setReplacements((rows) => [...rows, blankReplacement(nextRow.current++, issuerList.length === 1 ? issuerList[0]! : "")]);
            }}
          >
            Add replacement
          </Button>
        </Card>
        <div aria-live="polite" className="flex flex-col gap-3">
          {check && check.eligible ? (
            <StatusMessage tone={check.warnings.length > 0 ? "warning" : "success"} title="Check passed — nothing was created">
              {check.warnings.length > 0 ? (
                <ul className="list-disc pl-5">
                  {check.warnings.map((w) => (
                    <li key={w}>{w}</li>
                  ))}
                </ul>
              ) : (
                "The case can be created."
              )}
            </StatusMessage>
          ) : null}
          {refusal ? (
            <StatusMessage tone={refusal.tone} title={refusal.title}>
              {refusal.message}
              {check?.refusalDetail ? <span className="mt-1 block text-help">{check.refusalDetail}</span> : null}
              {refusal.reference ? ` Reference ${refusal.reference}.` : ""}
            </StatusMessage>
          ) : null}
          {failure ? (
            <StatusMessage tone={failure.tone} title={failure.title}>
              {failure.message}
              {failure.reference ? ` Reference ${failure.reference}.` : ""}
            </StatusMessage>
          ) : null}
          {showErrors && !valid && validId ? (
            <StatusMessage tone="danger" title="Fix the highlighted fields">
              Some values are missing or out of range. Nothing was sent.
            </StatusMessage>
          ) : null}
        </div>
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="secondary" busy={busy === "check"} disabled={disabled || busy !== null} onClick={() => void send(true)}>
            Validate
          </Button>
          <Button type="submit" variant="primary" busy={busy === "create"} disabled={disabled || busy !== null}>
            Create case
          </Button>
        </div>
        <Card title="What happens next">
          <p className="text-sm">
            After you create the case, three different Platform Operators must approve it (you may give one). A waiting period then lets the organization's owners object. When it ends and
            the approvals stand, an operator executes the recovery.
          </p>
        </Card>
      </form>
    </div>
  );
}
