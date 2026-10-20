#
# Which pull requests run each self-check step of .github/workflows/test.yml (control-server#803).
#
# Read by Select-TestSelfCheckRun.ps1, the step "Decide which self-checks this pull request runs" in test.yml, and checked
# by Test-TestSelfCheckScope.ps1. Every step of test.yml is in Steps or in NotSelfChecks; a step added to test.yml and to
# neither turns that self-check red.
#
# A step runs when a pull request changes any of its inputs, which are:
#   - the script the step runs (Script);
#   - every file that script reaches by name, worked out by the selector from the checkout, not listed here: each string in
#     the code of a .ps1/.psm1/.psd1 it reaches, read through the PowerShell parser, is cut into file-name-like tokens; a
#     token that is the leaf name of a file under scripts/ brings that file in, and a token that is a repository path
#     (src/..., .github/..., vendor/..., from the script's own directory or with leading ./ and ../ dropped) brings in
#     that file, or everything under that directory when the string is a path and not a sentence -- transitively, until
#     nothing new is named. NameClosure = $false turns this off for a self-check whose strings name paths as test data;
#     its Inputs then say what it reads;
#   - Inputs below: what a name cannot show. A directory the script lists (Get-ChildItem), a file whose name it builds from
#     a variable ("$scenario.ps1"), a project it builds. A prefix ends in '/'; a pattern holds '*'; anything else is a file.
# Matching is case-insensitive, so a differently cased spelling counts rather than slips through.
#
# Never skipped: an event that is not a pull request (a push, a dispatched run), a change list that could not be read, and
# a pull request that changes anything in Always.
#
# Needs: steps whose output this one reads (a build under bin/, a generated file), by Key for a self-check or by name for a
# step this table does not decide. A needed self-check runs whenever the step that needs it runs, and comes before it in
# test.yml; a needed step this table does not decide must never be gated on the scope. A step whose script reads
# src/ControlServer.Host/bin without building it itself must say what it needs. As of control-server#803 nothing reads
# another self-check's output: the two that read a Host build read the Test step's, which no pull request skips.
#
@{
    Always = @(
        '.github/workflows/test.yml'
        '.github/actions/'
        'scripts/TestSelfCheckScope.psd1'
        'scripts/Select-TestSelfCheckRun.ps1'
    )

    # Steps of test.yml that this table does not decide, by name (or by 'uses' for an unnamed one), and why.
    NotSelfChecks = @(
        @{ Step = 'actions/checkout@v7'; Why = 'checkout' }
        @{ Step = 'dotnet --info'; Why = 'diagnostics, a second' }
        @{ Step = 'Decide which self-checks this pull request runs'; Why = 'the selector itself' }
        @{ Step = 'Test'; Why = 'the suite; control-server#803 leaves it alone' }
        @{ Step = 'Decide whether to run the staged content-conflict self-check'; Why = 'that self-check has its own selector (control-server#567), registered below as decided by content-conflict-scope' }
        @{ Step = 'Remove the content-conflict stage root'; Why = 'cleanup of the content-conflict self-check' }
        @{ Step = 'Upload test results'; Why = 'artifact upload' }
        # Review M1 of PR #808: gated on the selector's own output, a selector that broke -- say, one that mangled every
        # changed path -- would skip the one check that says so, and the job would stay green. About 15 seconds.
        @{ Step = 'Self-check scope self-check (which pull requests run which self-check)'; Why = 'checks the selector, so the selector does not decide it'
            RunsAlways = $true }
        @{ Step = 'Build ControlServer.Host for the self-checks that read it (push to fp/v2-impl)'; Why = 'stands in for the Test step''s build on a push to fp/v2-impl, see PushToIntegration' }
    )

    # A push to fp/v2-impl (the coordinator's ruling on the re-review of PR #808): the backstop for a self-check a pull
    # request skipped, so every self-check runs and nothing else of weight does. The Test step's suite already ran on the
    # pull request's own merge commit, and the staged content-conflict check takes about 200 s; both are skipped. What a
    # skipped step's output was needed for is made by its replacement instead: the self-checks that Need the Test step read
    # its Host build, so a Host build runs in its place. Test-TestSelfCheckScope.ps1 holds test.yml to all of this.
    PushToIntegration = @{
        Condition = "github.event_name == 'push' && github.ref == 'refs/heads/fp/v2-impl'"
        Skips = @('Test', 'G3 runner self-check (staged content conflict)')
        Replaces = @{ Test = 'Build ControlServer.Host for the self-checks that read it (push to fp/v2-impl)' }
    }

    # What a build reads besides its project's own files (review S3 of PR #808): an input of every step that reads a build
    # or makes one. A pull request that only bumps Microsoft.Data.Sqlite in Directory.Packages.props changes what such a
    # step loads, and nothing else it names. Test-TestSelfCheckScope.ps1 holds each such step to listing all of these.
    BuildInputs = @('src/', 'vendor/', 'global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
        'NuGet.Config', '.config/')

    # In test.yml's order. Key is the name of the selector's output for the step (letters, digits, underscores).
    Steps = @(
        @{ Key = 'l2_onboard_handle'; Name = 'L2 scenario self-check (onboard handle after restart)'
            Script = 'scripts/l2/Test-L2OnboardHandleAfterRestart.ps1'
            # Lists every scenario and counterexample.
            Inputs = @('scripts/l2/scenarios/', 'scripts/l2/counterexamples/') }
        @{ Key = 'l2_hmi_phrase_watch'; Name = 'L2 scenario self-check (HMI phrase watch)'
            Script = 'scripts/l2/Test-L2HmiPhraseWatch.ps1'; Inputs = @() }
        @{ Key = 'l2_route_evidence'; Name = 'L2 scenario self-check (route evidence recompute)'
            Script = 'scripts/l2/Test-L2RouteEvidence.ps1'; Inputs = @() }
        @{ Key = 'l2_run_ledger'; Name = 'L2 scenario self-check (real-rig run ledger)'
            Script = 'scripts/l2/Test-L2RunLedger.ps1'; Inputs = @() }
        @{ Key = 'l2_probe_closures'; Name = 'L2 scenario self-check (probe closures resolve)'
            Script = 'scripts/l2/Test-L2ProbeClosureResolvable.ps1'
            # Lists scripts/l2/*.psm1.
            Inputs = @('scripts/l2/*.psm1') }
        @{ Key = 'l2_second_leg_intent'; Name = 'L2 scenario self-check (second-leg intent wait)'
            Script = 'scripts/l2/Test-L2SecondLegIntentWait.ps1'; Inputs = @() }
        @{ Key = 'l2_expected_action_overdue'; Name = 'L2 scenario self-check (expected-action overdue)'
            Script = 'scripts/l2/Test-L2ExpectedActionOverdue.ps1'; Inputs = @() }
        @{ Key = 'l2_forced_recovery_confirm'; Name = 'L2 scenario self-check (forced recovery confirm step)'
            Script = 'scripts/l2/Test-G3ForcedRecoveryEntry.ps1'; Inputs = @() }
        @{ Key = 'l2_screen_state'; Name = 'L2 scenario self-check (screen-state snapshot and pauses)'
            Script = 'scripts/l2/Test-L2ScreenState.ps1'
            # Parses "$scenario.ps1" for every scenario its crosscheck plan names.
            Inputs = @('scripts/l2/scenarios/') }
        @{ Key = 'g3_journey_uia_fixture'; Name = 'G3 scenario self-check (journey UIA fixture reads)'
            Script = 'scripts/l2/Test-G3JourneyUiaFixtureReads.ps1'; Inputs = @() }
        @{ Key = 'g3_content_conflict_scope'; Name = 'G3 runner self-check (content-conflict scope)'
            Script = 'scripts/Test-StagedG3ContentConflictScope.ps1'
            # Its strings name paths as test data -- some that exist nowhere, on purpose -- not as reads; what it reads is
            # the selector it checks, which is pure input.
            NameClosure = $false; Inputs = @('scripts/Select-StagedG3ContentConflictRun.ps1') }
        @{ Key = 'l2_scenario_round_scope'; Name = 'L2 self-check (scenario round scope)'
            Script = 'scripts/Test-L2ScenarioRunScope.ps1'
            # Sweeps every tracked file through Select-L2ScenarioRun.ps1, which reads every string in scripts/l2 and the
            # files under scripts/ they name, and checks l2.yml's wiring.
            # Its strings name src/ and docs/ paths as test data, not as reads.
            NameClosure = $false; Inputs = @('scripts/', '.github/workflows/') }
        @{ Key = 'l2_worklist_station_wait'; Name = 'L2 scenario self-check (worklist station wait)'
            Script = 'scripts/l2/Test-L2WorklistStationWait.ps1'; Inputs = @() }
        @{ Key = 'l2_double_command_error'; Name = 'L2 scenario self-check (double command refusal body)'
            Script = 'scripts/l2/Test-L2DoubleCommandError.ps1'; Inputs = @() }
        @{ Key = 'l2_journey_closure_ids'; Name = 'L2 scenario self-check (journey closure ids)'
            Script = 'scripts/l2/Test-L2JourneyClosureIds.ps1'; Inputs = @() }
        @{ Key = 'l2_multi_stop_plan_rows'; Name = 'L2 scenario self-check (multi-stop plan row parsing)'
            Script = 'scripts/l2/Test-L2MultiStopJourney.ps1'; Inputs = @() }
        @{ Key = 'g3_staged_error_path'; Name = 'G3 runner self-check (staged error path)'
            Script = 'scripts/Test-StagedG3ErrorPath.ps1'; Inputs = @() }
        @{ Key = 'g3_evidence_honesty'; Name = 'G3 runner self-check (evidence honesty)'
            Script = 'scripts/Test-G3EvidenceHonesty.ps1'
            # Drives the runners, which take scenarios by variable.
            Inputs = @('scripts/l2/scenarios/') }
        @{ Key = 'g3_self_check_override'; Name = 'G3 runner self-check (self-check override parameters)'
            Script = 'scripts/Test-G3SelfCheckOverride.ps1'; Inputs = @() }
        @{ Key = 'g3_nightly_decisions'; Name = 'G3 runner self-check (nightly G3 decisions)'
            Script = 'scripts/Test-NightlyG3.ps1'
            Inputs = @() }
        @{ Key = 'g3_runner_stage'; Name = 'G3 runner self-check (stage, clone and disk)'
            # control-server#585. Reads the four runners from a table of literal file names, which the name closure reaches.
            Script = 'scripts/Test-G3RunnerStage.ps1'; Inputs = @() }
        @{ Key = 'install_data_root_lock_wait'; Name = 'Install and upgrade self-check (data root lock wait)'
            Script = 'scripts/Test-DataRootLockWait.ps1'; Inputs = @() }
        @{ Key = 'install_two_car_paths'; Name = 'Install and upgrade self-check (two-car safety projection, first readiness)'
            Script = 'scripts/Test-InstallerTwoCarPaths.ps1'; Inputs = @() }
        @{ Key = 'g3_evidence_location'; Name = 'G3 runner self-check (evidence location diagnostic)'
            Script = 'scripts/Test-G3EvidenceLocation.ps1'; Inputs = @() }
        @{ Key = 'l2_closure_capture'; Name = 'L2 scenario self-check (closure variable capture)'
            Script = 'scripts/l2/Test-L2ClosureCapture.ps1'
            # Scans every script under scripts/.
            Inputs = @('scripts/') }
        @{ Key = 'l2_whole_array_return'; Name = 'L2 scenario self-check (whole-array readers not enumerated)'
            Script = 'scripts/l2/Test-L2WholeArrayReturn.ps1'
            # Scans every script under scripts/.
            Inputs = @('scripts/') }
        @{ Key = 'l2_single_row'; Name = 'L2 scenario self-check (single-row read)'
            Script = 'scripts/l2/Test-L2SingleRow.ps1'; Inputs = @() }
        @{ Key = 'l2_backlog_acceptance'; Name = 'L2 scenario self-check (backlog acceptance)'
            Script = 'scripts/l2/Test-L2BacklogAcceptance.ps1'; Inputs = @() }
        @{ Key = 'script_static_checks'; Name = 'Script static checks (parse, helper properties, string-null)'
            Script = 'scripts/Test-ScriptStaticChecks.ps1'
            # Scans every script under scripts/.
            Inputs = @('scripts/') }
        @{ Key = 'g3_runner_claims'; Name = 'G3 runner self-check (runner claims)'
            Script = 'scripts/Test-G3RunnerClaims.ps1'
            # Reads "$scenario.ps1" for every scenario a runner claims, and runner files from a table.
            Inputs = @('scripts/l2/scenarios/', 'scripts/*.ps1') }
        @{ Key = 'l2_dispatch_zone_parameters'; Name = 'L2 scenario self-check (dispatch zone parameters)'
            Script = 'scripts/l2/Test-L2DispatchZoneParameters.ps1'
            Inputs = @() }
        @{ Key = 'l2_lanes'; Name = 'L2 scenario self-check (CI lane plan)'
            Script = 'scripts/l2/Test-L2Lanes.ps1'
            # Lists scripts/l2/*.psm1.
            Inputs = @('scripts/l2/*.psm1') }
        @{ Key = 'l2_real_inbound'; Name = 'L2 scenario self-check (real inbound reads)'
            Script = 'scripts/l2/Test-L2RealInbound.ps1'; Inputs = @() }
        @{ Key = 'l2_session_continuity'; Name = 'L2 scenario self-check (session continuity verdict)'
            Script = 'scripts/l2/Test-L2SessionContinuity.ps1'; Inputs = @() }
        @{ Key = 'l2_slot_groups'; Name = 'L2 scenario self-check (slot groups)'
            Script = 'scripts/l2/Test-L2SlotGroups.ps1'; Inputs = @() }
        @{ Key = 'l2_task_type_stations'; Name = 'L2 scenario self-check (task type stations)'
            Script = 'scripts/l2/Test-L2TaskTypeStations.ps1'; Inputs = @() }
        @{ Key = 'l2_peer_publish_cache'; Name = 'L2 scenario self-check (peer publish cache concurrency)'
            Script = 'scripts/l2/Test-L2PeerPublishCache.ps1'; Inputs = @() }
        @{ Key = 'g3_claim_record_backfill'; Name = 'G3 runner self-check (demand-bearing claim record backfill)'
            Script = 'scripts/Test-DemandBearingClaimRecordBackfill.ps1'
            # -SkipStores: no build, no database; the runner's AST only. The script can build (without -SkipStores), so the
            # rule that a step making a build lists BuildInputs holds here too, without an exception to keep track of.
            Inputs = @('src/', 'vendor/', 'global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
                'NuGet.Config', '.config/') }
        @{ Key = 'scenario_sql_explain'; Name = 'Scenario SQL self-check (literal SQL against the migrated schema)'
            Script = 'scripts/Test-ScenarioSqlExplain.ps1'
            # Scans every script under scripts/, and builds ControlServer.Host to migrate the schema it explains against.
            Inputs = @('scripts/', 'src/', 'vendor/', 'global.json', 'Directory.Build.props', 'Directory.Build.targets',
                'Directory.Packages.props', 'NuGet.Config', '.config/') }
        @{ Key = 'parallel_instance'; Name = 'Parallel instance self-check'
            Script = 'scripts/parallel/Test-ParallelInstance.ps1'
            # Reads the Host's options, presets, project and migrations, and borrows the SQLite provider from its build.
            Inputs = @('scripts/parallel/', 'tools/', 'docs/production-cutover-mode-plan.md',
                # BuildInputs: it loads the Host build's SQLite provider.
                'src/', 'vendor/', 'global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
                'NuGet.Config', '.config/')
            # The provider comes from src/ControlServer.Host/bin, which the Test step's build fills; without it the case is a
            # yellow SKIP, not a red.
            Needs = @('Test') }
        @{ Key = 'g3_journey_v3_branches'; Name = 'G3 scenario self-check (journey v3 script branches)'
            Script = 'scripts/l2/Test-G3JourneyV3ScriptBranches.ps1'
            # BuildInputs: it loads the Host build's SQLite provider.
            Inputs = @('src/', 'vendor/', 'global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
                'NuGet.Config', '.config/')
            # Microsoft.Data.Sqlite from src/ControlServer.Host/bin/Release/net8.0/win-x64, which the Test step's build of
            # the test project (it references the Host; Directory.Build.props sets win-x64) fills. Without it: exit 2,
            # INCOMPLETE -- run 39428641491, where a red step before Test skipped both Test and the Scenario SQL check.
            # The Scenario SQL check builds the Host too, but this does not need it: measured on a clean worktree, a build
            # of the test project alone leaves the DLL there and this passes (evidence/cs803/dependency/).
            Needs = @('Test') }
        # Decided by its own selector, Select-StagedG3ContentConflictRun.ps1 (control-server#567); here so that every
        # self-check step is accounted for.
        @{ Key = 'staged_content_conflict'; Name = 'G3 runner self-check (staged content conflict)'
            Script = 'scripts/Test-StagedG3ContentConflict.ps1'; DecidedBy = 'content-conflict-scope' }
    )
}
