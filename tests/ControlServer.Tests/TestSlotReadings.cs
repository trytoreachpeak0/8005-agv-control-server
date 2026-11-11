using System.Text.Json;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// The vehicle's slot readings as the server holds them: a <c>SafetyStateSnapshot</c> in the inbox, as the onboard sends at the
/// handshake and in answer to <c>SafetyStateSnapshotRequested</c>.
/// </summary>
/// <remarks>
/// A cancellation once loading has started is authorized only on readings of this session, taken since the load began, that prove
/// every target slot empty, locked and reset (control-server#837). A test whose rig never had the vehicle report its slots, but
/// whose cancellation is meant to be authorized, seeds them with this.
/// </remarks>
internal static class TestSlotReadings
{
    /// <summary>
    /// A snapshot of the vehicle's current session, every slot empty, locked and reset, at the version after the session's last
    /// safety revision, so no change already on file reads as later than it.
    /// </summary>
    public static async Task SeedProvenEmptyAsync(ControlServerDbContext context, string agvId, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(context);
        SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking()
            .SingleAsync(row => row.AgvId == agvId, TestContext.Current.CancellationToken);
        await SeedSnapshotAsync(context, agvId, session.SessionGeneration, receivedAt, (session.SafetyRevision ?? 0) + 1);
    }

    public static async Task SeedSnapshotAsync(
        ControlServerDbContext context,
        string agvId,
        long sessionGeneration,
        DateTimeOffset receivedAt,
        long safetyStateVersion = 7,
        params int[] occupied)
    {
        ArgumentNullException.ThrowIfNull(context);
        string messageId = Guid.NewGuid().ToString("D");
        string json = JsonSerializer.Serialize(new
        {
            messageType = "SafetyStateSnapshot",
            messageId,
            agvId,
            sessionGeneration,
            payload = new
            {
                safetyStateVersion,
                observedAt = receivedAt,
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
                    physicalState = occupied.Contains(slot) ? "OCCUPIED" : "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                })
            }
        });
        context.ProtocolInbox.Add(new ProtocolInboxRow
        {
            MessageId = messageId,
            MessageType = "SafetyStateSnapshot",
            RequestJson = json,
            ContentHash = new string('5', 64),
            FirstResponseJson = string.Empty,
            ReceivedAt = receivedAt
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
