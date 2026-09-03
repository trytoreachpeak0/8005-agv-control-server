using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ControlServer.Domain;

/// <summary>
/// The content hash a recovery authorization command carries. The onboard side compares the value
/// it was sent against its own persisted context before it touches a door, and the server
/// recomputes it from the result that comes back, so the formula needs exactly one definition —
/// two would drift and the drift would read as a rejected recovery on site.
/// </summary>
public static class RecoveryCommandHash
{
    public static string Compute(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts))))
            .ToLowerInvariant();

    /// <summary>
    /// The hash bound to a command issued out of an exception recovery session: which
    /// authorization, which demand, which slot operation attempt, which slots, and at which forced
    /// recovery generation.
    /// </summary>
    public static string ForRecoveryAction(
        string workflowId,
        string demandId,
        string slotOperationAttemptId,
        string slotsJson,
        long forcedRecoveryGeneration) =>
        Compute(
            workflowId,
            demandId,
            slotOperationAttemptId,
            slotsJson,
            forcedRecoveryGeneration.ToString(CultureInfo.InvariantCulture));
}
