# E3-M2c test results

Captured after the enforcement-removal run (final rebuild), working tree = the E3-M2c change set on `9810a67`.

| Suite | Baseline at 9810a67 (passed / failed / skipped) | E3-M2c (total / passed / failed / skipped) |
|---|---|---|
| Contract (EnterpriseForms.Api.ContractTests, full) | 818 / 0 / 9 | 892 / 880 / 0 / 12 |
| Architecture (full) | 134 / 0 / 0 | 142 / 142 / 0 / 0 |
| Authority (AuthorizationAuthority.Tests, full) | 849 / 0 / 4 | 853 / 849 / 0 / 4 |
| Integrity (EnterpriseForms.Integrity.Tests, full) | 73 / 0 / 0 | 73 / 73 / 0 / 0 |
| Real Vault 1.20.4 (EnvelopeV2ReaderVaultIntegrationTests) | — | 3 / 3 / 0 / 0 |

The 12 contract skips are the 9 pre-existing real-Vault classes' tests plus the 3 new `EnvelopeV2ReaderVaultIntegrationTests`, which run only under `scripts/dev/e3-m2c-vault-reader.ps1` (row 5).

## New contract tests (65)

- `EnterpriseForms.Api.ContractTests.DecryptionModeProvenanceTests.AProtectionSelfCheckDecryptsOnlyItsOwnSeal`
- `EnterpriseForms.Api.ContractTests.DecryptionModeProvenanceTests.PathCTheCreationResponseRowsAreThisRequestsOwnSealsOnTheFirstAttemptAndOnEveryRetry`
- `EnterpriseForms.Api.ContractTests.DecryptionModeProvenanceTests.PathDTheOwnerUpdateResponseRowsAreFreshSealsAlsoForCarriedValuesAndOnAnIdempotentReplay`
- `EnterpriseForms.Api.ContractTests.DecryptionModeProvenanceTests.TheCandidateBuilderReadsTheCurrentRowsOnlyThroughTheVerifiedReader`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.AGenuineV2RowNeverOpensThroughTheSelfSealedApiWhateverItsLabels`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.ASchema1RowCarryingAnyV2ColumnIsRefused(column: "commitment")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.ASchema1RowCarryingAnyV2ColumnIsRefused(column: "identifier")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.ASchema1RowCarryingAnyV2ColumnIsRefused(column: "wrap-code")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.CancellationStopsTheVerifiedReaderWithoutAPartialResultOrAFailureRecord`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.EveryOtherSchemaAndEnvelopeCombinationIsRefusedBeforeTheProvider(schema: 0, envelope: 1)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.EveryOtherSchemaAndEnvelopeCombinationIsRefusedBeforeTheProvider(schema: 0, envelope: 2)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.EveryOtherSchemaAndEnvelopeCombinationIsRefusedBeforeTheProvider(schema: 1, envelope: 0)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.EveryOtherSchemaAndEnvelopeCombinationIsRefusedBeforeTheProvider(schema: 1, envelope: 2)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.EveryOtherSchemaAndEnvelopeCombinationIsRefusedBeforeTheProvider(schema: 2, envelope: 1)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.EveryOtherSchemaAndEnvelopeCombinationIsRefusedBeforeTheProvider(schema: 2, envelope: 3)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.EveryOtherSchemaAndEnvelopeCombinationIsRefusedBeforeTheProvider(schema: 3, envelope: 3)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "algorithm")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "commitment-31")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "document-purpose-reference")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "foreign-organization-reference")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "identifier-not-v7")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "legacy-reference")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "no-commitment")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "no-identifier")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "no-wrap-code")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "nonce-11")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "sealed-key-type")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "tag-15")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "wrap-code-3")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "wrap-code-family-mismatch")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.IncompleteOrUnexpectedV2MetadataIsRefusedBeforeTheProvider(defect: "wrapped-59")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.OrganizationAndPurposeIsolationHoldsBeforeAnyKeyOperation`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.ProviderFailuresOfAV2RowFailClosedWithoutFallback`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.SelfSealedAcceptsEnvelope1OfSchema0Or1(schema: 0)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.SelfSealedAcceptsEnvelope1OfSchema0Or1(schema: 1)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.SelfSealedRefusesAnEnvelope1RowCarryingV2Metadata(column: "commitment")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.SelfSealedRefusesAnEnvelope1RowCarryingV2Metadata(column: "identifier")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.SelfSealedRefusesAnEnvelope1RowCarryingV2Metadata(column: "wrap-code")`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.SelfSealedRefusesSchema2AndEnvelope2BeforeTheProvider(schema: 0, envelope: 2)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.SelfSealedRefusesSchema2AndEnvelope2BeforeTheProvider(schema: 1, envelope: 2)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.SelfSealedRefusesSchema2AndEnvelope2BeforeTheProvider(schema: 2, envelope: 1)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.SelfSealedRefusesSchema2AndEnvelope2BeforeTheProvider(schema: 2, envelope: 2)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.SelfSealedRefusesSchema2AndEnvelope2BeforeTheProvider(schema: 3, envelope: 1)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TamperedV2RowsAreRefusedByTheCommitmentAndTheAead`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheExactKeyVersionNamedByTheReferenceIsUsedWithoutFallback`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheNamedSelfSealedEntryPointsAcceptOnlyRowsThisProcessFreshlySealed`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedBatchRefusesNullAndRepeatedRows`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedDocumentEnvelopeCarriesTheStoredColumnsExactly`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedDocumentReaderFailsClosedOnTamperIdentityAndKeyFailures`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedDocumentReaderOpensV1AndV2VersionsWithOneUnwrapEach`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedDocumentReaderRefusesEveryOtherCombinationBeforeTheProvider(schema: 0, envelopeVersion: 1, withV2Columns: False)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedDocumentReaderRefusesEveryOtherCombinationBeforeTheProvider(schema: 0, envelopeVersion: 2, withV2Columns: True)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedDocumentReaderRefusesEveryOtherCombinationBeforeTheProvider(schema: 1, envelopeVersion: 1, withV2Columns: True)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedDocumentReaderRefusesEveryOtherCombinationBeforeTheProvider(schema: 1, envelopeVersion: 2, withV2Columns: True)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedDocumentReaderRefusesEveryOtherCombinationBeforeTheProvider(schema: 2, envelopeVersion: 1, withV2Columns: False)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedDocumentReaderRefusesEveryOtherCombinationBeforeTheProvider(schema: 2, envelopeVersion: 2, withV2Columns: False)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheVerifiedDocumentReaderRefusesEveryOtherCombinationBeforeTheProvider(schema: 3, envelopeVersion: 3, withV2Columns: True)`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.TheWorkerEntryPointReadsV2RowsSealedUnderTheRealLocalKeyring`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.VerifiedSchema1Envelope1OpensWithTheV1ReaderAndSchema0IsRefusedBeforeTheProvider`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ProductReaderTests.VerifiedSchema2Envelope2DispatchesToTheV2ReaderWithOneUnwrapPerDataKeyGroup`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ReaderVaultIntegrationTests.TheExactVaultKeyVersionIsUsedAndARetiredVersionIsNotReplacedByTheCurrentOne`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ReaderVaultIntegrationTests.TheVerifiedDocumentReaderOpensAVaultWrappedV2VersionAndRefusesAForeignOrganization`
- `EnterpriseForms.Api.ContractTests.EnvelopeV2ReaderVaultIntegrationTests.TheVerifiedFieldReaderOpensVaultWrappedV1AndV2RowsWithOneUnwrapPerDataKeyGroup`
- `EnterpriseForms.Api.ContractTests.PhaseC5SharedEditTests.PathEAnIdempotentReplayKeepsTheSameSplitAndCannotPassStoredContentAsSelfSealed`
- `EnterpriseForms.Api.ContractTests.PhaseC5SharedEditTests.PathETheSharedEditResponseDecryptsEditedRowsSelfSealedAndCopiedRowsVerified`

## New architecture tests (9)

- `EnterpriseForms.ArchitectureTests.WorkerFieldReaderCompositionTests.TheComposedFieldReaderIsTheS8DispatcherAndRefusesUnstagedContent`
- `EnterpriseForms.ArchitectureTests.WorkerFieldReaderCompositionTests.TheWorkerProgramComposesTheBusinessInfrastructure`
- `EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests.AnUnapprovedSelfSealedCallerIsDetected`
- `EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests.NoStoredContentReaderSwitchesToSelfSealedMode`
- `EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests.OnlyTheVerifiedDocumentDispatcherReachesTheV1DocumentDecryptor`
- `EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests.SelfSealedCallersArePinnedToTheReviewedInventory`
- `EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests.TheVerifiedStorageReadersUseTheVerifiedReaderOnly`
- `EnterpriseForms.ArchitectureTests.EnvelopeV2IsolationTests.OnlyTheApprovedDecryptionServicesReachEnvelopeV2AndOnlyThroughTheProductReader`
- `EnterpriseForms.ArchitectureTests.EnvelopeV2IsolationTests.TheProductReaderReachesNoWriterEntryPointOrKeySchedule`

## After the final security closure

| Suite | Total / passed / failed / skipped |
|---|---|
| Contract (full) | 913 / 901 / 0 / 12 |
| Architecture (full) | 144 / 144 / 0 / 0 |

New classes: `HandlerDecryptionModeTests` (8), `DecryptionClosureRuntimeTests` (13); new architecture rules `OnlyTheCreationFingerprintCheckOpensStoredFingerprintKeys` and `TheRawSingleValueApiIsReachedOnlyThroughTheFieldValueWrappers`. Closure mutations: `mutations/closure-results.json`. Authority, integrity and real-Vault suites were not rerun (no change to their contracts in the closure).
