import { Button, Card, LoadingBlock, Select, StatusMessage, TextArea, TextInput } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { consoleApi, ConsoleApiError, type OnboardingCreateResponse, type OnboardingOwnerInput } from "../api/client";
import { describeError } from "../app/errors";
import { navigate, onboardingPath } from "../app/router";
import { useConsoleSession } from "../app/session";

export const MIN_OWNERS = 2;
export const MAX_OWNERS = 10;
export const RECOMMENDED_OWNERS = 3;
const NAME_MIN = 2;
const NAME_MAX = 200;
const REFERENCE_MIN = 3;
const REFERENCE_MAX = 100;
const JUSTIFICATION_MIN = 10;
const JUSTIFICATION_MAX = 500;

export const verificationMethods: Array<{ value: string; label: string }> = [
  { value: "SupportCallback", label: "Support call-back" },
  { value: "ContractSignatory", label: "Contract signatory" },
  { value: "CustomerAdministratorAttestation", label: "Customer administrator attestation" },
];

interface OwnerRow extends OnboardingOwnerInput {
  rowKey: number;
}

const blankOwner = (rowKey: number, issuerUri: string): OwnerRow => ({ rowKey, issuerUri, subjectIdentifier: "", displayNameHint: "", verificationMethod: "", verificationReference: "" });
const newKey = () => `onb-${crypto.randomUUID()}`;

const lengthError = (value: string, min: number, max: number): string | undefined => {
  const length = value.trim().length;
  return length < min || length > max ? `Enter ${min} to ${max} characters.` : undefined;
};

/** PG-073 New organization: the operator proposes an organization and its initial owners; creating only files the request for two-operator approval. */
export function OnboardingNewPage() {
  const { touch, handleError } = useConsoleSession();
  const issuers = useQuery({ queryKey: ["onboarding-issuers"], queryFn: () => consoleApi.onboardingQueue({ q: "", status: "all", limit: 1 }), staleTime: 60_000 });
  const issuerList = issuers.data?.approvedIdentityIssuers ?? [];
  const nextRow = useRef(RECOMMENDED_OWNERS);
  const [name, setName] = useState("");
  const [owners, setOwners] = useState<OwnerRow[]>(() => Array.from({ length: RECOMMENDED_OWNERS }, (_, i) => blankOwner(i, "")));
  const [justification, setJustification] = useState("");
  const [showErrors, setShowErrors] = useState(false);
  const [busy, setBusy] = useState<"check" | "create" | null>(null);
  const [check, setCheck] = useState<OnboardingCreateResponse | null>(null);
  const [failure, setFailure] = useState<ReturnType<typeof describeError> | null>(null);
  // One idempotency key per form content: a retry of the same submission replays, an edited form gets a fresh key.
  const keyRef = useRef(newKey());
  const headingRef = useRef<HTMLHeadingElement>(null);

  useEffect(() => headingRef.current?.focus(), []);
  useEffect(() => {
    if (issuers.isSuccess) {
      touch();
    }
  }, [issuers.isSuccess, issuers.dataUpdatedAt, touch]);
  useEffect(() => {
    if (issuers.isError) {
      handleError(issuers.error);
    }
  }, [issuers.isError, issuers.error, handleError]);
  // A single approved issuer is the obvious default for every row that has none yet.
  useEffect(() => {
    if (issuerList.length === 1) {
      setOwners((rows) => rows.map((r) => (r.issuerUri ? r : { ...r, issuerUri: issuerList[0]! })));
    }
  }, [issuerList.length, issuerList]);

  const changed = () => {
    keyRef.current = newKey();
    setCheck(null);
    setFailure(null);
  };
  const updateOwner = (rowKey: number, patch: Partial<OnboardingOwnerInput>) => {
    changed();
    setOwners((rows) => rows.map((r) => (r.rowKey === rowKey ? { ...r, ...patch } : r)));
  };

  const unavailable = !issuers.isLoading && !issuers.isError && issuerList.length === 0;
  const nameError = lengthError(name, NAME_MIN, NAME_MAX);
  const justificationError = lengthError(justification, JUSTIFICATION_MIN, JUSTIFICATION_MAX);
  const ownerErrors = owners.map((o) => ({
    issuer: o.issuerUri ? undefined : "Choose an identity issuer.",
    subject: o.subjectIdentifier.trim() ? (o.subjectIdentifier.includes("@") ? "Enter the stable subject, never an email address." : undefined) : "Enter the subject identifier.",
    display: o.displayNameHint.trim() ? undefined : "Enter a display name.",
    method: o.verificationMethod ? undefined : "Choose how the owner was verified.",
    reference: lengthError(o.verificationReference, REFERENCE_MIN, REFERENCE_MAX),
  }));
  const valid = !nameError && !justificationError && owners.length >= MIN_OWNERS && owners.every((_, i) => Object.values(ownerErrors[i]!).every((e) => !e));

  const send = async (dryRun: boolean) => {
    setShowErrors(true);
    setFailure(null);
    if (!valid || unavailable) {
      return;
    }
    setBusy(dryRun ? "check" : "create");
    try {
      const response = await consoleApi.createOnboarding({
        idempotencyKey: keyRef.current,
        organizationDisplayName: name.trim(),
        initialOwners: owners.map((o) => ({
          issuerUri: o.issuerUri,
          verificationMethod: o.verificationMethod,
          subjectIdentifier: o.subjectIdentifier.trim(),
          displayNameHint: o.displayNameHint.trim(),
          verificationReference: o.verificationReference.trim(),
        })),
        justification: justification.trim(),
        dryRun,
      });
      touch();
      if (dryRun) {
        setCheck(response);
      } else if (response.request) {
        navigate(onboardingPath(response.request.requestId));
      } else {
        setFailure({
          title: "The request could not be confirmed",
          message: "The Authority did not return the created request. Check the onboarding queue before trying again.",
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
  const ownerWarning = owners.length < RECOMMENDED_OWNERS;
  return (
    <div className="flex flex-col gap-4">
      <div>
        <Button variant="ghost" size="sm" onClick={() => navigate("/platform/onboarding")}>
          ← Back to onboarding
        </Button>
        <h1 ref={headingRef} tabIndex={-1} className="mt-1 text-2xl font-semibold">
          New organization
        </h1>
        <p className="mt-1 text-sm text-muted">Propose an organization and its initial owners. Creating the request does not create the organization.</p>
      </div>
      {issuers.isLoading ? <LoadingBlock label="Checking onboarding availability" /> : null}
      {issuers.isError ? (
        <StatusMessage tone={describeError(issuers.error).tone} title={describeError(issuers.error).title}>
          {describeError(issuers.error).message}
        </StatusMessage>
      ) : null}
      {unavailable ? (
        <StatusMessage tone="warning" title="Organization onboarding is not available in this environment">
          No identity issuer is approved for onboarding here, so organizations cannot be created from this console.
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
        <Card title="Organization">
          <TextInput
            label="Organization name"
            required
            value={name}
            maxLength={NAME_MAX + 50}
            onChange={(event) => {
              changed();
              setName(event.target.value);
            }}
            error={showErrors || name.length > 0 ? nameError : undefined}
            helpText={`${NAME_MIN} to ${NAME_MAX} characters.`}
            disabled={unavailable}
            autoComplete="off"
          />
        </Card>
        <Card title="Initial owners">
          <p className="text-sm text-muted">
            Each owner must be a person the organization has confirmed to you. Enter between {MIN_OWNERS} and {MAX_OWNERS} owners.
          </p>
          {ownerWarning ? (
            <StatusMessage tone="warning" title={`Fewer than ${RECOMMENDED_OWNERS} owners`}>
              Three owners are recommended: with fewer, losing one owner leaves the organization one step from having no owner who can recover it.
            </StatusMessage>
          ) : null}
          <ol className="flex list-none flex-col gap-4 p-0">
            {owners.map((owner, index) => {
              const errors = ownerErrors[index]!;
              const n = index + 1;
              return (
                <li key={owner.rowKey}>
                  <fieldset className="flex flex-col gap-3 rounded-control border border-line p-4">
                    <legend className="px-1 text-sm font-semibold">Owner {n}</legend>
                    <div className="grid gap-3 sm:grid-cols-2">
                      <Select
                        label={`Identity issuer (owner ${n})`}
                        required
                        value={owner.issuerUri || undefined}
                        onValueChange={(value) => updateOwner(owner.rowKey, { issuerUri: value })}
                        options={issuerList.map((i) => ({ value: i, label: i }))}
                        error={showErrors ? errors.issuer : undefined}
                        disabled={unavailable}
                      />
                      <TextInput
                        label={`Subject identifier (owner ${n})`}
                        required
                        value={owner.subjectIdentifier}
                        onChange={(event) => updateOwner(owner.rowKey, { subjectIdentifier: event.target.value })}
                        error={showErrors || owner.subjectIdentifier.includes("@") ? errors.subject : undefined}
                        helpText="The identity provider's stable subject, never an email address."
                        disabled={unavailable}
                        autoComplete="off"
                      />
                      <TextInput
                        label={`Display name (owner ${n})`}
                        required
                        value={owner.displayNameHint}
                        maxLength={200}
                        onChange={(event) => updateOwner(owner.rowKey, { displayNameHint: event.target.value })}
                        error={showErrors ? errors.display : undefined}
                        disabled={unavailable}
                        autoComplete="off"
                      />
                      <Select
                        label={`Verification method (owner ${n})`}
                        required
                        value={owner.verificationMethod || undefined}
                        onValueChange={(value) => updateOwner(owner.rowKey, { verificationMethod: value })}
                        options={verificationMethods}
                        error={showErrors ? errors.method : undefined}
                        disabled={unavailable}
                      />
                      <TextInput
                        label={`Verification reference (owner ${n})`}
                        required
                        value={owner.verificationReference}
                        maxLength={REFERENCE_MAX + 50}
                        onChange={(event) => updateOwner(owner.rowKey, { verificationReference: event.target.value })}
                        error={showErrors || owner.verificationReference.length > 0 ? errors.reference : undefined}
                        helpText={`A ticket or contract number, ${REFERENCE_MIN} to ${REFERENCE_MAX} characters.`}
                        disabled={unavailable}
                        autoComplete="off"
                        className="sm:col-span-2"
                      />
                    </div>
                    {owners.length > MIN_OWNERS ? (
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        className="self-start"
                        onClick={() => {
                          changed();
                          setOwners((rows) => rows.filter((r) => r.rowKey !== owner.rowKey));
                        }}
                      >
                        Remove owner {n}
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
            disabled={owners.length >= MAX_OWNERS || unavailable}
            onClick={() => {
              changed();
              setOwners((rows) => [...rows, blankOwner(nextRow.current++, issuerList.length === 1 ? issuerList[0]! : "")]);
            }}
          >
            Add owner
          </Button>
        </Card>
        <Card title="Justification">
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
            helpText={`${justification.trim().length} of ${JUSTIFICATION_MAX} characters. Recorded with your name and shown to the approving operators.`}
            disabled={unavailable}
          />
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
                "The request can be created."
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
          {showErrors && !valid && !unavailable ? (
            <StatusMessage tone="danger" title="Fix the highlighted fields">
              Some values are missing or out of range. Nothing was sent.
            </StatusMessage>
          ) : null}
        </div>
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="secondary" busy={busy === "check"} disabled={unavailable || busy !== null} onClick={() => void send(true)}>
            Check
          </Button>
          <Button type="submit" variant="primary" busy={busy === "create"} disabled={unavailable || busy !== null}>
            Create request
          </Button>
        </div>
        <Card title="What happens next">
          <p className="text-sm">
            After you create the request, it needs approvals from two different Platform Operators (you may give one), then the organization key is provisioned by key custody, then an operator
            initializes the organization.
          </p>
        </Card>
      </form>
    </div>
  );
}
