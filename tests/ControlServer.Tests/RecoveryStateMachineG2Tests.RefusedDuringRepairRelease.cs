using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// control-server#480 (review of #482): where the inbound refusal of control-server#478 meets the repair release of
/// control-server#385 on batch-p3/v3. Here rather than in <see cref="RefusedSafetyBaselineTests"/> because the repair
/// release's fixtures (<c>HoldTheVehicleAsync</c>, <c>ReleaseAction</c>, <c>SlotReadings</c>, ...) are this class's.
/// </summary>
public sealed partial class RecoveryStateMachineG2Tests
{
    /// <summary>
    /// A refused message is not a taken one: the first message after a reconnect that the server refuses must not spend the
    /// connection's one look for the readings a repair release waits for (<c>ReleaseReadingsRequested</c>), so the next
    /// accepted message still asks for them.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ARefusedFirstMessageAfterAReconnectDoesNotSpendTheReleaseReadingsRequest()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CS480_REFUSED_FIRST";
        const string proof = "cs480-refused-first-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots));
            await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, RecoverySlots));
            await ExchangeAsync(processor, peer, state,
                ReleaseRecord("e3850000-0000-4000-8000-000000000911", sessionId, RecoverySlots));

            state = CurrentState(deferOutbound: true);
            List<string> handshake = [.. await ReconnectAsync(processor, peer, state)];
            await FinishHandshakeAsync(processor, peer, state, handshake);
            long generation = state.SessionGeneration!.Value;

            // The release action's messageId again, with other content: refused, the connection stays.
            JsonNode conflicting = JsonNode.Parse(ReleaseAction(sessionId, RecoverySlots))!;
            conflicting["payload"]!["reason"] = "Something else entirely.";
            string[] refused = await ExchangeAsync(
                processor, peer, state, InSession(conflicting.ToJsonString(), generation));
            Assert.Equal("ProtocolProblem", MessageType(refused[0]));
            Assert.Equal("MESSAGE_ID_CONTENT_CONFLICT",
                PayloadOf(refused[0]).GetProperty("problem").GetProperty("reasonCode").GetString());
            Assert.DoesNotContain(refused, line => MessageType(line) == "SafetyStateSnapshotRequested");

            string[] first = await ExchangeAsync(processor, peer, state, InSession(Envelope(
                "e3850000-0000-4000-8000-000000000912", "Heartbeat", new { sequence = 1 }), generation));
            string request = Assert.Single(first, line => MessageType(line) == "SafetyStateSnapshotRequested");
            Assert.Equal("PRE_MOVEMENT_RECONCILIATION", PayloadOf(request).GetProperty("reason").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A SAFE HOLD_RELEASE answer lifts the door hold even while a refused safety snapshot leaves the baseline untrusted,
    /// but the release does not make the vehicle Ready: the session stays RecoveryRequired / DEPARTURE_SAFETY_NOT_READY
    /// until an accepted SafetyStateSnapshot trusts the baseline again (control-server#478).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ASafeHoldReleaseAnswerAfterARefusedSnapshotLiftsTheHoldButNotReadiness()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CS480_REFUSED_SNAPSHOT";
        const string proof = "cs480-refused-snapshot-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots));
            await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, RecoverySlots));
            await ExchangeAsync(processor, peer, state,
                ReleaseRecord("e3850000-0000-4000-8000-000000000921", sessionId, RecoverySlots));
            string[] readings = await ExchangeAsync(
                processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000922", 8));
            string check = Assert.Single(readings, line => MessageType(line) == "PreDepartureSafetyCheck");

            // A regressed snapshot: refused, and the safety baseline is no longer trusted.
            string[] refused = await ExchangeAsync(
                processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000923", 7));
            Assert.Equal("ProtocolProblem", MessageType(refused[0]));
            Assert.True(state.SafetyBaselineUntrusted);

            string[] answered = await ExchangeAsync(
                processor, peer, state, HoldReleaseCheckResult("e3850000-0000-4000-8000-000000000924", check));
            Assert.Equal("DurableAck", MessageType(answered[0]));
            context.ChangeTracker.Clear();
            SlotDoorHoldRow[] holds = await context.SlotDoorHolds.AsNoTracking().ToArrayAsync(token);
            Assert.NotEmpty(holds);
            Assert.All(holds, hold => Assert.NotNull(hold.ReleasedAt));
            SessionRecoveryRow row = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal((SessionReadiness.RecoveryRequired, "DEPARTURE_SAFETY_NOT_READY"), (row.Readiness, row.ReasonCode));
            Assert.Null(row.DepartureSafe);
            foreach (string line in answered.Where(line => MessageType(line) == "VehicleBusinessStateSnapshot"))
            {
                Assert.NotEqual("READY", PayloadOf(line).GetProperty("readiness").GetString());
            }
            foreach (string line in answered.Where(line => MessageType(line) == "SessionReadiness"))
            {
                Assert.NotEqual("READY", PayloadOf(line).GetProperty("readiness").GetString());
            }
            Assert.True(state.SafetyBaselineUntrusted);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }
}
