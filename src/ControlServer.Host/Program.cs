using ControlServer.Domain;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "8005 AGV ControlServer");
builder.WebHost.UseUrls("http://127.0.0.1:58005");

WebApplication app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Json(new { status = "not-ready", reason = "MVP slices are not implemented" }, statusCode: 503));
app.MapGet("/version", () => Results.Ok(new
{
    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
    profileId = ProtocolCandidateIdentity.ProfileId,
    protocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
    manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
    approvalStatus = ProtocolCandidateIdentity.ApprovalStatus
}));

await app.RunAsync();

public partial class Program;
