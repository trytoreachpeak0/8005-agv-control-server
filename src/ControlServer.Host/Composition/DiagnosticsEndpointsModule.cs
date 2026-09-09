using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Composition;

/// <summary>
/// Liveness, readiness and the protocol identity this build was compiled against.
/// </summary>
internal static class DiagnosticsEndpointsModule
{
    internal static WebApplication MapControlServerDiagnostics(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (ControlServerDbContext dbContext, CancellationToken cancellationToken) =>
        {
            bool databaseAvailable = await dbContext.Database.CanConnectAsync(cancellationToken);
            bool vehicleReady = databaseAvailable && await dbContext.SessionRecoveries
                .AnyAsync(row => row.Readiness == SessionReadiness.Ready, cancellationToken);
            return vehicleReady
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new
                {
                    status = "not-ready",
                    reason = databaseAvailable ? "RECOVERY_HANDSHAKE_REQUIRED" : "DATABASE_UNAVAILABLE"
                }, statusCode: 503);
        });
        app.MapGet("/version", () => Results.Ok(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolTag = ProtocolCandidateIdentity.Tag,
            protocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
            manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            schemaBundleSha256 = ProtocolCandidateIdentity.SchemaBundleSha256,
            vectorsSha256 = ProtocolCandidateIdentity.VectorsSha256,
            approvalStatus = ProtocolCandidateIdentity.ApprovalStatus
        }));
        return app;
    }
}
