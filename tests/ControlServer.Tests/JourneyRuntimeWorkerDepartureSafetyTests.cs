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
using ControlServer.Host.Runtime;
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
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// Split out of the one partial JourneyRuntimeWorkerTests class so xunit runs it in parallel with the
/// rest (control-server#130); the shared fixture is <see cref="JourneyRuntimeWorkerTestKit"/>.
/// </summary>
public sealed class JourneyRuntimeWorkerDepartureSafetyTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task DepartureSafetyAnsweredPromptlyIsJudgedWhileItIsStillValid()
    {
        // The peer answers a pre-departure safety check in tens of milliseconds and stamps the
        // answer with a validity window of its own, which is shorter than one poll interval. The
        // engine used to publish the check and come back for the answer on its next iteration, by
        // which time the window had closed -- the journey stopped at AwaitingDepartureSafety with
        // PRE_DEPARTURE_SAFETY_NOT_VALID and no movement was ever authorized. Every earlier test
        // staged the answer with a window a minute wide, so none of them could show it.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Peer.OnMessageSent = async line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("messageType").GetString() != "PreDepartureSafetyCheck")
            {
                return;
            }
            DateTimeOffset answeredAt = fixture.Clock.GetUtcNow();
            await fixture.AddInboxAsync(
                Guid.NewGuid().ToString("D"),
                "PreDepartureSafetyCheckResult",
                new
                {
                    preDepartureSafetyCheckId = root.GetProperty("payload")
                        .GetProperty("preDepartureSafetyCheckId").GetString(),
                    outcome = "SAFE",
                    observedAt = answeredAt,
                    safetyStateVersion = 7,
                    // The window the real peer grants: shorter than the engine's poll interval.
                    validUntil = answeredAt.AddSeconds(2),
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
                // The real peer correlates by the check id, not by the messageId of the request.
                root.GetProperty("payload").GetProperty("preDepartureSafetyCheckId").GetString());
        };

        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_GATE"));
    }

    /// <summary>
    /// CV-PREDEPARTURE-SAFETY-EXPIRES: PreDepartureSafetyCheck, its SAFE result, then a
    /// SafetyStateChanged. The answer was true of a safety state that no longer holds, so it may
    /// not authorize the departure (NEVER_DEPART_ON_EXPIRED_CHECK) -- and the check it answered is
    /// spent (EXPIRE_CHECK_ON_SAFETY_STATE_CHANGE). This server used to stop there: one check id per
    /// journey, judged invalid on every later poll, and the journey waited at the pickup for ever
    /// with PRE_DEPARTURE_SAFETY_NOT_VALID. The check is now retired and asked again under a new
    /// identity against the current safety state, and that answer departs the vehicle.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task ASafetyChangeAfterTheAnswerExpiresTheCheckAndTheServerAsksAgainUnderANewIdentity()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        string expiredCheckId = runtime.PreDepartureSafetyCheckId;
        string expiredMessageId = runtime.PreDepartureSafetyCheckMessageId;
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "PreDepartureSafetyCheckResult",
            SafeDepartureAnswer(expiredCheckId, 7, fixture.Clock.GetUtcNow()), expiredCheckId);
        await fixture.AddSafetyStateChangedAsync(8, departureSafe: true, vehicleStopped: true);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", runtime.BlockReasonCode);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
        Assert.NotEqual(expiredCheckId, runtime.PreDepartureSafetyCheckId);
        Assert.NotEqual(expiredMessageId, runtime.PreDepartureSafetyCheckMessageId);
        Assert.NotNull((await fixture.Context.ProtocolOutbox.SingleAsync(
            row => row.MessageId == expiredMessageId, TestContext.Current.CancellationToken)).FencedAt);
        Assert.Equal(8, await ExpectedSafetyStateVersionAsync(fixture, runtime));

        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "PreDepartureSafetyCheckResult",
            SafeDepartureAnswer(runtime.PreDepartureSafetyCheckId, 8, fixture.Clock.GetUtcNow()),
            runtime.PreDepartureSafetyCheckId);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
    }

    /// <summary>
    /// The other way a check expires: the safety state moves on before any answer arrives, so the
    /// check itself names a version that is no longer current. It is asked again -- but not while
    /// the vehicle is unsafe, where a new check could only be answered UNSAFE and would be asked
    /// again on every poll.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task AnUnansweredCheckIsAskedAgainOnceSafetyHasMovedOnButNotWhileTheVehicleIsUnsafe()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        string firstCheckId = runtime.PreDepartureSafetyCheckId;

        await fixture.AddSafetyStateChangedAsync(8, departureSafe: false, vehicleStopped: true);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(firstCheckId, runtime.PreDepartureSafetyCheckId);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));

        await fixture.AddSafetyStateChangedAsync(9, departureSafe: true, vehicleStopped: true);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.NotEqual(firstCheckId, runtime.PreDepartureSafetyCheckId);
        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", runtime.BlockReasonCode);
        Assert.Equal(9, await ExpectedSafetyStateVersionAsync(fixture, runtime));
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
    }

    /// <summary>
    /// An answer whose own window has closed while the safety state stayed the same is also spent,
    /// but it is only asked again once it has been stale for longer than the evidence age. The case
    /// that produces it is a departure held for another reason -- a gate the create gate refuses --
    /// where the answer lapses every couple of seconds; asking again each time would write a new
    /// check to the outbox every few seconds for as long as the hold lasts.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task AnAnswerThatOnlyRanOutOfTimeIsAskedAgainOnceItIsOlderThanTheEvidenceAge()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        string firstCheckId = runtime.PreDepartureSafetyCheckId;
        DateTimeOffset answeredAt = fixture.Clock.GetUtcNow();
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = firstCheckId,
                outcome = "SAFE",
                observedAt = answeredAt,
                safetyStateVersion = 7,
                validUntil = answeredAt.AddSeconds(2),
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
            firstCheckId);

        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal(firstCheckId, runtime.PreDepartureSafetyCheckId);
        Assert.Equal("PRE_DEPARTURE_SAFETY_NOT_VALID", runtime.BlockReasonCode);

        fixture.Clock.Advance(fixture.Options.MaximumEvidenceAge);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.NotEqual(firstCheckId, runtime.PreDepartureSafetyCheckId);
        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", runtime.BlockReasonCode);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
    }

    /// <summary>
    /// control-server#80: a block's start is recorded when its code first appears, survives every later
    /// iteration that writes the same code, and starts over when the code changes. The departure safety
    /// wait is the case that needed care -- it cleared the code before every attempt and wrote it back,
    /// which would have restarted the start on every poll of a journey that stays refused.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task ABlockKeepsTheTimeItBeganWhileItsCodeHoldsAndStartsOverWhenTheCodeChanges()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        Assert.Null(runtime.BlockReasonCode);
        Assert.Null(runtime.BlockReasonSince);
        string firstCheckId = runtime.PreDepartureSafetyCheckId;
        DateTimeOffset answeredAt = fixture.Clock.GetUtcNow();
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"), "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = firstCheckId,
                outcome = "SAFE",
                observedAt = answeredAt,
                safetyStateVersion = 7,
                validUntil = answeredAt.AddSeconds(2),
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
            firstCheckId);

        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal("PRE_DEPARTURE_SAFETY_NOT_VALID", runtime.BlockReasonCode);
        DateTimeOffset blockedAt = runtime.BlockReasonSince ?? throw new InvalidOperationException("No start recorded.");

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal("PRE_DEPARTURE_SAFETY_NOT_VALID", runtime.BlockReasonCode);
        Assert.Equal(fixture.Clock.GetUtcNow(), runtime.UpdatedAt);
        Assert.Equal(blockedAt, runtime.BlockReasonSince);

        fixture.Clock.Advance(fixture.Options.MaximumEvidenceAge);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        runtime = await fixture.RuntimeAsync();
        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", runtime.BlockReasonCode);
        Assert.Equal(fixture.Clock.GetUtcNow(), runtime.BlockReasonSince);
    }

    private static object SafeDepartureAnswer(string checkId, long safetyStateVersion, DateTimeOffset observedAt) => new
    {
        preDepartureSafetyCheckId = checkId,
        outcome = "SAFE",
        observedAt,
        safetyStateVersion,
        validUntil = observedAt.AddMinutes(1),
        safety = new
        {
            departureSafe = true,
            vehicleStopped = true,
            allTargetSlotsLocked = true,
            allUnlockOutputsReset = true,
            unknownPresent = false,
            reasonCodes = Array.Empty<string>()
        }
    };

    private static async Task<long> ExpectedSafetyStateVersionAsync(RuntimeFixture fixture, JourneyRuntimeRow runtime)
    {
        ProtocolOutboxRow check = await fixture.Context.ProtocolOutbox.SingleAsync(
            row => row.MessageId == runtime.PreDepartureSafetyCheckMessageId, TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(check.PayloadJson);
        JsonElement payload = document.RootElement.GetProperty("payload");
        Assert.Equal(runtime.PreDepartureSafetyCheckId, payload.GetProperty("preDepartureSafetyCheckId").GetString());
        return payload.GetProperty("expectedSafetyStateVersion").GetInt64();
    }
}
