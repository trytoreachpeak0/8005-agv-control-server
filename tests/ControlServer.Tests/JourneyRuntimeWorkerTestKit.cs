using System.Data.Common;
using System.Text.Json.Nodes;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// The fixture and helpers the JourneyRuntimeWorker*Tests classes share. They were members of one partial
/// class, which xunit runs as one serial collection: 172 tests, the last minute and a half of every
/// test run (control-server#130). Moved here so the tests could be split into classes xunit runs in
/// parallel; nothing was changed but their accessibility.
/// </summary>
internal static class JourneyRuntimeWorkerTestKit
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 26, 1, 0, 0, TimeSpan.Zero);
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal static string? SnapshotRevisionProperty(string messageType) => messageType switch
    {
        "VehicleBusinessStateSnapshot" => "vehicleBusinessStateRevision",
        "CurrentStopWorklistSnapshot" => "worklistRevision",
        "UpcomingStopPlanSnapshot" => "planRevision",
        _ => null
    };

    internal static GovernedConfigurationPublisher CreateGovernedPublisher(ControlServerDbContext context)
    {
        GovernanceStore governance = new(
            context,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            AuditRetentionPolicy.Default);
        return new GovernedConfigurationPublisher(governance, governance);
    }

    internal sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(
            SqliteConnection connection,
            DbContextOptions<ControlServerDbContext> dbOptions,
            ControlServerDbContext context,
            RecordingCatalog catalog,
            RecordingBoxCounts boxCounts,
            RecordingRiot riot,
            RecordingPeer peer,
            RecordingRouteCostProbe routeCosts,
            bool catalogApproved,
            JourneyRuntimeOptions options,
            FixedTimeProvider clock,
            SaveChangesCounter saveChanges)
        {
            CatalogApproved = catalogApproved;
            Connection = connection;
            DbOptions = dbOptions;
            Context = context;
            Catalog = catalog;
            BoxCounts = boxCounts;
            Riot = riot;
            Peer = peer;
            RouteCosts = routeCosts;
            Options = options;
            Clock = clock;
            SaveChanges = saveChanges;
            Engine = CreateEngine();
        }

        private SqliteConnection Connection { get; }

        /// <summary>
        /// The same options the engine's context was built from, so a test can open a second
        /// context on this connection and write the way a different scope does in the host.
        /// </summary>
        private DbContextOptions<ControlServerDbContext> DbOptions { get; }

        /// <summary>The same options, for a test that writes configuration from a scope of its own.</summary>
        public DbContextOptions<ControlServerDbContext> DbOptionsForTests => DbOptions;

        public ControlServerDbContext Context { get; }
        public RecordingCatalog Catalog { get; }
        public RecordingBoxCounts BoxCounts { get; }
        public RecordingRiot Riot { get; }
        public RecordingPeer Peer { get; }
        public RecordingRouteCostProbe RouteCosts { get; }

        /// <summary>Whether the REQ-0302 pair is configured. False is the uncommissioned server.</summary>
        public bool CatalogApproved { get; }

        public JourneyRuntimeOptions Options { get; }

        /// <summary>
        /// Owned by the fixture rather than by the engine so that it survives
        /// <see cref="RecreateEngineAsync"/>, the way the process-wide singleton survives a scope.
        /// </summary>
        public CheckpointWaitLedger CheckpointWaits { get; } = new();

        public FixedTimeProvider Clock { get; }
        public SaveChangesCounter SaveChanges { get; }

        /// <summary>What the engine logged, so a test can pin what it does not log.</summary>
        public RecordingLogger<JourneyRuntimeEngine> EngineLog { get; } = new();

        /// <summary>What the slot capacity criterion logged; the one criterion that logs.</summary>
        public RecordingLogger<SlotCapacityCriterion> SlotCapacityLog { get; } = new();

        /// <summary>What the round-end structural dispatch block summary logged.</summary>
        public RecordingLogger<StructuralDispatchBlockSink> StructuralBlockLog { get; } = new();

        /// <summary>What the round-end starvation escalation logged (control-server#214).</summary>
        public RecordingLogger<StarvationEscalationSink> StarvationLog { get; } = new();

        public JourneyRuntimeEngine Engine { get; private set; }

        /// <summary>
        /// Whether RIoT reports this vehicle's emergency stop as latched (<c>CAN_RECOVER</c>). Off by default, so
        /// every existing test keeps reading <c>OK</c>. A test flips it after a trigger has gone out, which is what
        /// lets the next evaluation settle that trigger as confirmed -- and only an evaluation that runs can.
        /// </summary>
        public bool EmergencyLatched { get; set; }

        /// <summary>
        /// The <paramref name="commands"/> interceptor is the seam for asserting on the SQL the engine
        /// sends, which is the only way to tell a query that narrows in the store from one that reads a
        /// whole type back and filters in memory: a pre-filter that changed results would be a bug, so
        /// nothing observable distinguishes the two.
        /// </summary>
        public static async Task<RuntimeFixture> CreateAsync(
            bool catalogApproved = true,
            bool bindSlotModel = true,
            DbCommandInterceptor? commands = null)
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            SaveChangesCounter saveChanges = new();
            DbContextOptionsBuilder<ControlServerDbContext> builder =
                new DbContextOptionsBuilder<ControlServerDbContext>()
                    .UseSqlite(connection)
                    .AddInterceptors(saveChanges);
            if (commands is not null)
            {
                builder.AddInterceptors(commands);
            }

            DbContextOptions<ControlServerDbContext> dbOptions = builder.Options;
            ControlServerDbContext context = new(dbOptions);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            JourneyRuntimeOptions options = ValidOptions();
            FixedTimeProvider clock = new(Now);
            RecordingCatalog catalog = new();
            RecordingBoxCounts boxCounts = new();
            RecordingRiot riot = new(options, clock);
            RecordingPeer peer = new();
            RecordingRouteCostProbe routeCosts = new();
            RuntimeFixture fixture = new(
                connection, dbOptions, context, catalog, boxCounts, riot, peer, routeCosts,
                catalogApproved, options, clock, saveChanges);
            if (bindSlotModel)
            {
                await fixture.BindApprovedSlotModelAsync();
            }

            await TaskTypeStationRuntimeSeed.ActivateAsync(dbOptions, Now);
            await fixture.SeedRecoveredPeerAsync();
            await fixture.ImportAreaAssignmentsAsync(
                [.. DefaultAssignedAreas.Select(area => new AreaAssignment(area, options.DispatchZone, "FRONT"))]);
            saveChanges.Reset();
            return fixture;
        }

        /// <summary>
        /// The N-prefixed AREAs these tests use, all in the configured zone. Since control-server#72 the table is
        /// the whole execution whitelist, so a server with none dispatches nothing; D11-10 and Q18-10 stay
        /// unmapped because they are the tests' out-of-scope AREAs.
        /// </summary>
        public static readonly string[] DefaultAssignedAreas = ["N1-1", "N1-2", "N1-3", "N22-1"];

        /// <summary>Imports a new version of the area assignment table the way FieldOps does.</summary>
        public async Task<AreaAssignmentTableVersion> ImportAreaAssignmentsAsync(IReadOnlyList<AreaAssignment> assignments)
        {
            await using ControlServerDbContext importer = new(DbOptions);
            GovernanceStore governance = new(
                importer,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            return await new AreaAssignmentStore(importer, new GovernedConfigurationPublisher(governance, governance))
                .WriteVersionAsync(assignments, Clock.GetUtcNow(), TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Imports a new version of the per-zone dispatch parameters the way FieldOps does (control-server#216): each zone
        /// with its anti-starvation threshold in seconds, null for "not configured".
        /// </summary>
        public async Task<DispatchZoneParameterTableVersion> ImportStarvationThresholdsAsync(
            params (string Zone, long? ThresholdSeconds)[] zones)
        {
            await using ControlServerDbContext importer = new(DbOptions);
            GovernanceStore governance = new(
                importer,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            return await new DispatchZoneParameterStore(importer, new GovernedConfigurationPublisher(governance, governance))
                .WriteVersionAsync(
                    [.. zones.Select(zone => new DispatchZoneParameters(zone.Zone, null, zone.ThresholdSeconds))],
                    Clock.GetUtcNow(),
                    TestContext.Current.CancellationToken);
        }

        public AcceptedDemandSnapshot Demand(
            string demandId,
            string sublot,
            DateTimeOffset createdAt,
            string area = "N1-1",
            string eqp = "EQP-01",
            string package = "PDFN5×6-8L(12R)") => new(
            demandId,
            $"{sublot}|WIRE_TO_GATE",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Clock.GetUtcNow(),
            $"SERIES-{demandId}",
            "WIRE_TO_GATE",
            sublot,
            1,
            createdAt,
            createdAt.AddMinutes(1),
            $"TRACE-{demandId}",
            $"COMMIT-{demandId}",
            new LiveMesFieldSet(area, eqp, "STEP-01", createdAt, package));

        public async Task RecreateEngineAsync()
        {
            Context.ChangeTracker.Clear();
            Engine = CreateEngine();
            await Task.CompletedTask;
        }

        /// <summary>
        /// Enrols the approved eight-slot model (slots 1-4 FRONT, 5-8 REAR) and publishes the vehicle's IO binding
        /// to it, the way a commissioned site starts. Since control-server#73 target slots are chosen inside the
        /// demand's slot group from the vehicle's model, and a vehicle with no model is not dispatched.
        /// </summary>
        public async Task BindApprovedSlotModelAsync()
        {
            await using ControlServerDbContext enroller = new(DbOptions);
            GovernanceStore governance = new(
                enroller,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            SlotConfigurationAuthorityStore authority = new(
                enroller, new GovernedConfigurationPublisher(governance, governance));
            SlotModelVersionRow model = await authority.EnsureApprovedHardwareFactsAsync(
                Clock.GetUtcNow(), TestContext.Current.CancellationToken);
            await authority.PublishIoBindingsAsync(
                Options.AgvId,
                model.SlotModelVersionId,
                ApprovedSlotHardwareFacts.IoBindings,
                Clock.GetUtcNow(),
                TestContext.Current.CancellationToken);
        }

        public async Task AdvanceSessionAsync(long generation)
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.SessionGeneration = generation;
            session.CapabilityRevision = 1;
            session.SafetyRevision = 7;
            session.Readiness = SessionReadiness.Ready;
            session.ReasonCode = "READY";
            session.UpdatedAt = Clock.GetUtcNow();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await AddCapabilityAndSafetyAsync(generation);
        }

        /// <summary>
        /// Readiness returns on the session the vehicle already holds: the gate reopens without a
        /// reconnect, which is what the end of a forced recovery reconciliation looks like.
        /// </summary>
        public async Task RestoreSessionReadyAsync()
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.Readiness = SessionReadiness.Ready;
            session.ReasonCode = "READY";
            session.UpdatedAt = Clock.GetUtcNow();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// The peer is heard from again on the session it already holds -- a heartbeat, no reconnect.
        /// The runtime measures liveness from the last inbound of the current generation, and every
        /// other inbox helper here stamps the fixture's fixed start time, which is stale as soon as a
        /// test advances its clock.
        /// </summary>
        public async Task HearFromPeerAsync()
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            await AddRawInboxAsync(
                "Heartbeat",
                new { observedAt = Clock.GetUtcNow() },
                session.SessionGeneration,
                Clock.GetUtcNow());
        }

        /// <summary>Carries the journey to its trusted pickup arrival, where it waits for a sublot.</summary>
        public async Task<JourneyRuntimeRow> AdvanceToSublotWaitAsync()
        {
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            JourneyRuntimeRow pickupRuntime = await RuntimeAsync();
            Riot.SetSuccessfulArrival("TO_PICKUP", pickupRuntime.PickupStationRiotId);
            Riot.Vehicle = Riot.Vehicle with { CurrentStationId = pickupRuntime.PickupStationRiotId };
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        /// <summary>
        /// Records the safety evidence a real peer's snapshot carries once every slot door is shut:
        /// nothing unknown and no reason codes. The seeded session leaves both unset, which is what an
        /// unread safety picture looks like, and the station timeout refuses to close a stop on that.
        /// </summary>
        public async Task ProveSlotDoorsClosedAsync() =>
            await SetSafetyEvidenceAsync(unknownPresent: false, reasonCodes: []);

        public async Task SetSafetyEvidenceAsync(bool? unknownPresent, string[]? reasonCodes)
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.SafetyUnknownPresent = unknownPresent;
            session.SafetyReasonCodesJson = reasonCodes is null ? null : JsonSerializer.Serialize(reasonCodes);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Records the operator's sublot entry for the current journey, as the peer sends it.</summary>
        public async Task SubmitSublotAsync(string sublot)
        {
            JourneyRuntimeRow runtime = await RuntimeAsync();
            await AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "SublotSubmitted",
                new
                {
                    operationSessionId = runtime.OperationSessionId,
                    stationId = runtime.PickupStationId,
                    worklistRevision = runtime.WorklistRevision,
                    sublot,
                    entryMethod = "SCANNER",
                    @operator = new
                    {
                        operatorId = "OP-001",
                        verificationMethod = "BADGE",
                        verifiedAt = Clock.GetUtcNow()
                    }
                });
        }

        /// <summary>
        /// The peer comes back under a new session generation, through the entry its SessionHello
        /// reaches. The session is left mid-handshake; <see cref="AdvanceSessionAsync"/> completes it.
        /// </summary>
        public async Task ReconnectAsync(long generation) =>
            await new WireToGateStore(Context).BeginSessionRecoveryAsync(
                new SessionIdentity(
                    Options.AgvId,
                    generation,
                    ProtocolCandidateIdentity.RepositoryCommit,
                    ProtocolCandidateIdentity.ManifestSha256,
                    ProtocolCandidateIdentity.ProfileId,
                    ProtocolCandidateIdentity.ProtocolVersion),
                TestContext.Current.CancellationToken);

        /// <summary>Carries the journey to the point where the load command is outstanding.</summary>
        public async Task<JourneyRuntimeRow> AdvanceToLoadResultAsync()
        {
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            JourneyRuntimeRow pickupRuntime = await RuntimeAsync();
            Riot.SetSuccessfulArrival("TO_PICKUP", pickupRuntime.PickupStationRiotId);
            Riot.Vehicle = Riot.Vehicle with { CurrentStationId = pickupRuntime.PickupStationRiotId };
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            JourneyRuntimeRow runtime = await RuntimeAsync();
            await AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "SublotSubmitted",
                new
                {
                    operationSessionId = runtime.OperationSessionId,
                    stationId = runtime.PickupStationId,
                    worklistRevision = runtime.WorklistRevision,
                    sublot = "SUBLOT-001",
                    entryMethod = "SCANNER",
                    @operator = new
                    {
                        operatorId = "OP-001",
                        verificationMethod = "BADGE",
                        verifiedAt = Clock.GetUtcNow()
                    }
                });
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        public async Task<JourneyRuntimeRow> AdvanceToDepartureSafetyAsync()
        {
            await AdvanceToLoadResultAsync();
            StationOperationRow load = await OperationAsync(SlotOperationType.Load);
            await ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        /// <summary>Carries the journey through a safe departure check onto its way to the gate.</summary>
        public async Task<JourneyRuntimeRow> AdvanceToGateArrivalAsync()
        {
            JourneyRuntimeRow runtime = await AdvanceToDepartureSafetyAsync();
            await AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "PreDepartureSafetyCheckResult",
                new
                {
                    preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                    outcome = "SAFE",
                    observedAt = Clock.GetUtcNow(),
                    safetyStateVersion = 7,
                    validUntil = Clock.GetUtcNow().AddMinutes(1),
                    safety = new
                    {
                        departureSafe = true,
                        vehicleStopped = true,
                        allTargetSlotsLocked = true,
                        allUnlockOutputsReset = true,
                        unknownPresent = false,
                        reasonCodes = Array.Empty<string>()
                    }
                },
                runtime.PreDepartureSafetyCheckMessageId);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        /// <summary>Carries the journey on to the gate, where the unload command is issued.</summary>
        public async Task<JourneyRuntimeRow> RunToGateUnloadAsync()
        {
            await AdvanceToGateArrivalAsync();
            Riot.SetSuccessfulArrival("TO_GATE", TaskTypeStationRuntimeSeed.GateStationRiotId);
            Riot.Vehicle = Riot.Vehicle with { CurrentStationId = TaskTypeStationRuntimeSeed.GateStationRiotId };
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        /// <summary>Carries the journey through the unload result to atomic completion.</summary>
        public async Task<JourneyRuntimeRow> RunToCompletionAsync()
        {
            await RunToGateUnloadAsync();
            StationOperationRow unload = await OperationAsync(SlotOperationType.Unload);
            await ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
            await Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
            return await RuntimeAsync();
        }

        /// <summary>
        /// Stores an inbound envelope whose <c>sessionGeneration</c> is null, which is what the
        /// inbox holds before a generation is assigned. Liveness scans every message type, so it
        /// must skip these rather than fail on the missing number.
        /// </summary>
        public async Task AddInboxRowWithoutSessionGenerationAsync()
        {
            string messageId = Guid.NewGuid().ToString("D");
            Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = "SessionHello",
                RequestJson = JsonSerializer.Serialize(new
                {
                    messageType = "SessionHello",
                    messageId,
                    agvId = Options.AgvId,
                    sessionGeneration = (long?)null,
                    sentAt = Now,
                    payload = new { }
                }, SerializerOptions),
                ContentHash = new string('b', 64),
                FirstResponseJson = "{}",
                ReceivedAt = Clock.GetUtcNow()
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Simulates a peer that has gone quiet: the session row still says Ready and its
        /// snapshots are still on file, but nothing has been received from it for an hour.
        /// </summary>
        public async Task SilenceOnboardSessionAsync()
        {
            ProtocolInboxRow[] rows = await Context.ProtocolInbox
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in rows)
            {
                row.ReceivedAt = Clock.GetUtcNow().AddHours(-1);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Backdates only the snapshot payload timestamps, leaving the session live. Onboard sends
        /// each snapshot once per session, so this is the normal steady state, not a hazard.
        /// </summary>
        public async Task AgeOnboardSnapshotPayloadsAsync()
        {
            ProtocolInboxRow[] rows = await Context.ProtocolInbox
                .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in rows)
            {
                JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
                root["payload"]!["observedAt"] = Clock.GetUtcNow().AddHours(-1);
                row.RequestJson = root.ToJsonString(SerializerOptions);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Sets the undefined <c>supportsBatchUnlock</c> capability flag to false, which is the
        /// value Onboard ships and the value the protocol's own canonical example carries.
        /// </summary>
        public async Task ClearSupportsBatchUnlockAsync()
        {
            ProtocolInboxRow[] rows = await Context.ProtocolInbox
                .Where(row => row.MessageType == "CapabilitySnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in rows)
            {
                JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
                root["payload"]!["supportsBatchUnlock"] = false;
                row.RequestJson = root.ToJsonString(SerializerOptions);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task KeepOnlyOneAvailableSlotAsync()
        {
            ProtocolInboxRow[] rows = await Context.ProtocolInbox
                .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in rows)
            {
                JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
                JsonArray slots = root["payload"]!["slotStates"]!.AsArray();
                for (int index = 1; index < slots.Count; index++)
                {
                    slots[index]!["administrativeAvailability"] = "DISABLED";
                }
                row.RequestJson = root.ToJsonString(SerializerOptions);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Rewrites the session's SafetyStateSnapshot into what Onboard sends when the session is
        /// established while the vehicle is still moving. Slot states are untouched: movement
        /// changes neither their physical state nor their locks.
        /// </summary>
        public async Task EstablishSessionWhileVehicleIsMovingAsync()
        {
            ProtocolInboxRow row = await Context.ProtocolInbox.SingleAsync(
                item => item.MessageType == "SafetyStateSnapshot",
                TestContext.Current.CancellationToken);
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            root["payload"]!["safety"]!["departureSafe"] = false;
            root["payload"]!["safety"]!["vehicleStopped"] = false;
            row.RequestJson = root.ToJsonString(SerializerOptions);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Records a SafetyStateChanged the way OnboardMessageProcessor does: the envelope lands
        /// in the inbox and the session row takes the new revision and departure flag. Onboard
        /// sends the full snapshot once per session and reports every later change this way.
        /// </summary>
        /// <summary>
        /// Applies a SafetyStateChanged the way the host does: on the transport's own scope, which
        /// is a different <see cref="ControlServerDbContext"/> from the engine's. The ordinary
        /// <see cref="AddSafetyStateChangedAsync"/> writes through the engine's own context, so the
        /// engine sees the new revision on its tracked session row whatever it does -- which hides
        /// every question about when the engine actually re-reads that row.
        /// </summary>
        public async Task ApplySafetyStateChangedOnAnotherScopeAsync(
            long safetyStateVersion,
            bool departureSafe,
            bool vehicleStopped)
        {
            await using ControlServerDbContext scope = new(DbOptions);
            string messageId = Guid.NewGuid().ToString("D");
            string json = JsonSerializer.Serialize(new
            {
                messageType = "SafetyStateChanged",
                messageId,
                agvId = Options.AgvId,
                sessionGeneration = 1L,
                sentAt = Now,
                payload = new
                {
                    safetyStateVersion,
                    observedAt = Clock.GetUtcNow(),
                    safety = new
                    {
                        departureSafe,
                        vehicleStopped,
                        allTargetSlotsLocked = true,
                        allUnlockOutputsReset = true,
                        unknownPresent = false,
                        reasonCodes = Array.Empty<string>()
                    },
                    affectedSlots = Array.Empty<int>()
                }
            }, SerializerOptions);
            scope.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = "SafetyStateChanged",
                RequestJson = json,
                ContentHash = new string('f', 64),
                FirstResponseJson = "{}",
                ReceivedAt = Clock.GetUtcNow()
            });
            SessionRecoveryRow session = await scope.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.SafetyRevision = safetyStateVersion;
            session.DepartureSafe = departureSafe;
            // One SaveChanges, as ADR-cross-0033 requires: the envelope and the revision it advances
            // land together, so no reader can see one without the other.
            await scope.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Onboard's safety summary moves on: a SafetyStateChanged at the session's next safetyStateVersion carrying exactly
        /// these five facts, the ones dispatch admission reads (<c>VehicleDynamicFactsCriterion</c>). The session stays Ready;
        /// what changes is only what it vouches for.
        /// </summary>
        public async Task SetDepartureSummaryAsync(
            bool departureSafe = true,
            bool vehicleStopped = true,
            bool allTargetSlotsLocked = true,
            bool allUnlockOutputsReset = true,
            bool unknownPresent = false)
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(TestContext.Current.CancellationToken);
            long next = (session.SafetyRevision ?? 0) + 1;
            await AddRawInboxAsync("SafetyStateChanged", new
            {
                safetyStateVersion = next,
                observedAt = Clock.GetUtcNow(),
                safety = new
                {
                    departureSafe,
                    vehicleStopped,
                    allTargetSlotsLocked,
                    allUnlockOutputsReset,
                    unknownPresent,
                    reasonCodes = Array.Empty<string>()
                },
                affectedSlots = Array.Empty<int>()
            }, session.SessionGeneration, Clock.GetUtcNow());
            session.SafetyRevision = next;
            session.DepartureSafe = departureSafe;
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            Context.ChangeTracker.Clear();
        }

        public async Task AddSafetyStateChangedAsync(
            long safetyStateVersion,
            bool departureSafe,
            bool vehicleStopped,
            int[]? affectedSlots = null)
        {
            await AddRawInboxAsync("SafetyStateChanged", new
            {
                safetyStateVersion,
                observedAt = Clock.GetUtcNow(),
                safety = new
                {
                    departureSafe,
                    vehicleStopped,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                },
                affectedSlots = affectedSlots ?? []
            }, 1);
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.SafetyRevision = safetyStateVersion;
            session.DepartureSafe = departureSafe;
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Records the SafetyStateSnapshot Onboard sends mid-session when the server asks for readings
        /// (control-server#142): the next safetyStateVersion, the live summary, and every slot as the IO
        /// reads it now -- here all of them occupied and unlocked, the way a load in progress reads.
        /// </summary>
        /// <param name="receivedBeforeBaseline">
        /// Stamps the answer a second before the baseline by the server's receive clock, as a clock stepped back
        /// between the two would. Which snapshot is the baseline is a matter of safetyStateVersion, not of when the
        /// server happened to write it down.
        /// </param>
        public async Task AddMidSessionSafetySnapshotAsync(long safetyStateVersion, bool receivedBeforeBaseline = false)
        {
            // By default the baseline arrived a moment earlier, so the two rows differ in receive time as well as
            // version. The later one is received now, not after now: a receive time in the future does not count
            // as liveness.
            ProtocolInboxRow[] baseline = await Context.ProtocolInbox
                .Where(row => row.MessageType == "SafetyStateSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken);
            foreach (ProtocolInboxRow row in baseline)
            {
                row.ReceivedAt = receivedBeforeBaseline ? Now : Now.AddSeconds(-1);
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await AddRawInboxAsync("SafetyStateSnapshot", new
            {
                safetyStateVersion,
                observedAt = Clock.GetUtcNow(),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                },
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = "OCCUPIED",
                    lockState = "UNLOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                })
            }, 1, receivedBeforeBaseline ? Now.AddSeconds(-1) : Now);
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.SafetyRevision = safetyStateVersion;
            session.DepartureSafe = true;
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Records the SafetyStateSnapshot Onboard sends answering SafetyStateSnapshotRequested after a fault with cargo on board
        /// was cleared (control-server#318, REQ-0362): the slots the committed loads targeted read as given, every other slot
        /// empty, locked and reset. The session row is left as it is -- this snapshot is evidence about the cargo, not the
        /// session's safety summary. Returns the cargo slots, and fails when there are none: a snapshot about no slots proves
        /// nothing either way.
        /// </summary>
        /// <param name="cargoSlot">
        /// Per cargo slot, by its position among the cargo slots: the three states it reads as, or null to leave it out of the
        /// snapshot. Overrides the three single states when given.
        /// </param>
        /// <param name="agvId">The vehicle the snapshot is from; this fixture's own when not given.</param>
        public async Task<int[]> AddCargoSnapshotAsync(
            DateTimeOffset receivedAt,
            string physicalState = "OCCUPIED",
            string lockState = "LOCKED",
            string unlockOutputState = "RESET",
            bool unknownPresent = false,
            Func<int, (string Physical, string Lock, string Output)?>? cargoSlot = null,
            string? agvId = null)
        {
            StationOperationRow[] loads = await Context.StationOperations.AsNoTracking()
                .Where(row => row.OperationType == SlotOperationType.Load && row.Status == StationOperationStatus.Committed)
                .ToArrayAsync(TestContext.Current.CancellationToken);
            int[] cargo = [.. loads.SelectMany(row => JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson)!).Distinct().Order()];
            Assert.NotEmpty(cargo);
            SessionRecoveryRow session = await Context.SessionRecoveries.AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            int earlier = await Context.ProtocolInbox.CountAsync(
                row => row.MessageType == "SafetyStateSnapshot", TestContext.Current.CancellationToken);
            cargoSlot ??= _ => (physicalState, lockState, unlockOutputState);
            (int Slot, (string Physical, string Lock, string Output)? States)[] reported = [.. Enumerable.Range(1, 8)
                .Select(slot => (slot, cargo.Contains(slot) ? cargoSlot(Array.IndexOf(cargo, slot)) : ("EMPTY", "LOCKED", "RESET")))
                .Where(item => item.Item2 is not null)];
            await AddRawInboxAsync("SafetyStateSnapshot", new
            {
                safetyStateVersion = (session.SafetyRevision ?? 0) + 1000 + earlier,
                observedAt = receivedAt,
                safety = new
                {
                    departureSafe = !unknownPresent,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent,
                    reasonCodes = Array.Empty<string>()
                },
                slotStates = reported.Select(item => new
                {
                    slotNo = item.Slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = item.States!.Value.Physical,
                    lockState = item.States!.Value.Lock,
                    unlockOutputState = item.States!.Value.Output,
                    reasonCodes = Array.Empty<string>()
                })
            }, session.SessionGeneration, receivedAt, agvId);
            return cargo;
        }

        public async Task SetOnboardUnknownAsync()
        {
            ProtocolInboxRow row = await Context.ProtocolInbox.SingleAsync(
                item => item.MessageType == "SafetyStateSnapshot",
                TestContext.Current.CancellationToken);
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            root["payload"]!["safety"]!["departureSafe"] = false;
            root["payload"]!["safety"]!["unknownPresent"] = true;
            row.RequestJson = root.ToJsonString(SerializerOptions);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public Task<JourneyRuntimeRow> RuntimeAsync() => Context.JourneyRuntimes
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public Task<JourneyRuntimeRow> RuntimeAsync(string demandId) => Context.JourneyRuntimes
            .AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken);

        public Task<AcceptedDemandRow> DemandRowAsync() => Context.AcceptedDemands
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public Task<JourneyBacklogRow> BacklogAsync(string demandId) => Context.JourneyBacklog
            .AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken);

        public Task<VehicleDispatchLeaseRow> LeaseAsync() => Context.VehicleDispatchLeases
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        public async Task<string> IntentStatusAsync(string purpose) => (await Context.OrderIntents
            .AsNoTracking()
            .SingleAsync(row => row.Purpose == purpose, TestContext.Current.CancellationToken)).Status;

        public Task<StationOperationRow> OperationAsync(SlotOperationType type) => Context.StationOperations
            .AsNoTracking()
            .SingleAsync(row => row.OperationType == type, TestContext.Current.CancellationToken);

        public async Task<string[]> PendingOutboxMessageIdsAsync() => await Context.ProtocolOutbox
            .AsNoTracking()
            .Where(row => row.AcknowledgedAt == null && row.FencedAt == null)
            .Select(row => row.MessageId)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        /// <summary>The payload of one outbound message, as stored and sent.</summary>
        public async Task<JsonElement> OutboxPayloadAsync(string messageId)
        {
            ProtocolOutboxRow row = await Context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(item => item.MessageId == messageId, TestContext.Current.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            return document.RootElement.GetProperty("payload").Clone();
        }

        public async Task<string[]> OutboxTypesAsync() => await Context.ProtocolOutbox
            .AsNoTracking()
            .OrderBy(row => row.MessageType)
            .Select(row => row.MessageType)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        public async Task AddInboxAsync(
            string messageId,
            string messageType,
            object payload,
            string? correlationId = null)
        {
            JourneyRuntimeRow runtime = await RuntimeAsync();
            string json = JsonSerializer.Serialize(new
            {
                protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                profileId = ProtocolCandidateIdentity.ProfileId,
                protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                messageType,
                messageId,
                correlationId,
                agvId = Options.AgvId,
                sessionGeneration = 1,
                sentAt = Now,
                payload
            }, SerializerOptions);
            Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = messageType,
                RequestJson = json,
                ContentHash = new string('a', 64),
                FirstResponseJson = "{}",
                ReceivedAt = Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task ApplySafeResultAsync(
            StationOperationRow operation,
            SlotOperationType type,
            SlotBusinessState state)
        {
            int[] slots = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
            string resultId = Guid.NewGuid().ToString("D");
            await new WireToGateStore(Context).ApplyOperationResultAsync(
                new StationOperationResult(
                    resultId,
                    operation.SlotOperationAttemptId,
                    operation.DemandId,
                    type,
                    "COMPLETED",
                    slots.Select(slot => new SlotPhysicalEvidence(slot, state, true, true)).ToArray(),
                    true,
                    Now,
                    new string(type == SlotOperationType.Load ? 'b' : 'c', 64),
                    new string(type == SlotOperationType.Load ? 'd' : 'e', 64)),
                Options.AgvId,
                0,
                TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// The result Onboard reports when a station operation runs out its own operator timeout:
        /// the slots were never filled and the batch did not complete. Physical side effects are
        /// unproven from here on, which is what sends the operation to RecoveryRequired.
        /// </summary>
        public async Task ApplyTimedOutResultAsync(StationOperationRow operation, SlotOperationType type)
        {
            int[] slots = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
            await new WireToGateStore(Context).ApplyOperationResultAsync(
                new StationOperationResult(
                    Guid.NewGuid().ToString("D"),
                    operation.SlotOperationAttemptId,
                    operation.DemandId,
                    type,
                    "FAILED",
                    slots.Select(slot => new SlotPhysicalEvidence(slot, SlotBusinessState.Empty, true, true)).ToArray(),
                    false,
                    Clock.GetUtcNow(),
                    new string('8', 64),
                    new string('9', 64)),
                Options.AgvId,
                0,
                TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Drops the peer the way powering the vehicle down for a repair does: the session row
        /// leaves Ready and stays behind with the reason it left for.
        /// </summary>
        public async Task DropOnboardSessionAsync()
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.Readiness = SessionReadiness.RecoveryRequired;
            session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
            session.UpdatedAt = Clock.GetUtcNow();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// What DecideReadinessAsync now does to the session on the way through applying a refused
        /// result: the vehicle is told it needs recovery, on the same row the runtime reads to
        /// decide whether it may advance at all. Unlike a dropped session, the peer is still there.
        /// </summary>
        public async Task MarkSessionRecoveryRequiredByOperationAsync()
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(
                TestContext.Current.CancellationToken);
            session.Readiness = SessionReadiness.RecoveryRequired;
            session.ReasonCode = "OPERATION_RECOVERY_REQUIRED";
            session.UpdatedAt = Clock.GetUtcNow();
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// The FP-C13 catalog gate, configured with approved values. Every runtime test faces a
        /// commissioned server, which is what these tests are about; the unapproved case is a gate
        /// test of its own (<see cref="CreateGateTests"/>) rather than a variant of all of them.
        /// </summary>
        private CatalogAvailabilityAccess CreateCatalogAccess() => new(
            new CatalogAvailabilityStore(Context),
            Microsoft.Extensions.Options.Options.Create(CatalogApproved
                ? new MapStationCatalogOptions
                {
                    ApprovedSyncPeriod = TimeSpan.FromSeconds(30),
                    ApprovedMaxUnconfirmed = TimeSpan.FromMinutes(5),
                }
                : new MapStationCatalogOptions()),
            new CatalogAlarmLedger(),
            Clock,
            NullLogger<CatalogAvailabilityAccess>.Instance);

        private JourneyRuntimeEngine CreateEngine()
        {
            WireToGateStore store = new(Context);
            JourneyIntakeCoordinator intake = new(
                new DemandIntakeService(Catalog, store),
                new MovementDispatchService(store, Riot));
            OnboardJourneyPublisher publisher = new(store, Peer, Clock);
            Microsoft.Extensions.Options.IOptions<JourneyRuntimeOptions> options =
                Microsoft.Extensions.Options.Options.Create(Options);
            // One instance for both readers: the dispatch chain resolves the capacity at acceptance and
            // the engine resolves it again after the entry, and the real host shares one scoped store
            // between them the same way.
            PackageCapacityStore packageCapacity = new(Context);
            // One of each, shared by the round and the engine, the way the host's scope shares them.
            VehicleDispatchPolicyAccess dispatchPolicy =
                new(new VehicleDispatchPolicyStore(Context), options, Clock);
            OnboardDispatchFactsReader onboardFacts = new(Context, options, Clock);
            DispatchRoundRunner dispatchRound = new(
                Context,
                Catalog,
                Riot,
                intake,
                new DispatchAdmissionChain(DispatchAdmissionCriteria.Default(
                    options,
                    new MapStationResolver(),
                    packageCapacity,
                    store,
                    new VehicleFaultStore(Context),
                    BoxCounts,
                    SlotCapacityLog,
                    new TransportDemandSuppressionStore(Context),
                    Context,
                    routeGraph: null,
                    catalog: CreateCatalogAccess(),
                    createGate: CreateGate())),
                // 在途链从同一条空闲链派生（control-server#211）：换掉动态事实那一条，加上追加的四道门。
                new InTransitDispatchAdmissionChain(DispatchAdmissionCriteria.InTransit(
                    DispatchAdmissionCriteria.Default(
                        options,
                        new MapStationResolver(),
                        packageCapacity,
                        store,
                        new VehicleFaultStore(Context),
                        BoxCounts,
                        SlotCapacityLog,
                        new TransportDemandSuppressionStore(Context),
                        Context,
                        routeGraph: null,
                        catalog: CreateCatalogAccess(),
                        createGate: CreateGate()),
                    options)),
                new DispatchZoneParameterStore(Context, CreateGovernedPublisher()),
                DispatchCandidateOrdering.Ranker(),
                dispatchPolicy,
                new AreaAssignmentStore(Context, CreateGovernedPublisher()),
                new VehicleSlotPositionReader(Context),
                new DispatchRoundOutcomeSinks(
                    new StructuralDispatchBlockSink(
                        new StructuralDispatchBlockStore(Context),
                        new VehicleSlotPositionReader(Context),
                        new VehicleRoster(options),
                        StructuralBlockLog),
                    new StarvationEscalationSink(Context, StarvationLog)),
                SlotGroupFullness,
                onboardFacts,
                options,
                Clock,
                EngineLog);
            return new JourneyRuntimeEngine(
                Context,
                Riot,
                Riot,
                new MapStationResolver(),
                new BoundFixedTaskStationResolver(TaskTypeStationRuntimeSeed.Access(Context), options),
                TaskTypeStationRuntimeSeed.Access(Context),
                TaskTypeStationRuntimeSeed.CatalogBindingHolds(Context, Clock),
                new MovementDispatchService(store, Riot),
                store,
                publisher,
                BoxCounts,
                packageCapacity,
                CreateCatalogAccess(),
                new CatalogAvailabilityStore(Context),
                CreateGate(),
                new VehicleRoster(options),
                dispatchPolicy,
                Riot,
                CheckpointWaits,
                CreateFaultCoordinator(),
                dispatchRound,
                onboardFacts,
                new DispatchZoneParameterStore(Context, CreateGovernedPublisher()),
                SlotGroupFullness,
                Riot,
                new ForeignRunningOrderSupervisor(
                    Context,
                    Riot,
                    Riot,
                    new RiotOrderCommandAuditStore(Context),
                    new VehicleRoster(options),
                    Clock,
                    ForeignOrderLog),
                options,
                Clock,
                EngineLog);
        }

        /// <summary>What the foreign running order supervisor logged (control-server#330): its alarms are log events.</summary>
        public RecordingLogger<ForeignRunningOrderSupervisor> ForeignOrderLog { get; } = new();

        /// <summary>
        /// 派车轮写、推进段读的那块板（批次7-07）：宿主里是单例，这里一个夹具一块，跨轮次保留。换一块新的再
        /// <see cref="RecreateEngineAsync"/>，就是一次重启——那块板是进程内的，随进程一起没了。
        /// </summary>
        public SlotGroupFullnessBoard SlotGroupFullness { get; set; } = new();

        /// <summary>
        /// The governed-configuration publisher over this fixture's database: what the batch 4 area assignment
        /// store needs to be built at all.
        /// </summary>
        private GovernedConfigurationPublisher CreateGovernedPublisher()
        {
            GovernanceStore governance = new(
                Context,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            return new GovernedConfigurationPublisher(governance, governance);
        }

        /// <summary>
        /// The fault model, reachable but never reached by these tests: the engine consults it
        /// only when RIoT reports a leg's order FAILED, and nothing here produces one. It is built
        /// over the real stores so that a test which ever does produce one fails on the behaviour
        /// rather than on a stub that was never taught to answer.
        /// </summary>
        private VehicleFaultCoordinator CreateFaultCoordinator()
        {
            VehicleFaultStore faults = new(Context);
            RiotOrderCommandAuditStore audit = new(Context);
            Microsoft.Extensions.Options.IOptions<VehicleFaultOptions> faultOptions =
                Microsoft.Extensions.Options.Options.Create(new VehicleFaultOptions());
            SilentCommandGateway gateway = new(Clock, () => EmergencyLatched);
            return new VehicleFaultCoordinator(
                faults,
                gateway,
                Riot,
                Riot,
                audit,
                new RiotOrderCommandService(gateway, audit, Riot, Clock),
                new EmergencyStopSupervisor(
                    gateway,
                    gateway,
                    gateway,
                    audit,
                    faults,
                    Microsoft.Extensions.Options.Options.Create(new RiotCommandOptions()),
                    Clock,
                    NullLogger<EmergencyStopSupervisor>.Instance),
                new VehicleMotionLedger(faultOptions),
                faultOptions,
                Clock,
                NullLogger<VehicleFaultCoordinator>.Instance);
        }

        private PreCreateGate CreateGate() => new(
            RouteCosts,
            new CatalogAvailabilityStore(Context),
            Clock,
            NullLogger<PreCreateGate>.Instance);

        /// <summary>
        /// A command surface that answers but is never asked here. Every method throws nothing and
        /// records nothing on purpose: if one of these tests ever does drive a leg to FAILED, the
        /// assertion that fails should be about the fault model, not about a double that was left
        /// unable to answer.
        /// </summary>
        private sealed class SilentCommandGateway(TimeProvider clock, Func<bool> latched)
            : IRiotOrderCommandGateway, IRiotVehicleEmergencyFacts, IRiotVehicleOrderFacts
        {
            public Task<RiotVehicleOrderObservation> ReadUnfinishedOrdersAsync(
                string deviceKey,
                CancellationToken cancellationToken)
            {
                _ = cancellationToken;
                return Task.FromResult(new RiotVehicleOrderObservation(deviceKey, false, [], clock.GetUtcNow()));
            }

            public Task<RiotCommandCallResult> IssueOrderCommandAsync(
                RiotOrderCommandKind kind,
                string orderId,
                string? reason,
                CancellationToken cancellationToken)
            {
                _ = orderId;
                _ = reason;
                _ = cancellationToken;
                return Task.FromResult(new RiotCommandCallResult(
                    RiotCommandCallDisposition.Accepted,
                    new RiotOrderCallReceipt(
                        RiotCommandTypeNames.For(kind), "SdkAccepted", clock.GetUtcNow())));
            }

            public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
                RiotEmergencyCommandKind kind,
                string deviceKey,
                CancellationToken cancellationToken)
            {
                _ = deviceKey;
                _ = cancellationToken;
                return Task.FromResult(new RiotCommandCallResult(
                    RiotCommandCallDisposition.Accepted,
                    new RiotOrderCallReceipt(
                        RiotCommandTypeNames.For(kind), "SdkAccepted", clock.GetUtcNow())));
            }

            public Task<RiotVehicleEmergencyObservation> ReadEmergencyStateAsync(
                string deviceKey,
                CancellationToken cancellationToken)
            {
                _ = cancellationToken;
                return Task.FromResult(new RiotVehicleEmergencyObservation(
                    deviceKey,
                    latched() ? RiotVehicleEmergencyObservation.CanRecover : RiotVehicleEmergencyObservation.Ok,
                    clock.GetUtcNow()));
            }
        }

        private async Task SeedRecoveredPeerAsync()
        {
            Context.SessionRecoveries.Add(new SessionRecoveryRow
            {
                AgvId = Options.AgvId,
                SessionGeneration = 1,
                ProtocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
                ManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                ProfileId = ProtocolCandidateIdentity.ProfileId,
                ProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                CapabilityRevision = 1,
                CapabilityHash = new string('1', 64),
                SafetyRevision = 7,
                SafetyHash = new string('2', 64),
                DepartureSafe = true,
                RecoveryReportId = Guid.NewGuid().ToString("D"),
                Readiness = SessionReadiness.Ready,
                ReasonCode = "READY",
                UpdatedAt = Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await AddCapabilityAndSafetyAsync(1);
        }

        private async Task AddCapabilityAndSafetyAsync(long generation)
        {
            await AddRawInboxAsync("CapabilitySnapshot", new
            {
                capabilityVersion = 1,
                observedAt = Now,
                slotModelVersion = "SLOT-MODEL-1",
                activeSlotConfigurationVersion = "SLOT-CONFIG-1",
                activeSlotConfigurationFingerprint = new string('0', 64),
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                }),
                supportsBatchUnlock = true,
                onboardJournalFormatVersion = 1
            }, generation);
            await AddRawInboxAsync("SafetyStateSnapshot", new
            {
                safetyStateVersion = 7,
                observedAt = Now,
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                },
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                })
            }, generation);
        }

        /// <summary>
        /// A second DbContext over the same database, which is what a TCP connection gets: OnboardTcpServer
        /// opens one scope -- and so one context -- for as long as a connection lives, while the runtime
        /// worker writes the same journeys and operations from a context of its own on every pass. Built
        /// from <see cref="DbOptions"/>, the same options the engine's context uses, so this second
        /// context carries the interceptors too and the fixture has one way of opening another scope
        /// rather than two that differ in what they record.
        /// </summary>
        public ControlServerDbContext OpenConnectionContext() => new(DbOptions);

        /// <summary>
        /// The operator's 「修正装货」, entering through OnboardMessageProcessor on the connection whose
        /// context is <paramref name="connection"/>, the way every message on a TCP connection does.
        /// Returns the server's answer: LoadCorrectionAuthorization when it is authorized,
        /// LoadCorrectionRejected when it is not.
        /// </summary>
        public async Task<string> RequestLoadCorrectionOnConnectionAsync(
            ControlServerDbContext connection,
            string correctionId,
            string demandId,
            string attemptId,
            int[] slots)
        {
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                connection, new WireToGateStore(connection), Clock, new ConfigurationBuilder().Build());
            OnboardConnectionState state = new()
            {
                AgvId = Options.AgvId,
                SessionGeneration = 1,
                CapabilityRevision = 1,
                SafetyRevision = 7,
                Readiness = SessionReadiness.Ready
            };
            return await processor.ProcessAsync(
                PeerEnvelope(
                    "LoadCorrectionRequested",
                    new
                    {
                        correctionId,
                        demandId,
                        slotOperationAttemptId = attemptId,
                        slots,
                        @operator = new
                        {
                            operatorId = "OP-001",
                            verificationMethod = "BADGE",
                            verifiedAt = Clock.GetUtcNow()
                        }
                    }),
                state,
                TestContext.Current.CancellationToken);
        }

        private string PeerEnvelope(string messageType, object payload) => JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId = Guid.NewGuid().ToString("D"),
            correlationId = (string?)null,
            agvId = Options.AgvId,
            sessionGeneration = 1L,
            sentAt = Clock.GetUtcNow(),
            payload
        }, SerializerOptions);

        private async Task AddRawInboxAsync(
            string messageType,
            object payload,
            long generation,
            DateTimeOffset? receivedAt = null,
            string? agvId = null)
        {
            string messageId = Guid.NewGuid().ToString("D");
            string json = JsonSerializer.Serialize(new
            {
                messageType,
                messageId,
                agvId = agvId ?? Options.AgvId,
                sessionGeneration = generation,
                sentAt = Now,
                payload
            }, SerializerOptions);
            Context.ProtocolInbox.Add(new ProtocolInboxRow
            {
                MessageId = messageId,
                MessageType = messageType,
                RequestJson = json,
                ContentHash = new string('f', 64),
                FirstResponseJson = "{}",
                ReceivedAt = receivedAt ?? Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        private static JourneyRuntimeOptions ValidOptions() => new()
        {
            Enabled = true,
            PollInterval = TimeSpan.FromSeconds(1),
            AgvId = "老厂前线新多仓位1",
            VehicleKey = "BROKERX-0c20ff0600d644869a6a80c186065d85",
            AgvLifecycleGeneration = 1,
            MapId = 25,
            MapIdentity = "MAP-25",
            DispatchZone = "MAP-25-WIRE_TO_GATE",
            DispatchGeneration = 1,
            MinimumBatteryPercent = 40,
            MaximumEvidenceAge = TimeSpan.FromMinutes(2),
            SublotBoxCountPath = "/api/v2/sublot-box-count",
            AllowedWorkTypes = ["WIRE_TO_GATE"],
            AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
            AdmissionPolicyVersion = 1,
            AdmissionPolicyDeploymentId = "TEST-DEPLOYMENT-1",
            // Off, so a journey leaves the pickup in the iteration its load commits, as every test
            // here was written against. The tests about the wait turn it on for themselves.
            StationDepartureWaitTimeout = TimeSpan.Zero
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    internal sealed class SaveChangesCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        /// <summary>
        /// What each save since the last <see cref="Reset"/> wrote, one entry per save, as
        /// <c>RowType.Property</c> for every property it inserted or modified. It is how a test says
        /// that several facts commit together rather than one after another.
        /// </summary>
        public List<string[]> Saves { get; } = [];

        /// <summary>
        /// A fault to inject: a save whose written properties satisfy this throws after being recorded, before anything
        /// reaches the database -- a crash at that save, as far as the database can tell.
        /// </summary>
        public Func<string[], bool>? FailWhen { get; set; }

        public void Reset()
        {
            Count = 0;
            Saves.Clear();
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            Record(eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Record(eventData);
            return ValueTask.FromResult(result);
        }

        private void Record(DbContextEventData eventData)
        {
            Count++;
            if (eventData.Context is null)
            {
                return;
            }
            string[] written = eventData.Context.ChangeTracker.Entries()
                .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
                .SelectMany(entry => entry.Properties
                    .Where(property => entry.State == EntityState.Added || property.IsModified)
                    .Select(property => $"{entry.Metadata.ClrType.Name}.{property.Metadata.Name}"))
                .ToArray();
            Saves.Add(written);
            if (FailWhen?.Invoke(written) == true)
            {
                throw new InvalidOperationException("Injected failure at this save.");
            }
        }
    }

    internal sealed class RecordingCatalog : IMesIngestCatalog
    {
        private AcceptedDemandSnapshot[] _items = [];
        private int _readCount;

        public Action<int>? BeforeRead { get; set; }

        public void Set(params AcceptedDemandSnapshot[] items) => _items = items;

        public Task<DemandCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            BeforeRead?.Invoke(++_readCount);
            string epoch = _items.FirstOrDefault()?.HistoryEpoch ?? "11111111-1111-4111-8111-111111111111";
            return Task.FromResult(new DemandCatalogSnapshot(epoch, 21, _items));
        }

        public Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_items.SingleOrDefault(item => item.DemandId == demandId));
        }
    }

    internal sealed class RecordingBoxCounts : ISublotBoxCountReader
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public Action? BeforeRead { get; set; }

        public void Set(string sublot, int count) => _counts[sublot] = count;
        public void Remove(string sublot) => _counts.Remove(sublot);

        public Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            BeforeRead?.Invoke();
            return Task.FromResult(_counts.TryGetValue(sublot, out int count) ? (int?)count : null);
        }
    }

    /// <summary>
    /// RIoT's RouteCost, answering reachable unless a test says otherwise.
    /// </summary>
    /// <remarks>
    /// Reachable is the default because these tests are about the journey, not about the gate: a
    /// commissioned server whose vehicle can reach its stations is the world they were written
    /// against. What the gate does with the other answers is <see cref="CreateGateTests"/>'s
    /// subject.
    /// </remarks>
    internal sealed class RecordingRouteCostProbe : IRiotRouteCostProbe
    {
        private readonly Dictionary<int, long?> _byStation = [];

        public long DefaultCostMm { get; set; } = 12000;

        public List<(int MapId, int StationId, string VehicleKey)> Calls { get; } = [];

        /// <summary>Answer this station with a specific cost; a negative one means unreachable.</summary>
        public void Set(int stationId, long costMm) => _byStation[stationId] = costMm;

        /// <summary>Make the call itself fail for this station — no answer, not "unreachable".</summary>
        public void FailFor(int stationId) => _byStation[stationId] = null;

        public Task<RiotRouteCost?> ReadRouteCostAsync(
            int mapId,
            int stationId,
            string vehicleKey,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Calls.Add((mapId, stationId, vehicleKey));
            if (_byStation.TryGetValue(stationId, out long? configured))
            {
                return Task.FromResult(configured is null ? null : new RiotRouteCost(configured.Value));
            }

            return Task.FromResult<RiotRouteCost?>(new RiotRouteCost(DefaultCostMm));
        }
    }

    internal sealed class RecordingRiot
        : IRiotMovementGateway, IRiotVehicleFacts, IRiotMapStationCatalog, IVehicleMotionFacts, IRiotVehicleSafetyFacts,
            IRiotOrderListingFacts, IRiotOrderCommandGateway
    {
        private readonly JourneyRuntimeOptions _options;
        private readonly FixedTimeProvider _clock;
        private readonly Dictionary<string, int> _creates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RiotOrderObservation> _orders = new(StringComparer.Ordinal);
        private RiotMapStation[] _mapStations =
        [
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(210, "关卡"),
            new RiotMapStation(300, "等待点")
        ];

        public RecordingRiot(JourneyRuntimeOptions options, FixedTimeProvider clock)
        {
            _options = options;
            _clock = clock;
            Vehicle = new RiotVehicleObservation(
                options.VehicleKey,
                Connected: true,
                Enabled: true,
                ProcState: "IDLE",
                CurrentMap: options.MapIdentity,
                CurrentStationId: 1,
                BatteryPercent: 80,
                BatteryState: "NO_CHARGE",
                Speed: 0,
                ObservedAt: clock.GetUtcNow(),
                LockStatus: 0,
                OrderTaskId: null);
        }

        public RiotVehicleObservation Vehicle { get; set; }

        /// <summary>
        /// What RIoT reports in <c>movementState</c>. Null is the ordinary case for these tests:
        /// they are not about motion, and a null state reads as Unknown exactly as a field RIoT
        /// did not send would.
        /// </summary>
        public string? MovementState { get; set; }

        public Action? BeforeReadVehicle { get; set; }
        public bool LoseNextCreateResponse { get; set; }
        public int TotalCreateCount => _creates.Values.Sum();

        public int CreateCount(string purpose) => _creates.GetValueOrDefault(purpose);

        public void SetMapStations(params RiotMapStation[] stations) => _mapStations = stations;

        /// <summary>When set, the next Map/Station catalog read throws it instead of answering, once.</summary>
        public Exception? FailNextMapRead { get; set; }

        public Task<RiotMapStationCatalogSnapshot> ReadMapStationsAsync(
            int mapId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (FailNextMapRead is { } failure)
            {
                FailNextMapRead = null;
                return Task.FromException<RiotMapStationCatalogSnapshot>(failure);
            }
            return Task.FromResult(new RiotMapStationCatalogSnapshot(
                mapId,
                _clock.GetUtcNow(),
                new string('c', 64),
                _mapStations));
        }

        public void SetSuccessfulArrival(string purpose, int stationId) =>
            SetSuccessfulArrival(purpose, UpperId(purpose), stationId);

        /// <summary>
        /// Takes the upperId from the runtime row, which is the only way to reach a journey whose
        /// demand is not the one <see cref="UpperId"/> hardcodes.
        /// </summary>
        public void SetSuccessfulArrival(string purpose, string upperId, int stationId)
        {
            _orders[upperId] = new RiotOrderObservation(
                upperId,
                RiotOrderObservationKind.Terminal,
                $"ORDER-{purpose}",
                OrderState: 5,
                VehicleKey: _options.VehicleKey,
                MapId: _options.MapId,
                DestinationStationId: stationId);
        }

        /// <summary>Reports the order under <paramref name="upperId"/> as RIoT's terminal FAILED from now on.</summary>
        public void FailOrder(string upperId) =>
            _orders[upperId] = _orders[upperId] with
            {
                Kind = RiotOrderObservationKind.Terminal,
                OrderState = RiotOrderState.Failed,
            };

        /// <summary>Reports the order under <paramref name="upperId"/> as RIoT's terminal CANCELLED from now on (control-server#215).</summary>
        /// <summary>把这张单在 RIoT 上的状态改成 <paramref name="orderState"/>（终态时观测种类随之为 Terminal）。</summary>
        public void SetOrderState(string upperId, int orderState, bool terminal) =>
            _orders[upperId] = _orders[upperId] with
            {
                Kind = terminal ? RiotOrderObservationKind.Terminal : RiotOrderObservationKind.Active,
                OrderState = orderState,
            };

        /// <summary>
        /// The order under <paramref name="upperId"/> reads Unknown from now on -- the shape the gateway returns for a failed
        /// or indeterminate read instead of throwing (control-server#316).
        /// </summary>
        public void MakeOrderUnreadable(string upperId) =>
            _orders[upperId] = _orders[upperId] with { Kind = RiotOrderObservationKind.Unknown };

        public void CancelOrder(string upperId) =>
            _orders[upperId] = _orders[upperId] with
            {
                Kind = RiotOrderObservationKind.Terminal,
                OrderState = RiotOrderState.Cancelled,
            };

        public async Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            BeforeReadVehicle?.Invoke();
            if (ReadVehicleDelay is { } delay)
            {
                await delay(cancellationToken);
            }
            return Vehicle with { VehicleKey = vehicleKey, ObservedAt = _clock.GetUtcNow() };
        }

        /// <summary>
        /// Awaited inside every vehicle read, with the caller's token: a RIoT that answers late, or not at all until the
        /// caller gives up (control-server#273's read budget).
        /// </summary>
        public Func<CancellationToken, Task>? ReadVehicleDelay { get; set; }

        public Task<VehicleMotionSample> SampleMotionAsync(string deviceKey, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new VehicleMotionSample(
                deviceKey,
                HttpRiotMovementGateway.ReadMotion(MovementState, Vehicle.Speed),
                MovementState,
                Vehicle.Speed,
                Vehicle.CurrentMap,
                Vehicle.CurrentStationId,
                _clock.GetUtcNow()));
        }

        /// <summary>
        /// The reason codes RIoT's vehicle safety read reports for this vehicle (control-server#318's second guard reads it).
        /// Empty -- stopped, nothing in the way -- by default; a test names what it wants the vehicle to be in, such as
        /// <c>RIOT_EMERGENCY_NOT_OK</c> or <c>RIOT_VEHICLE_NOT_ONLINE</c>.
        /// </summary>
        public string[] SafetyReasons { get; set; } = [];

        public int SafetyReads { get; private set; }

        public Task<RiotVehicleSafetyObservation> ReadVehicleSafetyAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            SafetyReads++;
            return Task.FromResult(new RiotVehicleSafetyObservation(
                vehicleKey,
                SafetyReasons.Length == 0 ? RiotVehicleMotionState.Stopped : RiotVehicleMotionState.Unknown,
                _clock.GetUtcNow(),
                "L1",
                SafetyReasons));
        }

        /// <summary>
        /// The next read of the order under this upperId throws instead of answering, once: the process stopping before it
        /// asked RIoT anything about that order (the "decided, not created" crash point of control-server#318).
        /// </summary>
        public string? CrashOnNextReconcileOf { get; set; }

        /// <summary>
        /// Called with the upperId after every read of an order has been answered: lets a test change what the next read
        /// says, such as a read that fails right after the one that found the order terminal (control-server#318, review S2).
        /// </summary>
        public Action<string>? AfterReconcile { get; set; }

        /// <summary>
        /// The next create reaches RIoT -- the order exists there from now on -- and then the process stops before the answer
        /// is recorded, once (the "created, not recorded" crash point of control-server#318).
        /// </summary>
        public bool CrashAfterNextCreate { get; set; }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (CrashOnNextReconcileOf is { } crashing && crashing == upperId)
            {
                CrashOnNextReconcileOf = null;
                throw new IOException($"The process stopped before RIoT was asked about {upperId}.");
            }
            RiotOrderObservation answer = _orders.TryGetValue(upperId, out RiotOrderObservation? order)
                ? order
                : new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null);
            AfterReconcile?.Invoke(upperId);
            return Task.FromResult(answer);
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _creates[intent.Purpose] = CreateCount(intent.Purpose) + 1;
            RiotOrderObservation active = new(
                intent.UpperId,
                RiotOrderObservationKind.Active,
                $"ORDER-{intent.Purpose}",
                OrderState: 3,
                VehicleKey: intent.VehicleKey,
                MapId: intent.MapId,
                DestinationStationId: intent.DestinationStationId);
            _orders[intent.UpperId] = active;
            if (CrashAfterNextCreate)
            {
                CrashAfterNextCreate = false;
                throw new IOException($"The process stopped after RIoT created {intent.UpperId} and before the answer was recorded.");
            }
            if (LoseNextCreateResponse)
            {
                LoseNextCreateResponse = false;
                return Task.FromResult(new RiotOrderObservation(
                    intent.UpperId, RiotOrderObservationKind.Unknown, null));
            }
            return Task.FromResult(active);
        }

        // ---- control-server#330: the by-state order listing, the by-orderId read, and the order command endpoint ----

        private readonly List<RiotListedOrder> _otherOrders = [];
        private readonly Dictionary<string, int> _endedOtherOrders = new(StringComparer.Ordinal);

        /// <summary>
        /// Puts an order into RIoT that this server did not create through <see cref="CreateAsync"/>: listed from now on in
        /// state <paramref name="orderState"/>, on <paramref name="executeVehicleKey"/> (null for RIoT's <c>"--"</c>).
        /// </summary>
        public void PlaceOrder(
            string orderId,
            string? upperId,
            int orderState,
            string? executeVehicleKey,
            string? appointVehicleKey = null)
        {
            _otherOrders.RemoveAll(order => order.OrderId == orderId);
            _endedOtherOrders.Remove(orderId);
            _otherOrders.Add(new RiotListedOrder(
                orderId, upperId, orderState, appointVehicleKey ?? executeVehicleKey, executeVehicleKey ?? "--"));
        }

        /// <summary>The placed order <paramref name="orderId"/> leaves the listing in the final state <paramref name="orderState"/>.</summary>
        public void EndPlacedOrder(string orderId, int orderState)
        {
            _otherOrders.RemoveAll(order => order.OrderId == orderId);
            _endedOtherOrders[orderId] = orderState;
        }

        /// <summary>The placed order <paramref name="orderId"/> leaves the listing and its own read answers nothing.</summary>
        public void ForgetPlacedOrder(string orderId)
        {
            _otherOrders.RemoveAll(order => order.OrderId == orderId);
            _endedOtherOrders.Remove(orderId);
        }

        /// <summary>Whether the by-state listing answers completely. False is the page that does not cover every record.</summary>
        public bool ListingComplete { get; set; } = true;

        /// <summary>How many times the by-state listing was read.</summary>
        public int ListingReads { get; private set; }

        /// <summary>
        /// Called with the number of the listing read about to be answered (1 for the first): lets a test change what RIoT
        /// holds between two reads, such as between the read that found an order and the re-read before its cancel.
        /// </summary>
        public Action<int>? BeforeListing { get; set; }

        /// <summary>Every order command that reached this RIoT, in order.</summary>
        public List<(RiotOrderCommandKind Kind, string OrderId, string? Reason)> OrderCommands { get; } = [];

        /// <summary>Whether a cancel moves the order to CANCELLED (BC-ORDER-003). False is a RIoT that accepts and does nothing.</summary>
        public bool CancelTakesEffect { get; set; } = true;

        /// <summary>What the next order command call answers.</summary>
        public RiotCommandCallDisposition OrderCommandDisposition { get; set; } = RiotCommandCallDisposition.Accepted;

        /// <summary>
        /// The next order command reaches RIoT -- and takes whatever effect it takes -- then the process stops before the answer
        /// is recorded, once (the "sent, not recorded" crash point of control-server#330).
        /// </summary>
        public bool CrashAfterNextOrderCommand { get; set; }

        /// <summary>
        /// When set, every listing read throws it: a defect somewhere in the supervision, since the real gateway never throws
        /// for a RIoT failure (control-server#330).
        /// </summary>
        public Exception? ListingThrows { get; set; }

        public Task<RiotUnfinishedOrderListing> ListUnfinishedOrdersAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ListingReads++;
            BeforeListing?.Invoke(ListingReads);
            if (ListingThrows is { } failure)
            {
                return Task.FromException<RiotUnfinishedOrderListing>(failure);
            }
            if (!ListingComplete)
            {
                return Task.FromResult(new RiotUnfinishedOrderListing(false, [], _clock.GetUtcNow()));
            }

            // This server's own orders are listed too, the way RIoT lists them: a live one in its state, on the vehicle it was
            // created for once it is past QUEUEING. So every test that drives a journey also has its own orders go through the
            // ownership check -- one taken for foreign would be cancelled, and would show.
            RiotListedOrder[] own = [.. _orders.Values
                .Where(order => order.Kind == RiotOrderObservationKind.Active &&
                                order.OrderState is 1 or 3 or 7 or 9 &&
                                !string.IsNullOrWhiteSpace(order.OrderId))
                .Select(order => new RiotListedOrder(
                    order.OrderId!,
                    order.UpperId,
                    order.OrderState,
                    order.VehicleKey,
                    order.OrderState == 1 ? "--" : order.VehicleKey))];
            return Task.FromResult(new RiotUnfinishedOrderListing(
                true, [.. own, .. _otherOrders], _clock.GetUtcNow()));
        }

        public Task<RiotOrderStateReading> ReadOrderStateAsync(string orderId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            int? state = _otherOrders.FirstOrDefault(order => order.OrderId == orderId)?.OrderState
                         ?? (_endedOtherOrders.TryGetValue(orderId, out int ended) ? ended : (int?)null)
                         ?? _orders.Values.FirstOrDefault(order => order.OrderId == orderId)?.OrderState;
            return Task.FromResult(new RiotOrderStateReading(orderId, state, _clock.GetUtcNow()));
        }

        public Task<RiotCommandCallResult> IssueOrderCommandAsync(
            RiotOrderCommandKind kind,
            string orderId,
            string? reason,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            OrderCommands.Add((kind, orderId, reason));
            if (kind == RiotOrderCommandKind.Cancel && CancelTakesEffect &&
                OrderCommandDisposition == RiotCommandCallDisposition.Accepted)
            {
                if (_otherOrders.Any(order => order.OrderId == orderId))
                {
                    EndPlacedOrder(orderId, RiotOrderState.Cancelled);
                }
                foreach (string upperId in _orders.Where(entry => entry.Value.OrderId == orderId).Select(entry => entry.Key)
                             .ToArray())
                {
                    CancelOrder(upperId);
                }
            }
            if (CrashAfterNextOrderCommand)
            {
                CrashAfterNextOrderCommand = false;
                throw new IOException($"The process stopped after RIoT received {kind} for {orderId} and before the answer was recorded.");
            }
            return Task.FromResult(new RiotCommandCallResult(
                OrderCommandDisposition,
                new RiotOrderCallReceipt(RiotCommandTypeNames.For(kind), OrderCommandDisposition.ToString(), _clock.GetUtcNow())));
        }

        public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind kind,
            string deviceKey,
            CancellationToken cancellationToken)
        {
            _ = deviceKey;
            _ = cancellationToken;
            throw new InvalidOperationException(
                $"No emergency command is expected from the foreign running order supervision; {kind} was issued.");
        }

        private static string UpperId(string purpose) => purpose switch
        {
            "TO_PICKUP" => "W2G-10000000-0000-4000-8000-000000000001-PICKUP-1",
            "TO_GATE" => "W2G-10000000-0000-4000-8000-000000000001-GATE-1",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };
    }

    /// <summary>
    /// A RIoT that lists no unfinished order and is never sent an order command (control-server#330), for the fixtures whose
    /// tests are not about foreign orders. A command reaching it is a failure, not something to answer.
    /// </summary>
    /// <remarks>
    /// Its answers carry a fixed observation time and never read the fixture's clock: the fleet fixture's transcript tests move
    /// their clock on every read, so a read made by this double would shift every timestamp after it and look like a change in
    /// what the round decided (control-server#330 found that out the first time).
    /// </remarks>
    internal sealed class QuietForeignOrderRiot : IRiotOrderListingFacts, IRiotOrderCommandGateway
    {
        public Task<RiotUnfinishedOrderListing> ListUnfinishedOrdersAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new RiotUnfinishedOrderListing(true, [], DateTimeOffset.UnixEpoch));
        }

        public Task<RiotOrderStateReading> ReadOrderStateAsync(string orderId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new RiotOrderStateReading(orderId, null, DateTimeOffset.UnixEpoch));
        }

        public Task<RiotCommandCallResult> IssueOrderCommandAsync(
            RiotOrderCommandKind kind,
            string orderId,
            string? reason,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"No order command is expected here; {kind} for {orderId} was issued.");

        public Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
            RiotEmergencyCommandKind kind,
            string deviceKey,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"No emergency command is expected here; {kind} for {deviceKey} was issued.");

        /// <summary>The supervisor these fixtures run, over this quiet RIoT.</summary>
        public static ForeignRunningOrderSupervisor Supervisor(
            ControlServerDbContext context,
            JourneyRuntimeOptions options,
            TimeProvider clock)
        {
            QuietForeignOrderRiot riot = new();
            return new ForeignRunningOrderSupervisor(
                context,
                riot,
                riot,
                new RiotOrderCommandAuditStore(context),
                new VehicleRoster(Microsoft.Extensions.Options.Options.Create(options)),
                clock,
                NullLogger<ForeignRunningOrderSupervisor>.Instance);
        }
    }

    internal sealed class RecordingPeer : IOnboardPeer
    {
        public List<byte[]> Lines { get; } = [];

        /// <summary>
        /// Lets a test answer a command the moment it is sent, the way the real peer does. Without
        /// it an answer can only be staged before an iteration, which hides everything that depends
        /// on how long the server takes to come back and read it.
        /// </summary>
        public Func<string, Task>? OnMessageSent { get; set; }

        public async Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Lines.Add(ndjsonLine.ToArray());
            if (OnMessageSent is not null)
            {
                await OnMessageSent(System.Text.Encoding.UTF8.GetString(ndjsonLine.Span)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Answers snapshots the way <c>OnboardHmi_MVP@304e6ad</c> does.
    /// </summary>
    /// <remarks>
    /// That peer keys an adopted snapshot on its message type alone, journals it in SQLite and
    /// never deletes the row, so the revision it holds survives a reconnect. A revision below the
    /// one it holds is refused as SNAPSHOT_REVISION_REGRESSION and never acknowledged; a duplicate
    /// at the revision it already holds is journalled and acknowledged again, because the journal
    /// write and the acknowledgement are sequential statements with no early exit between them.
    /// Acknowledgements are buffered rather than applied on receipt so that a test can lose exactly
    /// the ones a peer had in flight when its connection dropped.
    /// </remarks>
    internal sealed class AdoptingPeer(ControlServerDbContext context, TimeProvider clock)
    {
        private readonly Dictionary<string, long> _journal = new(StringComparer.Ordinal);
        private readonly List<(string MessageId, string MessageType, string ContentSha256, long Revision)> _buffered = [];

        public List<(string MessageType, long Delivered, long Held)> Regressions { get; } = [];

        public List<(string MessageType, long Revision)> Adopted { get; } = [];

        public void Receive(string ndjsonLine)
        {
            string wire = ndjsonLine.TrimEnd('\n');
            using JsonDocument document = JsonDocument.Parse(wire);
            JsonElement root = document.RootElement;
            string messageType = root.GetProperty("messageType").GetString()!;
            string? revisionProperty = SnapshotRevisionProperty(messageType);
            if (revisionProperty is null)
            {
                return;
            }

            long revision = root.GetProperty("payload").GetProperty(revisionProperty).GetInt64();
            if (_journal.TryGetValue(messageType, out long held) && revision < held)
            {
                Regressions.Add((messageType, revision, held));
                return;
            }

            if (!_journal.TryGetValue(messageType, out held) || revision != held)
            {
                Adopted.Add((messageType, revision));
            }

            _journal[messageType] = revision;
            _buffered.Add((
                root.GetProperty("messageId").GetString()!,
                messageType,
                Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(wire)))
                    .ToLowerInvariant(),
                revision));
        }

        public async Task DeliverBufferedAcksAsync()
        {
            WireToGateStore store = new(context);
            foreach ((string messageId, string messageType, string contentSha256, long revision) in _buffered)
            {
                await store.AcknowledgeOutboundEnvelopeAsync(
                    messageId,
                    messageType,
                    contentSha256,
                    revision,
                    clock.GetUtcNow(),
                    TestContext.Current.CancellationToken);
            }

            _buffered.Clear();
        }

        public void LoseBufferedAcks() => _buffered.Clear();
    }

    internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }

    internal static object BeforeSublotOperator(RuntimeFixture fixture) => new
    {
        operatorId = "OP-001",
        verificationMethod = "BADGE",
        verifiedAt = fixture.Clock.GetUtcNow()
    };

    internal static string BeforeSublotEnvelope(
        RuntimeFixture fixture,
        string messageId,
        string messageType,
        long generation,
        object payload) => JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId,
            correlationId = (string?)null,
            agvId = fixture.Options.AgvId,
            sessionGeneration = generation,
            // Fixed for the same reason as observedAt: sentAt is part of the line a resend repeats.
            sentAt = Now,
            payload
        }, SerializerOptions);
    internal const string RejectedEntryPackage = "PDFN5×6-8L(12R)";
}
