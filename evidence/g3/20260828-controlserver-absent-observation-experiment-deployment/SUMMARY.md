# ControlServer absent-at-observation experimental create gate

Observed: 2026-08-28, Asia/Shanghai

Result: PASS for the authorized ControlServer-only implementation, test, package, push, and local deployment. The experimental gate is installed but remains disabled. JourneyRuntime remained disabled throughout. No Onboard or simulator peer was started, no real RIoT mutation was sent, no order was created, and no vehicle moved.

G3 and RC remain **INCONCLUSIVE**. This local deployment proves the fail-closed implementation and deployment controls; it does not provide a live cross-repository journey or authorize one.

## Authorization and repository scope

The user authorized this concrete change only in `8005-agv-control-server/ControlServer_MVP`, including implementation, tests, commit/push, packaging, and local deployment while JourneyRuntime stayed disabled. Real RIoT mutation, order creation, vehicle movement, and writes to protected repositories were excluded.

No protected repository was modified. The protocol repository was read only to verify the approved release identity.

## Exact identities

- Product commit: `ControlServer_MVP@9056d3d4c5b96281069023fefb531aae13f5e7a9`
- Integration branch: `codex/riot-sdk-integration@9056d3d4c5b96281069023fefb531aae13f5e7a9`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- Protocol manifest SHA-256: `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- Package manifest SHA-256: `b872d08bfd7c26c6e7c387994084582765df10b77087c9018f65d08949b91988`

Both remote ControlServer branches were read back after push and pointed to the exact product commit.

## Implemented safety boundary

The host adds a default-off `RiotAbsentAtObservationCreateExperiment` configuration section. Enabling it requires one complete version-1 authorization with an exact upper-id, demand, movement leg, lifecycle generation, dispatch generation, and future expiry. Disabled-but-populated configuration still returns no authorization, and invalid enabled configuration fails host startup.

The experimental path accepts only an exact typed SDK observation: `Unknown` with a sanitized `RECONCILE / AbsentAtObservation` receipt, no returned order, no HTTP status, no business code, no result, and no failure category. Found, terminal, indeterminate, HTTP 404, legacy, generation-65, prior-audit, prior-attempt, expired, or identity-mismatched states cannot enter the experiment.

Before any SDK Create call, the implementation durably reserves a one-shot authorization, commits PRE as `RESULT_UNKNOWN`, atomically consumes the permit while writing ARM, and commits START. Authorization identity and eligibility basis flow through every audit phase. Expiry is checked both before PRE and before START. Cancellation, exception, post-read absence, process loss, restart, and duplicate callers remain non-retryable and fail closed after START or ARM.

The permit ledger has unique authorization, upper-id, and consumed-attempt constraints. Status, create-attempt count, audit sequence, and authorization reservation are concurrency tokens. Audit sequence increments with the intent update in one transaction. A deterministic duplicate-permit race proved that a losing caller cannot append a late PRE after the winner completes CREATE_RESPONSE.

## Validation

- Locked restore: PASS.
- Format and diff checks: PASS.
- Focused related regression: 160 passed, 0 failed, 0 skipped.
- Final complete Release suite: 218 passed, 0 failed, 0 skipped.
- Non-incremental Release solution build: 0 warnings, 0 errors.
- Fresh SQLite smoke: all 11 migrations applied through `20260828103631_ExperimentalAbsentObservationCreateAuthorization`.
- EF model drift: none.
- High-risk pseudo-mutation audit: 15 behavior-changing mutations killed, 0 survived.
- Independent semantic, persistence/concurrency, and scope/safety reviews: clean after fixes.

Key regression evidence includes:

- `ExactPermitIsPersistedBeforeReadThenUnknownArmAndStartAreDurableBeforeSingleCreate`
- `OnlyExactTypedAbsentReceiptCanUseExperimentalGate`
- `ProcessLossAfterExperimentalPreBeforeArmMarksUnknownAndDisabledRestartNeverCreates`
- `LosingDuplicatePermitCallerCannotAppendLatePreAfterWinnerCompletesCreateResponse`
- `ArmedPermitWithoutStartedEventSurvivesRestartAndNeverCreates`
- `CallerCancellationAfterStartConsumesPermitAndRestartNeverRetries`
- `UpgradeFromDurableCreateAuditPreservesHistoricalUnknownsAndCreatesPermitUniquenessConstraints`
- `InvalidEnabledConfigurationFailsHostStartupThroughValidateOnStart`

Every Create exercised by tests used a fake gateway or local HTTP handler. Tests did not contact the real RIoT system.

All eight G2 slices passed against the exact product and protocol commits: `W2G-IS-00` through `W2G-IS-07`, 116 tests total, 0 failed, 0 skipped.

## Package and local deployment

The exact clean product commit produced a self-contained `win-x64` package. Independent validation confirmed 382 unique declared payload files, with no escaping or duplicate paths, missing files, length/hash mismatches, or undeclared package payloads.

The rollback-capable local updater completed with run id `20260828T110202Z`. Its sanitized result bound the exact source commit and package manifest, service Running, approved protocol identity, authenticated read-only safety state `STOPPED` with zero reasons, JourneyRuntime false, and explicit false values for mutation, order creation, and vehicle movement. Diagnostic lifecycle markers were in the required order with no failure or rollback marker.

After the fixed administrator task restored the approved 10% test threshold, elevated read-only verification confirmed:

- 381 non-configuration payload files matched byte-for-byte;
- the only package-file deviation was the approved base threshold change from 30 to 10;
- the installed file set was exact, with no missing, unexpected, or hash-mismatched files;
- both JourneyRuntime layers were false;
- the experimental create gate was false.

## Final safety state

- Windows service: Running / Automatic / LocalSystem.
- Ports 58005 and 58007: loopback-only and owned only by the service.
- `/health/live`: `live`; all nine protocol version fields matched the package.
- JourneyRuntime: false in both configuration layers.
- Experimental create gate: false.
- Test battery threshold: 10%.
- Stop marker: present; authorized journey orchestrator: Ready, not running.
- Onboard processes: 0; simulator processes: 0; temporary peer listeners on 1502/58006: 0.
- Real RIoT mutation: false; order created: false; vehicle moved: false.

Raw credentials, configuration identities, vehicle identity, response bodies, backup locations, unrestricted logs, and local artifact paths are intentionally excluded. Sanitized machine-readable facts are in [result.json](result.json).
