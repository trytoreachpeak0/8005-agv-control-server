using System.Data.Common;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// Entering a sublot after the dispatch: the server resolves the demand inside the dispatch scope and
/// recomputes the authoritative basket count against BR-013 section 2, refusing the entry with the real
/// reason when it cannot — commanding no slot and opening nothing (control-server#82, FP-IS-02).
/// </summary>
/// <remarks>
/// <para>
/// <b>What the entry answers is the address, and only the address.</b> Protocol 2.0.0 took the demand
/// off <c>SublotSubmitted</c>: the operator enters a sublot, and which demand that sublot belongs to is
/// the server's to resolve, inside the demands this journey was dispatched for rather than the whole
/// catalogue. A submission whose address does not answer the entry request open now — another vehicle,
/// another operation session, another stop or round of this journey — is not a wrong entry and is not
/// refused; it is not this request's answer at all.
/// </para>
/// <para>
/// <b>The refusal is the operator's, not the maintainer's.</b> BR-013 makes the recomputed count
/// authoritative and forbids falling back to a default, so a count that cannot be established stops the
/// load and travels back on the wire, where the operator is standing. The journey is left exactly where
/// it was — still waiting at the pickup, with no slot reserved and nothing unlocked — so a rescan after
/// the data is fixed is judged afresh, and the station deadline of <c>control-server#79</c> still runs.
/// </para>
/// </remarks>
public sealed partial class JourneyRuntimeWorkerTests
{
    private const string RejectedEntryDemandId = "10000000-0000-4000-8000-000000000001";
    private const string RejectedEntrySublot = "SUBLOT-001";
    private const string RejectedEntryPackage = "PDFN5×6-8L(12R)";

    /// <summary>
    /// The message about every dispatch of a stop that nothing has entered yet raised one for: the
    /// inbox holds submissions from other vehicles, other journeys and earlier rounds of this one, and
    /// judging them against the stop being waited at reported "the entry does not match" for rows that
    /// were never answers to it.
    /// </summary>
    /// <remarks>
    /// The fourth submission is the real one, and it is what makes the first three mean something: a
    /// filter that skipped everything would pass the first three assertions and fail this one.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task SubmissionsThatDoNotAnswerTheEntryRequestOpenNowAreSkippedWithoutABlockReason()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachEntryWaitAsync();

        await AddEntryAsync(
            fixture,
            agvId: "老厂前线新多仓位2",
            payload: EntryPayload(await fixture.RuntimeAsync(), RejectedEntrySublot));
        await AddEntryAsync(
            fixture,
            payload: EntryPayload(await fixture.RuntimeAsync(), RejectedEntrySublot, operationSessionId: "session-of-another-journey"));
        await AddEntryAsync(
            fixture,
            payload: EntryPayload(
                await fixture.RuntimeAsync(),
                RejectedEntrySublot,
                worklistRevision: (await fixture.RuntimeAsync()).WorklistRevision + 1));

        await fixture.Engine.ExecuteOnceAsync(token);

        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        Assert.Null(waiting.BlockReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, waiting.Stage);
        Assert.Empty(await RejectionEnvelopesAsync(fixture));
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());

        await AddEntryAsync(fixture, payload: EntryPayload(waiting, RejectedEntrySublot));
        await fixture.Engine.ExecuteOnceAsync(token);

        JourneyRuntimeRow loaded = await fixture.RuntimeAsync();
        Assert.Null(loaded.BlockReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, loaded.Stage);
        Assert.Single(await fixture.OutboxTypesAsync(), type => type == "SlotOperationCommand");
    }

    /// <summary>
    /// The submission read is narrowed in the query, so the inbox's history is not read back and parsed
    /// on every poll of every stop. The parse still decides what matches; narrowing only keeps rows that
    /// cannot be answers out of the read.
    /// </summary>
    /// <remarks>
    /// Asserted on the SQL the engine really sent, because nothing observable can tell a narrowed read
    /// from a wide one — a pre-filter that changed results would be a bug, not a shortcut. The address
    /// lives in <c>RequestJson</c>, so a narrowed read filters on that column. Asserted past the
    /// <c>WHERE</c>, because every read of the table selects the column: the projection says nothing
    /// about what was excluded.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheSubmissionReadNarrowsOnTheAddressInsteadOfReadingTheInboxHistory()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingCommands commands = new();
        await using RuntimeFixture fixture = await ReachEntryWaitAsync(commands);

        await AddEntryAsync(fixture, payload: EntryPayload(await fixture.RuntimeAsync(), RejectedEntrySublot));
        await fixture.Engine.ExecuteOnceAsync(token);

        Assert.True(
            commands.InboxReads.Any(ReadsSubmissionsAndNarrowsOnTheAddress),
            "No read of ProtocolInbox both selected the SublotSubmitted rows and narrowed them on the "
            + "address, so the inbox's history is read back and parsed on every poll. Reads sent, "
            + "distinct: " + string.Join(" | ", commands.InboxReads.Distinct(StringComparer.Ordinal).Take(4)));
    }

    /// <summary>
    /// Whether one query reads the submissions and excludes rows by address rather than by message type
    /// alone.
    /// </summary>
    private static bool ReadsSubmissionsAndNarrowsOnTheAddress(string sql)
    {
        int where = sql.IndexOf("WHERE", StringComparison.Ordinal);
        return where >= 0
            && sql.Contains("MessageType", StringComparison.Ordinal)
            && sql[where..].Contains("RequestJson", StringComparison.Ordinal);
    }

    /// <summary>
    /// CV-SUBLOT-REJECTED-AFTER-ENTRY, all three steps: the entry request opens the stop, the submission
    /// answers it naming only a sublot, and the server refuses that entry — after re-reading the box
    /// count and the approved capacity — rather than unlocking anything.
    /// </summary>
    /// <remarks>
    /// The refusal is the capacity case, the vector's own <c>stableErrorCode</c>. The approved mapping
    /// is withdrawn after the journey is already at the pickup, which is the whole point of revalidating
    /// here: the table the dispatch was admitted against is not the one the entry has to be refused
    /// against.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SUBLOT-REJECTED-AFTER-ENTRY")]
    public async Task AnEntryWhosePackageCapacityIsGoneAfterTheDispatchIsRefusedAndUnlocksNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachEntryWaitAsync();

        // Step 1: the entry request is what the operator is answering.
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Contains("SublotEntryRequested", await fixture.OutboxTypesAsync());
        JsonElement request = await fixture.OutboxPayloadAsync(runtime.SublotRequestMessageId);
        Assert.Equal(
            [RejectedEntrySublot],
            request.GetProperty("expectedSublots").EnumerateArray().Select(item => item.GetString()));
        Assert.False(request.TryGetProperty("demandId", out _));

        // Step 2: the operator's entry names a sublot and no demand.
        string submitted = await AddEntryAsync(fixture, payload: EntryPayload(runtime, RejectedEntrySublot));
        await SetPackageCapacityAsync(fixture, capacity: null);

        // Step 3: refused, for the reason the table now gives.
        await fixture.Engine.ExecuteOnceAsync(token);

        JsonElement refusal = Assert.Single(await RejectionEnvelopesAsync(fixture));
        Assert.Equal("SublotRejected", refusal.GetProperty("messageType").GetString());
        Assert.Equal(submitted, refusal.GetProperty("correlationId").GetString());
        JsonElement payload = refusal.GetProperty("payload");
        Assert.Equal(RejectedEntryDemandId, payload.GetProperty("demandId").GetString());
        Assert.Equal(RejectedEntrySublot, payload.GetProperty("rejectedSublot").GetString());
        Assert.Equal(
            ServerReasonCodes.PackageCapacityUnresolved,
            payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(runtime.WorklistRevision, payload.GetProperty("currentWorklistRevision").GetInt64());
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
        Assert.Empty(await fixture.Context.StationOperations.AsNoTracking().ToArrayAsync(token));
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// The sublot the operator entered belongs to no demand this journey was dispatched for. The server
    /// refuses it naming no demand, which is what <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c> means: there is no
    /// demand to name. Nothing is loaded and no slot is reserved.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ASublotOutsideTheDispatchScopeIsRefusedNamingNoDemand()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachEntryWaitAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        string entered = await AddEntryAsync(fixture, payload: EntryPayload(runtime, "SUBLOT-NOT-IN-SCOPE"));

        await fixture.Engine.ExecuteOnceAsync(token);

        JsonElement payload = Assert.Single(await RejectionEnvelopesAsync(fixture)).GetProperty("payload");
        Assert.Equal(
            ServerReasonCodes.SublotNotInDispatchScope,
            payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("demandId").ValueKind);
        Assert.Equal("SUBLOT-NOT-IN-SCOPE", payload.GetProperty("rejectedSublot").GetString());
        JsonElement envelope = Assert.Single(await RejectionEnvelopesAsync(fixture));
        Assert.Equal(entered, envelope.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
        Assert.Empty(await fixture.Context.StationOperations.AsNoTracking().ToArrayAsync(token));
        Assert.Equal(runtime.TargetSlotsJson, (await fixture.RuntimeAsync()).TargetSlotsJson);
    }

    /// <summary>
    /// Every way BR-013 section 2 says the authoritative basket count cannot be established, each with
    /// the reason code the candidate's registry gives it and none of them starting a load.
    /// </summary>
    /// <remarks>
    /// The count is the number of baskets the slots were reserved against at acceptance, so the last
    /// case — a count that recomputes to a different number — is refused for the same reason as a count
    /// that cannot be read at all: the reservation no longer fits what the machine holds.
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [InlineData("lookup-failed", ServerReasonCodes.SublotBoxCountUnavailable)]
    [InlineData("no-result", ServerReasonCodes.SublotBoxCountUnavailable)]
    [InlineData("non-positive-count", ServerReasonCodes.SublotBoxCountUnavailable)]
    [InlineData("package-missing", ServerReasonCodes.PackageCapacityUnresolved)]
    [InlineData("capacity-unresolved", ServerReasonCodes.PackageCapacityUnresolved)]
    [InlineData("capacity-non-positive", ServerReasonCodes.PackageCapacityUnresolved)]
    [InlineData("count-changed", "EXPECTED_BASKET_COUNT_MISMATCH")]
    public async Task AnEntryTheBasketCountCannotBeRecomputedForIsRefusedWithItsOwnReasonCode(
        string situation,
        string expectedReasonCode)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachEntryWaitAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        switch (situation)
        {
            case "lookup-failed":
                fixture.BoxCounts.BeforeRead = () => throw new HttpRequestException("SUBLOT_BOX_COUNT is unreachable.");
                break;
            case "no-result":
                fixture.BoxCounts.Remove(RejectedEntrySublot);
                break;
            case "non-positive-count":
                fixture.BoxCounts.Set(RejectedEntrySublot, 0);
                break;
            case "package-missing":
                await SetFrozenPackageAsync(fixture, package: null);
                break;
            case "capacity-unresolved":
                await SetPackageCapacityAsync(fixture, capacity: null);
                break;
            case "capacity-non-positive":
                await SetPackageCapacityAsync(fixture, capacity: 0);
                break;
            case "count-changed":
                fixture.BoxCounts.Set(RejectedEntrySublot, runtime.ExpectedBasketCount * 4 + 4);
                break;
        }

        string submitted = await AddEntryAsync(fixture, payload: EntryPayload(runtime, RejectedEntrySublot));
        await fixture.Engine.ExecuteOnceAsync(token);

        JsonElement refusal = Assert.Single(await RejectionEnvelopesAsync(fixture));
        Assert.Equal(submitted, refusal.GetProperty("correlationId").GetString());
        JsonElement payload = refusal.GetProperty("payload");
        Assert.Equal(expectedReasonCode, payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(RejectedEntryDemandId, payload.GetProperty("demandId").GetString());
        Assert.Equal(RejectedEntrySublot, payload.GetProperty("rejectedSublot").GetString());

        JourneyRuntimeRow refused = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, refused.Stage);
        Assert.Null(refused.ConsumedSublotMessageId);
        Assert.Equal(runtime.TargetSlotsJson, refused.TargetSlotsJson);
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
        Assert.Empty(await fixture.Context.StationOperations.AsNoTracking().ToArrayAsync(token));
        Assert.Null((await fixture.LeaseAsync()).ReleasedAt);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
    }

    /// <summary>
    /// The refusal answers the submission it refused: <c>correlationId</c> is that submission's
    /// <c>messageId</c>, which is what lets the operator's screen attach the reason to the thing they
    /// scanned, and <c>currentWorklistRevision</c> is the revision the stop stands at.
    /// </summary>
    /// <remarks>
    /// The candidate's manifest makes both structural: <c>SublotRejected</c> is a RESPONSE correlated
    /// by <c>REQUIRED_ORIGINAL_MESSAGE_ID</c> and carries no business deduplication key. MVP shipped
    /// this one null and the peer refused every refusal with <c>CORRELATION_INVALID</c>, so no reason
    /// ever reached an operator (<c>8005-agv-control-server#20</c>).
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheRefusalAnswersTheSubmissionItRefusedAtTheStopsCurrentRevision()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachEntryWaitAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        string submitted = await AddEntryAsync(
            fixture,
            messageId: "s1000000-0000-4000-8000-000000000001",
            payload: EntryPayload(runtime, "SUBLOT-NOT-IN-SCOPE"));

        await fixture.Engine.ExecuteOnceAsync(token);

        JsonElement envelope = Assert.Single(await RejectionEnvelopesAsync(fixture));
        Assert.Equal(submitted, envelope.GetProperty("correlationId").GetString());
        Assert.NotEqual(submitted, envelope.GetProperty("messageId").GetString());
        Assert.Equal(fixture.Options.AgvId, envelope.GetProperty("agvId").GetString());
        Assert.Equal(1, envelope.GetProperty("sessionGeneration").GetInt64());
        JsonElement payload = envelope.GetProperty("payload");
        Assert.Equal(runtime.OperationSessionId, payload.GetProperty("operationSessionId").GetString());
        Assert.Equal(runtime.WorklistRevision, payload.GetProperty("currentWorklistRevision").GetInt64());
        // The rejection is the durable outbound, not a bare send: the operator's reason survives the
        // connection it was refused on.
        Assert.Contains("SublotRejected", await fixture.OutboxTypesAsync());
    }

    /// <summary>
    /// One submission is refused once, however many times the runtime comes back to the stop — and the
    /// refusal costs one round of remote reads, not one per poll. A rescan after the data is fixed is a
    /// new submission and is judged afresh, and it loads.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ARefusedSubmissionIsRefusedOnceAndTheNextOneIsJudgedAfresh()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachEntryWaitAsync();
        int reads = 0;
        fixture.BoxCounts.BeforeRead = () => reads++;
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        await AddEntryAsync(fixture, payload: EntryPayload(runtime, "SUBLOT-NOT-IN-SCOPE"));

        await fixture.Engine.ExecuteOnceAsync(token);
        await fixture.Engine.ExecuteOnceAsync(token);
        int afterTheStopSettledIntoWaiting = reads;

        await fixture.Engine.ExecuteOnceAsync(token);

        Assert.Single(await RejectionEnvelopesAsync(fixture));
        Assert.Equal(1, PeerMessageCount(fixture, "SublotRejected"));
        Assert.Equal(afterTheStopSettledIntoWaiting, reads);

        // The operator rescans the sublot they meant. Under the same worklist revision, that is a
        // second submission of the same value — no business deduplication key makes it a conflict.
        await AddEntryAsync(fixture, payload: EntryPayload(runtime, RejectedEntrySublot));
        await fixture.Engine.ExecuteOnceAsync(token);

        JourneyRuntimeRow loaded = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, loaded.Stage);
        Assert.Single(await fixture.OutboxTypesAsync(), type => type == "SlotOperationCommand");
        Assert.Equal(runtime.SublotRequestMessageId, loaded.SublotRequestMessageId);
        ProtocolOutboxRow entryRequest = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == runtime.SublotRequestMessageId, token);
        Assert.NotNull(entryRequest.AcknowledgedAt);
    }

    /// <summary>
    /// Two rescans of the same sublot under one worklist revision are two submissions and two refusals,
    /// and neither is a content conflict. <c>businessDedupKeys</c> for both messages is empty, so
    /// nothing in the protocol makes the second submission a replay of the first — and the two
    /// refusals are two different messages, which is what keeps the second one from being refused as a
    /// rewritten identity.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TwoRescansOfOneSublotUnderOneRevisionAreTwoRefusalsAndNotAConflict()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachEntryWaitAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.BoxCounts.Set(RejectedEntrySublot, 0);
        string first = await AddEntryAsync(fixture, payload: EntryPayload(runtime, RejectedEntrySublot));
        await fixture.Engine.ExecuteOnceAsync(token);
        string second = await AddEntryAsync(fixture, payload: EntryPayload(runtime, RejectedEntrySublot));
        await fixture.Engine.ExecuteOnceAsync(token);

        JsonElement[] refusals = await RejectionEnvelopesAsync(fixture);
        Assert.Equal(2, refusals.Length);
        Assert.Equal(
            [first, second],
            refusals.Select(refusal => refusal.GetProperty("correlationId").GetString())
                .Order(StringComparer.Ordinal));
        Assert.Equal(2, refusals.Select(refusal => refusal.GetProperty("messageId").GetString()).Distinct().Count());
        Assert.All(
            refusals,
            refusal => Assert.Equal(
                ServerReasonCodes.SublotBoxCountUnavailable,
                refusal.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString()));
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
    }

    /// <summary>
    /// A stop whose entry was refused still runs out its station departure wait and ends the way an
    /// unscanned one does (control-server#79): the operator's refusal is not a decision about the
    /// demand, and the vehicle must not be held at the pickup by it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AStopWhoseEntryWasRefusedStillEndsAtItsStationDeadline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachEntryWaitAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        await AddEntryAsync(fixture, payload: EntryPayload(runtime, "SUBLOT-NOT-IN-SCOPE"));
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.Single(await RejectionEnvelopesAsync(fixture));
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(token);

        JourneyRuntimeRow ended = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Completed, ended.Stage);
        Assert.Equal(JourneyRuntimeEngine.StationTimeoutCancellationReason, ended.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.DemandRowAsync()).Status);
        Assert.NotNull((await fixture.LeaseAsync()).ReleasedAt);
        Assert.DoesNotContain("SlotOperationCommand", await fixture.OutboxTypesAsync());
    }

    /// <summary>
    /// Carries a journey to its pickup arrival and proves the doors shut, so the station deadline is
    /// live and the stop is one an entry can answer.
    /// </summary>
    private static async Task<RuntimeFixture> ReachEntryWaitAsync(DbCommandInterceptor? commands = null)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: commands);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(RejectedEntryDemandId, RejectedEntrySublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(RejectedEntrySublot, 7);
        JourneyRuntimeRow runtime = await fixture.AdvanceToSublotWaitAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        await fixture.ProveSlotDoorsClosedAsync();
        return fixture;
    }

    /// <summary>
    /// One entry as the peer sends it: the address it answers and the sublot the operator entered, and
    /// no demand — protocol 2.0.0 took the demand off the message and made resolving it the server's job.
    /// </summary>
    private static object EntryPayload(
        JourneyRuntimeRow runtime,
        string sublot,
        string? operationSessionId = null,
        long? worklistRevision = null) => new
        {
            operationSessionId = operationSessionId ?? runtime.OperationSessionId,
            stationId = runtime.PickupStationId,
            worklistRevision = worklistRevision ?? runtime.WorklistRevision,
            sublot,
            entryMethod = "SCANNER",
            @operator = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = Now }
        };

    /// <summary>
    /// Writes a submission the way the peer's connection would, with the envelope fields a test needs to
    /// vary — the vehicle and the session generation live on the envelope, not in the payload.
    /// </summary>
    private static async Task<string> AddEntryAsync(
        RuntimeFixture fixture,
        object payload,
        string? messageId = null,
        string? agvId = null,
        long sessionGeneration = 1)
    {
        string id = messageId ?? Guid.NewGuid().ToString("D");
        fixture.Context.ProtocolInbox.Add(new ProtocolInboxRow
        {
            MessageId = id,
            MessageType = "SublotSubmitted",
            RequestJson = JsonSerializer.Serialize(
                new
                {
                    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                    profileId = ProtocolCandidateIdentity.ProfileId,
                    protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                    protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                    messageType = "SublotSubmitted",
                    messageId = id,
                    correlationId = (string?)null,
                    agvId = agvId ?? fixture.Options.AgvId,
                    sessionGeneration,
                    sentAt = Now,
                    payload
                },
                SerializerOptions),
            ContentHash = new string('a', 64),
            FirstResponseJson = "{}",
            ReceivedAt = Now
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>
    /// Rewrites the PACKAGE frozen on the accepted demand. The runtime revalidates against what the
    /// demand was accepted with, so a package that was never there and one withdrawn since are the same
    /// fact to it; only the dispatch admits a demand, and that has already happened.
    /// </summary>
    private static async Task SetFrozenPackageAsync(RuntimeFixture fixture, string? package)
    {
        AcceptedDemandRow demand = await fixture.Context.AcceptedDemands.SingleAsync(
            row => row.DemandId == RejectedEntryDemandId, TestContext.Current.CancellationToken);
        demand.LiveMesFieldsJson = JsonSerializer.Serialize(
            new LiveMesFieldSet("N1-1", "EQP-01", "STEP-01", Now.AddMinutes(-10), package),
            SerializerOptions);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await fixture.RecreateEngineAsync();
    }

    /// <summary>
    /// Changes the approved capacity mapping for the demand's PACKAGE — the table moved after the
    /// vehicle was sent, which is exactly what revalidating after the entry is for. <c>null</c> is the
    /// mapping withdrawn, so nothing matches; zero is a mapping that matches but cannot be divided by,
    /// which BR-013 fails closed on rather than treating as a capacity.
    /// </summary>
    private static async Task SetPackageCapacityAsync(RuntimeFixture fixture, int? capacity)
    {
        PackageCapacityRuleRow rule = await fixture.Context.PackageCapacityRules.SingleAsync(
            row => row.Pattern == RejectedEntryPackage, TestContext.Current.CancellationToken);
        if (capacity is { } boxes)
        {
            rule.MaxBoxesPerBasket = boxes;
        }
        else
        {
            fixture.Context.PackageCapacityRules.Remove(rule);
        }

        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The outbound <c>SublotRejected</c> envelopes, whole: the correlation the refusal carries is on
    /// the envelope, not in the payload. Empty when nothing was refused.
    /// </summary>
    private static async Task<JsonElement[]> RejectionEnvelopesAsync(RuntimeFixture fixture)
    {
        string[] stored = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotRejected")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        JsonElement[] envelopes = new JsonElement[stored.Length];
        for (int index = 0; index < stored.Length; index++)
        {
            using JsonDocument document = JsonDocument.Parse(stored[index]);
            envelopes[index] = document.RootElement.Clone();
        }

        return envelopes;
    }

    /// <summary>How many lines of one type the peer was sent.</summary>
    private static int PeerMessageCount(RuntimeFixture fixture, string messageType) => fixture.Peer.Lines
        .Select(line =>
        {
            using JsonDocument document = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(line));
            return document.RootElement.GetProperty("messageType").GetString();
        })
        .Count(type => string.Equals(type, messageType, StringComparison.Ordinal));

    /// <summary>
    /// Captures the SQL the engine sent against the inbox, so a test can assert the read was narrowed
    /// before it happened rather than only that its results were right.
    /// </summary>
    /// <remarks>
    /// Reads only. This provider routes its writes through the same interception point, and a row being
    /// inserted is not a query that could have been narrowed.
    /// </remarks>
    private sealed class RecordingCommands : DbCommandInterceptor
    {
        public List<string> InboxReads { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("\"ProtocolInbox\"", StringComparison.Ordinal))
            {
                InboxReads.Add(command.CommandText);
            }

            return ValueTask.FromResult(result);
        }
    }
}
