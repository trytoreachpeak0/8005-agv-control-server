using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// control-server#826: how the server judges the vehicle's answers to a pre-departure safety check.
/// </summary>
/// <remarks>
/// <para>
/// On 2026-10-21 agv02's clock ran about 0.12 s ahead of the server's. Its answers were stamped with an
/// <c>observedAt</c> in the server's future, and the server required <c>observedAt &lt;= now</c> exactly, so every
/// answer judged on receipt was invalid; agv02 stood at the pickup four times, the longest for 22 minutes, until
/// its clock was stepped back. The server now accepts an <c>observedAt</c> up to
/// <see cref="JourneyRuntimeOptions.PeerClockSkewTolerance"/> ahead of its own clock.
/// </para>
/// <para>
/// The second half: the server read the answers to one check id oldest first and let the first one decide. Once
/// the onboard side re-answers the same check (onboard-hmi#386), that rule meant a first invalid answer kept every
/// later valid one from ever being read. Answers that no longer describe the vehicle -- another session
/// generation, another safety state version, past their own <c>validUntil</c>, older than the evidence age, or
/// stamped beyond the tolerance -- are now passed over, and of the rest the newest one decides.
/// </para>
/// </remarks>
public sealed class PreDepartureAnswerClockSkewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task AnAnswerStampedAFewTenthsOfASecondAheadOfTheServerDepartsTheVehicle()
    {
        await using RuntimeFixture fixture = await DemandAsync();
        AnswerEveryCheck(fixture, TimeSpan.FromMilliseconds(120));

        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task AnAnswerStampedBeyondTheToleranceIsNotValidAndTheLogSaysWhy()
    {
        await using RuntimeFixture fixture = await DemandAsync();
        AnswerEveryCheck(fixture, TimeSpan.FromMilliseconds(600));

        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Equal("PRE_DEPARTURE_SAFETY_NOT_VALID", runtime.BlockReasonCode);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Contains(
            fixture.EngineLog.Entries,
            entry => entry.Level == LogLevel.Warning &&
                     entry.Message.Contains(runtime.PreDepartureSafetyCheckId!, StringComparison.Ordinal) &&
                     entry.Message.Contains("observedAt-ahead", StringComparison.Ordinal));
    }

    /// <summary>
    /// The case the field hit once the vehicle answers a re-asked check again: the first answer under the check id
    /// no longer describes the vehicle, a later one does. The later one departs the vehicle, and it is the one the
    /// journey records as consumed.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    [InlineData("observedAt-ahead")]
    [InlineData("validUntil-passed")]
    [InlineData("older-than-evidence-age")]
    [InlineData("session-generation")]
    [InlineData("safety-state-version")]
    public async Task AnOutdatedFirstAnswerDoesNotHideALaterValidOne(string outdatedBy)
    {
        await using RuntimeFixture fixture = await DemandAsync();
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        string checkId = runtime.PreDepartureSafetyCheckId!;
        DateTimeOffset now = fixture.Clock.GetUtcNow();

        string firstId = Guid.NewGuid().ToString("D");
        (object first, long generation) = outdatedBy switch
        {
            "observedAt-ahead" => (Answer(checkId, 7, now.AddSeconds(5)), 1L),
            "validUntil-passed" => (Answer(checkId, 7, now.AddSeconds(-5), validFor: TimeSpan.FromSeconds(2)), 1L),
            "older-than-evidence-age" => (Answer(checkId, 7, now - fixture.Options.MaximumEvidenceAge - TimeSpan.FromSeconds(1)), 1L),
            "session-generation" => (Answer(checkId, 7, now), 0L),
            "safety-state-version" => (Answer(checkId, 6, now), 1L),
            _ => throw new ArgumentOutOfRangeException(nameof(outdatedBy)),
        };
        await fixture.AddInboxAsync(
            firstId, "PreDepartureSafetyCheckResult", first, checkId, generation, receivedAt: now.AddSeconds(-1));
        string secondId = Guid.NewGuid().ToString("D");
        await fixture.AddInboxAsync(
            secondId, "PreDepartureSafetyCheckResult", Answer(checkId, 7, now), checkId, receivedAt: now);

        await fixture.Engine.ExecuteOnceAsync(Token);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(secondId, runtime.ConsumedSafetyResultMessageId);
    }

    /// <summary>
    /// The newest answer decides in both directions: a vehicle that answered SAFE and then, under the same check,
    /// UNSAFE (a door came open) does not leave on the older SAFE.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task ALaterUnsafeAnswerOutranksAnEarlierSafeOne()
    {
        await using RuntimeFixture fixture = await DemandAsync();
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        string checkId = runtime.PreDepartureSafetyCheckId!;
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "PreDepartureSafetyCheckResult",
            Answer(checkId, 7, now.AddMilliseconds(-500)), checkId, receivedAt: now.AddMilliseconds(-500));
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "PreDepartureSafetyCheckResult",
            Answer(checkId, 7, now, allTargetSlotsLocked: false), checkId, receivedAt: now);

        await fixture.Engine.ExecuteOnceAsync(Token);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Equal("PRE_DEPARTURE_SAFETY_NOT_VALID", runtime.BlockReasonCode);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Contains(
            fixture.EngineLog.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("not-safe", StringComparison.Ordinal));
    }

    /// <summary>The tolerance is about the clock only: an answer that says the vehicle is not safe stays not safe.</summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    [InlineData("door-not-locked")]
    [InlineData("vehicle-moving")]
    public async Task AnUnsafeAnswerWithinTheToleranceStillHoldsTheVehicle(string unsafeBy)
    {
        await using RuntimeFixture fixture = await DemandAsync();
        AnswerEveryCheck(
            fixture,
            TimeSpan.FromMilliseconds(120),
            allTargetSlotsLocked: unsafeBy != "door-not-locked",
            vehicleStopped: unsafeBy != "vehicle-moving");

        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Equal("PRE_DEPARTURE_SAFETY_NOT_VALID", runtime.BlockReasonCode);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
    }

    /// <summary>
    /// The state a real onboard reports all the way along its own order: the session is not ready because of an
    /// order this server created. A synthetic onboard always reports safe and never shows it. A SAFE answer inside
    /// the tolerance that arrives while the session is in that state does not depart the vehicle; once the session
    /// is ready again, a fresh answer does.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task WhileTheSessionIsNotReadyOnTheOwnOrderASkewedSafeAnswerDoesNotDepart()
    {
        await using RuntimeFixture fixture = await DemandAsync();
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        string checkId = runtime.PreDepartureSafetyCheckId!;
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "PreDepartureSafetyCheckResult",
            Answer(checkId, 7, now.AddMilliseconds(120)), checkId, receivedAt: now);

        await fixture.Engine.ExecuteOnceAsync(Token);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));

        await fixture.RestoreSessionReadyAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        now = fixture.Clock.GetUtcNow();
        string freshId = Guid.NewGuid().ToString("D");
        await fixture.AddInboxAsync(
            freshId, "PreDepartureSafetyCheckResult",
            Answer((await fixture.RuntimeAsync()).PreDepartureSafetyCheckId!, 7, now.AddMilliseconds(120)),
            (await fixture.RuntimeAsync()).PreDepartureSafetyCheckId, receivedAt: now);
        await fixture.Engine.ExecuteOnceAsync(Token);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(freshId, runtime.ConsumedSafetyResultMessageId);
    }

    private static async Task<RuntimeFixture> DemandAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        return fixture;
    }

    /// <summary>
    /// Answers every departure check as it is sent, the way the peer does, stamped <paramref name="ahead"/> of the
    /// server's clock: the answer is judged in the same iteration that asks, so it has to be there by then.
    /// </summary>
    private static void AnswerEveryCheck(
        RuntimeFixture fixture,
        TimeSpan ahead,
        bool allTargetSlotsLocked = true,
        bool vehicleStopped = true) =>
        fixture.Peer.OnMessageSent = async line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("messageType").GetString() != "PreDepartureSafetyCheck")
            {
                return;
            }
            string checkId = root.GetProperty("payload").GetProperty("preDepartureSafetyCheckId").GetString()!;
            DateTimeOffset now = fixture.Clock.GetUtcNow();
            await fixture.AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "PreDepartureSafetyCheckResult",
                Answer(
                    checkId, 7, now + ahead,
                    validFor: TimeSpan.FromSeconds(2),
                    allTargetSlotsLocked: allTargetSlotsLocked,
                    vehicleStopped: vehicleStopped),
                checkId,
                receivedAt: now);
        };

    private static object Answer(
        string checkId,
        long safetyStateVersion,
        DateTimeOffset observedAt,
        TimeSpan? validFor = null,
        bool allTargetSlotsLocked = true,
        bool vehicleStopped = true)
    {
        bool safe = allTargetSlotsLocked && vehicleStopped;
        return new
        {
            preDepartureSafetyCheckId = checkId,
            checkPurpose = "DEPARTURE",
            outcome = safe ? "SAFE" : "UNSAFE",
            observedAt,
            safetyStateVersion,
            validUntil = observedAt + (validFor ?? TimeSpan.FromMinutes(1)),
            safety = new
            {
                departureSafe = safe,
                vehicleStopped,
                allTargetSlotsLocked,
                allUnlockOutputsReset = true,
                unknownPresent = false,
                reasonCodes = safe ? Array.Empty<string>() : ["SLOT_DOOR_NOT_LOCKED"]
            }
        };
    }
}
