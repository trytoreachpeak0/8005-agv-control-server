using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// REQ-0359 on the server (control-server#383): an administrator declares the slot a vehicle is waiting on faulty; the server
/// judges what it can, writes the declaration and its <c>SlotFaultDeclarationCommand</c> in one save, sends it, and takes the
/// vehicle's <c>SlotFaultDeclarationResult</c>.
/// </summary>
/// <remarks>
/// <para>
/// The vehicle is driven through <see cref="OnboardMessageProcessor"/> as lines on the wire -- handshake, the overdue alarm,
/// a mid-session safety snapshot, the result, the <c>OperationResult</c> -- so the declaration is judged against state the
/// server built itself. Only the journey and its load operation are written by hand, in the shape acceptance and the runtime
/// leave them while a load waits for its result.
/// </para>
/// <para>
/// The <c>OperationResult</c> the vehicle sends after an applied declaration is settled by the path every
/// <c>UNKNOWN</c> result takes (<c>WireToGateStore.ApplyOperationResultAsync</c>): the operation and the demand go to
/// <c>RecoveryRequired</c>, and the runtime blocks the journey on its next round (<c>LOAD_RESULT_REQUIRES_RECOVERY</c>),
/// which the synthetic L2 scenario <c>slot-fault-declaration</c> shows on a running server.
/// </para>
/// </remarks>
public sealed class SlotFaultDeclarationTests
{
    private const string AgvId = "AGV-001";
    private const string VehicleKey = "VEHICLE-001";
    private const string DemandId = "10000000-0000-4000-8000-000000000383";
    private const string AttemptId = "20000000-0000-4000-8000-000000000383";
    private const string SessionCredentialVariable = "CONTROL_SERVER_TEST_SLOT_FAULT_SESSION_CREDENTIAL";
    private const string SessionCredential = "test-credential-not-for-production";
    private const string DeclarationCredential = "slot-fault-declaration-credential";
    private const string OverdueAlarmId = "00000000-0000-4000-8000-00000000a383";
    private const string OperatorId = "maintenance-383";

    private static readonly DateTimeOffset Now = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // --- The entry point ---------------------------------------------------------------------------------------------

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnUnpopulatedCredentialVariableMakesTheEntryPointUnavailable()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();

        var result = await fixture.PostAsync(
            Request(), "Bearer anything", credentialVariable: "CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N"));

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Empty(await fixture.DeclarationsAsync());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AWrongBearerCredentialIsRefusedBeforeAnythingIsWritten()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();

        var result = await fixture.PostAsync(Request(), "Bearer wrong-credential");

        Assert.IsType<UnauthorizedHttpResult>(result.Result);
        Assert.Empty(await fixture.DeclarationsAsync());
        Assert.Empty(await fixture.CommandsAsync());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AVehicleThisServerDoesNotDriveIsNotFound()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();

        var result = await fixture.PostAsync(Request() with { AgvId = "AGV-SOMEONE-ELSE" });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Empty(await fixture.DeclarationsAsync());
    }

    /// <summary>
    /// Nothing to judge: a field missing or outside what the protocol lets the command carry is a 422, refused before a
    /// single fact is read. The category and the role are the command's own enumerations, and the note must not be empty.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("requestId")]
    [InlineData("requestIdNotAUuid")]
    [InlineData("agvId")]
    [InlineData("slotNo")]
    [InlineData("slotNoZero")]
    [InlineData("faultCategory")]
    [InlineData("faultCategoryLowercase")]
    [InlineData("note")]
    [InlineData("operatorId")]
    [InlineData("administratorRole")]
    [InlineData("administratorRoleOperator")]
    public async Task AnIncompleteRequestIsUnprocessable(string defect)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationHttpRequest request = defect switch
        {
            "requestId" => Request() with { RequestId = null },
            "requestIdNotAUuid" => Request() with { RequestId = "declaration-1" },
            "agvId" => Request() with { AgvId = " " },
            "slotNo" => Request() with { SlotNo = null },
            "slotNoZero" => Request() with { SlotNo = 0 },
            "faultCategory" => Request() with { FaultCategory = null },
            "faultCategoryLowercase" => Request() with { FaultCategory = "lock" },
            "note" => Request() with { Note = "  " },
            "operatorId" => Request() with { OperatorId = "" },
            "administratorRole" => Request() with { AdministratorRole = null },
            "administratorRoleOperator" => Request() with { AdministratorRole = "OPERATOR" },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

        var result = await fixture.PostAsync(request);

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
        Assert.Empty(await fixture.DeclarationsAsync());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ASlotWhoseExpectedActionIsNotOverdueCannotBeDeclared()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync(overdueSlot: null);

        var result = await fixture.PostAsync(Request());

        Assert.Equal([SlotFaultDeclarationRefusals.ExpectedActionNotOverdue], Conflict(result));
        Assert.Empty(await fixture.DeclarationsAsync());
        Assert.Empty(await fixture.CommandsAsync());
    }

    /// <summary>
    /// REQ-0357 opens one slot at a time and the overdue alarm names it. Slot 2 is a target of the same operation, but the
    /// vehicle is waiting on slot 1: what the administrator saw at slot 2 is not what the vehicle is stuck on.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ASlotOtherThanTheOneTheVehicleIsWaitingOnCannotBeDeclared()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();

        var result = await fixture.PostAsync(Request() with { SlotNo = 2 });

        Assert.Equal([SlotFaultDeclarationRefusals.NotTheCurrentSlot], Conflict(result));
        Assert.Empty(await fixture.DeclarationsAsync());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ASlotOutsideTheOperationCannotBeDeclared()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();

        var result = await fixture.PostAsync(Request() with { SlotNo = 5 });

        Assert.Equal(
            [SlotFaultDeclarationRefusals.NotTheCurrentSlot, SlotFaultDeclarationRefusals.SlotNotInOperation],
            Conflict(result));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AVehicleWithNoSlotOperationInProgressCannotBeDeclared()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync(seedOperation: false);

        var result = await fixture.PostAsync(Request());

        Assert.Equal([SlotFaultDeclarationRefusals.NoSlotOperationInProgress], Conflict(result));
    }

    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData(StationOperationStatus.Committed)]
    [InlineData(StationOperationStatus.Failed)]
    [InlineData(StationOperationStatus.Cancelled)]
    public async Task AnOperationThatHasClosedCannotBeDeclared(StationOperationStatus closed)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        await fixture.SetOperationStatusAsync(closed);

        var result = await fixture.PostAsync(Request());

        Assert.Equal([SlotFaultDeclarationRefusals.OperationAlreadyClosed], Conflict(result));
        Assert.Empty(await fixture.DeclarationsAsync());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnOperationAlreadyJudgedUnknownCannotBeDeclaredAgain()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        await fixture.SetOperationStatusAsync(StationOperationStatus.RecoveryRequired);

        var result = await fixture.PostAsync(Request());

        Assert.Equal([SlotFaultDeclarationRefusals.OperationAlreadyUnknown], Conflict(result));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ASecondDeclarationWhileTheFirstAwaitsTheVehicleIsRefusedAndQueuesNoSecondCommand()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        Assert.IsType<Accepted<SlotFaultDeclarationResponse>>((await fixture.PostAsync(Request())).Result);

        var second = await fixture.PostAsync(Request() with { RequestId = Guid.NewGuid().ToString("D"), Note = "second look" });

        Assert.Equal([SlotFaultDeclarationRefusals.DeclarationPending], Conflict(second));
        Assert.Single(await fixture.DeclarationsAsync());
        Assert.Single(await fixture.CommandsAsync());
    }

    /// <summary>A refusal lists every reason at once, so the administrator learns all of what is missing.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ARefusalListsEveryReasonThatApplies()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync(overdueSlot: null);
        await fixture.SetOperationStatusAsync(StationOperationStatus.Committed);

        var result = await fixture.PostAsync(Request());

        Assert.Equal(
            [SlotFaultDeclarationRefusals.ExpectedActionNotOverdue, SlotFaultDeclarationRefusals.OperationAlreadyClosed],
            Conflict(result));
    }

    /// <summary>
    /// A real onboard is not READY while it holds an order of this server's (memory real-onboard-not-ready-while-driving);
    /// the synthetic peer always is. The declaration does not read readiness at all, and this pins it: the vehicle is
    /// waiting at a station with the session held for recovery, and the declaration still goes through.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ASessionHeldUnreadyByThisServersOwnOrderDoesNotStopADeclaration()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        await fixture.HoldSessionUnreadyAsync();

        var result = await fixture.PostAsync(Request());

        Assert.IsType<Accepted<SlotFaultDeclarationResponse>>(result.Result);
    }

    // --- Persisted, then sent ----------------------------------------------------------------------------------------

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnAcceptedDeclarationIsPersistedWithItsCommandAndTheCommandGoesOutAsStored()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();

        var result = await fixture.PostAsync(Request());

        SlotFaultDeclarationResponse body = Assert.IsType<Accepted<SlotFaultDeclarationResponse>>(result.Result).Value!;
        SlotFaultDeclarationRow declaration = Assert.Single(await fixture.DeclarationsAsync());
        ProtocolOutboxRow command = Assert.Single(await fixture.CommandsAsync());
        Assert.Equal(declaration.DeclarationId, body.DeclarationId);
        Assert.Equal(SlotFaultDeclarationStates.Pending, body.State);
        Assert.True(body.SentToVehicle);
        // The server fills demand and attempt from the operation in progress; the request carried neither.
        Assert.Equal(DemandId, body.DemandId);
        Assert.Equal(AttemptId, body.SlotOperationAttemptId);
        // messageId derived from the declaration, not from the attempt (memory outbox-message-id-unique).
        Assert.Equal(JourneyPlanBuilder.StableGuid(declaration.DeclarationId, "slot-fault-declaration-command"), command.MessageId);
        Assert.Equal(command.MessageId, declaration.CommandMessageId);

        string sent = Assert.Single(fixture.Peer.Lines);
        Assert.Equal(command.PayloadJson, sent.TrimEnd('\n'));
        using JsonDocument wire = JsonDocument.Parse(command.PayloadJson);
        JsonElement root = wire.RootElement;
        Assert.Equal("SlotFaultDeclarationCommand", root.GetProperty("messageType").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("correlationId").ValueKind);
        Assert.Equal(AgvId, root.GetProperty("agvId").GetString());
        Assert.Equal(fixture.State.SessionGeneration, root.GetProperty("sessionGeneration").GetInt64());
        JsonElement payload = root.GetProperty("payload");
        Assert.Equal(declaration.DeclarationId, payload.GetProperty("declarationId").GetString());
        Assert.Equal(DemandId, payload.GetProperty("demandId").GetString());
        Assert.Equal(AttemptId, payload.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal(1, payload.GetProperty("slotNo").GetInt32());
        Assert.Equal(OperatorId, payload.GetProperty("administrator").GetProperty("operatorId").GetString());
        Assert.Equal("SESSION", payload.GetProperty("administrator").GetProperty("verificationMethod").GetString());
        Assert.Equal("MAINTENANCE_ADMINISTRATOR", payload.GetProperty("administratorRole").GetString());
        Assert.Equal("LOCK", payload.GetProperty("faultCategory").GetString());
        Assert.Equal("锁舌卡死，门推不开", payload.GetProperty("note").GetString());
        Assert.Equal(Now, payload.GetProperty("declaredAt").GetDateTimeOffset());
        Assert.Equal(9, payload.EnumerateObject().Count());
    }

    /// <summary>
    /// The crash point: the save that writes the outbox row fails. The declaration was in the same save, so nothing of it is
    /// left -- no record the vehicle will never hear about, and nothing that would hold the attempt's one pending slot.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AFailureWritingTheCommandLeavesNoDeclarationBehind()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        FailOnOutboxInsert failure = new();

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.PostWithInterceptorAsync(Request(), failure));

        Assert.True(failure.Triggered);
        Assert.Empty(await fixture.DeclarationsAsync());
        Assert.Empty(await fixture.CommandsAsync());
        Assert.Empty(fixture.Peer.Lines);
        // And the attempt is not held: the same request, retried, goes through.
        Assert.IsType<Accepted<SlotFaultDeclarationResponse>>((await fixture.PostAsync(Request())).Result);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task TheSameRequestSentAgainIsAnsweredFromTheFirstAndQueuesNoSecondCommand()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationHttpRequest request = Request();
        SlotFaultDeclarationResponse first =
            Assert.IsType<Accepted<SlotFaultDeclarationResponse>>((await fixture.PostAsync(request)).Result).Value!;

        SlotFaultDeclarationResponse again =
            Assert.IsType<Accepted<SlotFaultDeclarationResponse>>((await fixture.PostAsync(request)).Result).Value!;

        Assert.Equal(first.DeclarationId, again.DeclarationId);
        Assert.Single(await fixture.DeclarationsAsync());
        Assert.Single(await fixture.CommandsAsync());
        // A repeat sends nothing: the command is already the outbox's to deliver.
        Assert.Single(fixture.Peer.Lines);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ARequestIdReusedForAnotherRequestIsRefused()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationHttpRequest request = Request();
        await fixture.PostAsync(request);

        var reused = await fixture.PostAsync(request with { FaultCategory = "IO_MODULE" });

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(reused.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        SlotFaultDeclarationRow declaration = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal("LOCK", declaration.FaultCategory);
    }

    /// <summary>
    /// Two administrators at the same moment: both read that the attempt has no pending declaration, and both write. The
    /// second is interleaved deterministically -- it runs to completion inside the first's save -- so the first's insert
    /// is the one that meets the other's row. The database's filtered unique index is what refuses it, not a read.
    /// Run on a database the migrations built as well as on EnsureCreated, since production has the former.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoAdministratorsDeclaringTheSameSlotAtOnceAreHeldToOneDeclarationByTheDatabase(bool migrate)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync(migrate: migrate);
        RunBeforeSave other = new(async () =>
        {
            var inner = await fixture.PostAsync(Request() with
            {
                RequestId = Guid.NewGuid().ToString("D"),
                OperatorId = "system-383",
                AdministratorRole = "SYSTEM_ADMINISTRATOR"
            });
            Assert.IsType<Accepted<SlotFaultDeclarationResponse>>(inner.Result);
        });

        var result = await fixture.PostWithInterceptorAsync(Request(), other);

        Assert.True(other.Ran);
        Assert.Equal([SlotFaultDeclarationRefusals.DeclarationPending], Conflict(result));
        SlotFaultDeclarationRow declaration = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal("system-383", declaration.AdministratorId);
        Assert.Single(await fixture.CommandsAsync());
    }

    /// <summary>
    /// The migration writes the one-pending-declaration rule as a partial unique index: its SQL carries the filter.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task TheMigratedDatabaseHoldsOnePendingDeclarationPerAttemptInAPartialUniqueIndex()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync(migrate: true);
        await using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_SlotFaultDeclarations_PendingAttempt'";

        string? sql = (string?)await command.ExecuteScalarAsync(Token);

        Assert.Equal(
            "CREATE UNIQUE INDEX \"IX_SlotFaultDeclarations_PendingAttempt\" ON \"SlotFaultDeclarations\" (\"SlotOperationAttemptId\") WHERE State = 'PENDING'",
            sql);
    }

    /// <summary>
    /// The vehicle is not connected when the administrator declares. The declaration is persisted all the same and the
    /// command waits in the outbox; the reconnect's recovery report replays it once, rebound to the new session and
    /// otherwise byte for byte what was stored.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ADeclarationMadeWhileTheVehicleIsAwayIsKeptAndSentOnceWhenItReconnects()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        fixture.Peer.Connected = false;

        var result = await fixture.PostAsync(Request());

        SlotFaultDeclarationResponse body = Assert.IsType<Accepted<SlotFaultDeclarationResponse>>(result.Result).Value!;
        Assert.False(body.SentToVehicle);
        ProtocolOutboxRow stored = Assert.Single(await fixture.CommandsAsync());
        long before = fixture.State.SessionGeneration!.Value;

        fixture.Peer.Connected = true;
        await fixture.ReconnectAsync();

        string replayed = Assert.Single(fixture.Peer.Lines, line => MessageType(line) == "SlotFaultDeclarationCommand");
        using JsonDocument replayedWire = JsonDocument.Parse(replayed);
        using JsonDocument storedWire = JsonDocument.Parse(stored.PayloadJson);
        Assert.Equal(stored.MessageId, replayedWire.RootElement.GetProperty("messageId").GetString());
        Assert.True(replayedWire.RootElement.GetProperty("sessionGeneration").GetInt64() > before);
        Assert.Equal(
            storedWire.RootElement.GetProperty("payload").GetRawText(),
            replayedWire.RootElement.GetProperty("payload").GetRawText());
    }

    // --- The vehicle's answer ----------------------------------------------------------------------------------------

    /// <summary>
    /// CV-SLOT-FAULT-DECLARATION-APPLIED: the vehicle applies the declaration and then reports the operation, the declared
    /// slot <c>UNKNOWN</c> under <c>SLOT_FAULT_DECLARED</c> and the unopened one <c>NOT_STARTED</c>. The operation and the
    /// demand go to <c>RecoveryRequired</c> by the path every <c>UNKNOWN</c> result takes, and the session is told it needs
    /// recovery.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task AnAppliedDeclarationFollowedByItsUnknownResultPutsTheOperationIntoRecovery()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();

        string ack = await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "APPLIED");
        Assert.Equal(["DurableAck"], Lines(ack).Select(MessageType));
        Assert.Equal(StationOperationStatus.Prepared, (await fixture.OperationAsync()).Status);

        string answer = await fixture.SendOperationResultAsync(DeclaredUnknownResult());

        Assert.Equal("DurableAck", MessageType(Lines(answer)[0]));
        Assert.Equal(SessionReadiness.RecoveryRequired, fixture.State.Readiness);
        Assert.Equal(StationOperationStatus.RecoveryRequired, (await fixture.OperationAsync()).Status);
        Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await fixture.DemandAsync()).Status);
        SlotFaultDeclarationRow after = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal(SlotFaultDeclarationStates.Applied, after.State);
        Assert.Equal("APPLIED", after.ResultOutcome);
        Assert.Null(after.ResultProblemJson);
        Assert.Single(await fixture.OperationResultsAsync());
    }

    /// <summary>The two messages in the other order: the result settles the operation once, the answer records only.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task AnUnknownResultArrivingBeforeTheAppliedAnswerSettlesTheOperationOnce()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();

        await fixture.SendOperationResultAsync(DeclaredUnknownResult());
        string picture = await fixture.BusinessPictureAsync();
        string ack = await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "APPLIED");

        Assert.Equal(["DurableAck"], Lines(ack).Select(MessageType));
        Assert.Equal(picture, await fixture.BusinessPictureAsync());
        Assert.Equal(StationOperationStatus.RecoveryRequired, (await fixture.OperationAsync()).Status);
        Assert.Equal(SlotFaultDeclarationStates.Applied, Assert.Single(await fixture.DeclarationsAsync()).State);
        Assert.Single(await fixture.OperationResultsAsync());
    }

    /// <summary>
    /// CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE: the operator closed the door just as the administrator declared, the
    /// vehicle refuses the declaration, and the server withdraws it -- demand, operation and journey unchanged column for
    /// column, the refusal and its reason recorded.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ARefusedDeclarationChangesNoBusinessStateAndRecordsWhy()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        string before = await fixture.BusinessPictureAsync();

        string ack = await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "NOT_APPLICABLE");

        Assert.Equal(["DurableAck"], Lines(ack).Select(MessageType));
        Assert.Equal(before, await fixture.BusinessPictureAsync());
        SlotFaultDeclarationRow after = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal(SlotFaultDeclarationStates.NotApplicable, after.State);
        Assert.Equal("NOT_APPLICABLE", after.ResultOutcome);
        using JsonDocument problem = JsonDocument.Parse(after.ResultProblemJson!);
        Assert.Equal("ACTION_NOT_ALLOWED_IN_STATE", problem.RootElement.GetProperty("reasonCode").GetString());
        Assert.Equal(Now, after.ResultReceivedAt);
    }

    /// <summary>The refusal after the operation completed: the completion stands, and the refusal only records.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ACompletedResultBeforeTheRefusalStandsAndTheRefusalChangesNothing()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();

        await fixture.SendOperationResultAsync(CompletedResult());
        string settled = await fixture.BusinessPictureAsync();
        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "NOT_APPLICABLE");

        Assert.Equal(settled, await fixture.BusinessPictureAsync());
        Assert.Equal(StationOperationStatus.Committed, (await fixture.OperationAsync()).Status);
        Assert.Equal(SlotFaultDeclarationStates.NotApplicable, Assert.Single(await fixture.DeclarationsAsync()).State);
    }

    /// <summary>
    /// A second answer for the same declaration with the same content -- outcome, attempt and problem, both problems null
    /// counting as the same -- under a new messageId is acknowledged and changes nothing: the first answer is the record.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("APPLIED")]
    [InlineData("NOT_APPLICABLE")]
    public async Task ASecondAnswerWithTheSameContentIsAcknowledgedAndChangesNothing(string outcome)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, outcome);
        SlotFaultDeclarationRow first = Assert.Single(await fixture.DeclarationsAsync());
        string picture = await fixture.BusinessPictureAsync();

        string answer = await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, outcome);

        Assert.Equal(["DurableAck"], Lines(answer).Select(MessageType));
        SlotFaultDeclarationRow after = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(after));
        Assert.Equal(picture, await fixture.BusinessPictureAsync());
    }

    /// <summary>
    /// A second answer for an answered declaration with other content is refused with <c>BUSINESS_ID_CONTENT_CONFLICT</c>
    /// and the connection stays (control-server#481). Acknowledged, the vehicle would drop its row silently while the two
    /// ends disagree about what it did; refused with one of the four content-conflict codes, the vehicle gives the row up and
    /// tells its operator (onboard-hmi#254). The declaration stays as first answered, and it is not pending, so nothing is
    /// replayed after it.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("outcome")]
    [InlineData("problem")]
    [InlineData("attempt")]
    public async Task ASecondAnswerWithOtherContentIsRefusedAsABusinessIdConflict(string differs)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "NOT_APPLICABLE");
        SlotFaultDeclarationRow first = Assert.Single(await fixture.DeclarationsAsync());
        string picture = await fixture.BusinessPictureAsync();
        string messageId = Guid.NewGuid().ToString("D");

        string answer = differs switch
        {
            "outcome" => await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "APPLIED", messageId: messageId),
            "problem" => await fixture.SendResultAsync(
                declaration.DeclarationId, AttemptId, "NOT_APPLICABLE",
                new { reasonCode = "ACTION_NOT_ALLOWED_IN_STATE", fieldPath = "payload.slotNo", displayMessage = "另一个原因" },
                messageId),
            _ => await fixture.SendResultAsync(
                declaration.DeclarationId, "20000000-0000-4000-8000-00000000ffff", "NOT_APPLICABLE", messageId: messageId)
        };

        AssertRefused(answer, messageId, ServerReasonCodes.BusinessIdContentConflict);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(Assert.Single(await fixture.DeclarationsAsync())));
        Assert.Equal(picture, await fixture.BusinessPictureAsync());
        fixture.Peer.Lines.Clear();
        await fixture.ReconnectAsync();
        Assert.DoesNotContain(fixture.Peer.Lines, line => MessageType(line) == "SlotFaultDeclarationCommand");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnAnswerForADeclarationThisServerNeverMadeIsAcknowledgedAndChangesNothing()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        string picture = await fixture.BusinessPictureAsync();

        string ack = await fixture.SendResultAsync(Guid.NewGuid().ToString("D"), AttemptId, "APPLIED");

        Assert.Equal(["DurableAck"], Lines(ack).Select(MessageType));
        Assert.Equal(picture, await fixture.BusinessPictureAsync());
        Assert.Equal(SlotFaultDeclarationStates.Pending, Assert.Single(await fixture.DeclarationsAsync()).State);
        _ = declaration;
    }

    /// <summary>
    /// An answer naming another attempt than the declaration's is refused with <c>BUSINESS_ID_CONTENT_CONFLICT</c>
    /// (control-server#481), and the declaration stays pending: the refusal keeps nothing of the message. What closes it is
    /// the vehicle refusing the replayed command once it has given the answer up -- see
    /// <see cref="AVehicleRefusingTheReplayedCommandClosesTheDeclarationAsUnreconciled"/>.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnAnswerNamingAnotherAttemptIsRefusedAndLeavesTheDeclarationPending()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        string picture = await fixture.BusinessPictureAsync();
        string messageId = Guid.NewGuid().ToString("D");

        string answer = await fixture.SendResultAsync(
            declaration.DeclarationId, "20000000-0000-4000-8000-00000000ffff", "APPLIED", messageId: messageId);

        AssertRefused(answer, messageId, ServerReasonCodes.BusinessIdContentConflict);
        Assert.Equal(picture, await fixture.BusinessPictureAsync());
        SlotFaultDeclarationRow after = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal(SlotFaultDeclarationStates.Pending, after.State);
        Assert.Null(after.ResultOutcome);
    }

    /// <summary>
    /// An answer naming a declaration this server made for another vehicle is refused with
    /// <c>BUSINESS_ID_CONTENT_CONFLICT</c>, not acknowledged as unknown (control-server#481, the same line as the review of
    /// part one drew for another vehicle's recovery workflow, S2): the id is known, it is just not this vehicle's. The other
    /// vehicle's declaration is left as it was.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnAnswerNamingAnotherVehiclesDeclarationIsRefusedAndLeavesItAsItWas()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow other = await fixture.SeedOtherVehiclesDeclarationAsync();
        string picture = await fixture.BusinessPictureAsync();
        string messageId = Guid.NewGuid().ToString("D");

        string answer = await fixture.SendResultAsync(other.DeclarationId, AttemptId, "APPLIED", messageId: messageId);

        AssertRefused(answer, messageId, ServerReasonCodes.BusinessIdContentConflict);
        Assert.Equal(picture, await fixture.BusinessPictureAsync());
        Assert.Equal(
            JsonSerializer.Serialize(other),
            JsonSerializer.Serialize(Assert.Single(await fixture.DeclarationsAsync())));
    }

    // --- The close-out: the vehicle gave its answer up (control-server#481) -----------------------------------------

    /// <summary>
    /// Once the server has refused a declaration's answer for good, the vehicle gives the row up (onboard-hmi#254) and
    /// refuses the command the server replays with a <c>ProtocolProblem</c> correlated to it, code
    /// <c>SLOT_OPERATION_CONFLICT</c> (onboard-hmi#266). The server takes that as the end of waiting: the declaration becomes
    /// <c>UNRECONCILED</c> with the refusal recorded, its command is settled, it is not replayed again, and the attempt can
    /// be declared again -- no state that only a database edit gets out of. Nothing of the business changes.
    /// </summary>
    /// <remarks>
    /// Two ways the answer is refused, both ending here: the server's own branch (the answer names another attempt), and the
    /// inbox (the same messageId as a line already taken, with other content -- a vehicle whose journal was replaced).
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("attempt-mismatch")]
    [InlineData("message-id-conflict")]
    public async Task AVehicleRefusingTheReplayedCommandClosesTheDeclarationAsUnreconciled(string refusedBy)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        string picture = await fixture.BusinessPictureAsync();
        string messageId = Guid.NewGuid().ToString("D");
        if (refusedBy == "attempt-mismatch")
        {
            AssertRefused(
                await fixture.SendResultAsync(
                    declaration.DeclarationId, "20000000-0000-4000-8000-00000000ffff", "APPLIED", messageId: messageId),
                messageId,
                ServerReasonCodes.BusinessIdContentConflict);
        }
        else
        {
            Assert.Equal(
                ["DurableAck"],
                Lines(await fixture.SendResultAsync(Guid.NewGuid().ToString("D"), AttemptId, "APPLIED", messageId: messageId))
                    .Select(MessageType));
            AssertRefused(
                await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "APPLIED", messageId: messageId),
                messageId,
                ServerReasonCodes.MessageIdContentConflict);
        }
        Assert.Equal(SlotFaultDeclarationStates.Pending, Assert.Single(await fixture.DeclarationsAsync()).State);

        // The vehicle reconnects, the command is replayed, and the vehicle -- its answer given up -- refuses it.
        fixture.Peer.Lines.Clear();
        await fixture.ReconnectAsync();
        Assert.Contains(fixture.Peer.Lines, line => MessageType(line) == "SlotFaultDeclarationCommand");
        string refusalId = Guid.NewGuid().ToString("D");
        string answer = await fixture.RefuseCommandAsync(
            declaration.CommandMessageId, ServerReasonCodes.SlotOperationConflict, refusalId);

        Assert.Empty(Lines(answer));
        SlotFaultDeclarationRow after = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal(SlotFaultDeclarationStates.Unreconciled, after.State);
        Assert.Null(after.ResultOutcome);
        Assert.Equal(refusalId, after.ResultMessageId);
        Assert.Equal(Now, after.ResultReceivedAt);
        using (JsonDocument problem = JsonDocument.Parse(after.ResultProblemJson!))
        {
            Assert.Equal(ServerReasonCodes.SlotOperationConflict, problem.RootElement.GetProperty("reasonCode").GetString());
        }
        Assert.Equal(Now, Assert.Single(await fixture.CommandsAsync()).AcknowledgedAt);
        Assert.Equal(picture, await fixture.BusinessPictureAsync());

        fixture.Peer.Lines.Clear();
        await fixture.ReconnectAsync();
        Assert.DoesNotContain(fixture.Peer.Lines, line => MessageType(line) == "SlotFaultDeclarationCommand");
        Assert.IsType<Accepted<SlotFaultDeclarationResponse>>(
            (await fixture.PostAsync(Request() with { RequestId = Guid.NewGuid().ToString("D") })).Result);
    }

    /// <summary>
    /// The give-up as onboard-hmi#266 sends it: the moment its answer is refused, the vehicle refuses the command it has at
    /// hand, in the same session, without waiting for a reconnect to replay it. The refusal of the answer kept nothing, so
    /// the declaration is still pending when the command's refusal arrives; it becomes <c>UNRECONCILED</c>, its command is
    /// settled, and the next reconnect does not replay it.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("attempt-mismatch")]
    [InlineData("message-id-conflict")]
    public async Task ARefusalOfTheCommandInTheSameSessionAsTheRefusedAnswerClosesTheDeclaration(string refusedBy)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        long? generation = fixture.State.SessionGeneration;
        string picture = await fixture.BusinessPictureAsync();
        string messageId = Guid.NewGuid().ToString("D");
        if (refusedBy == "attempt-mismatch")
        {
            AssertRefused(
                await fixture.SendResultAsync(
                    declaration.DeclarationId, "20000000-0000-4000-8000-00000000ffff", "APPLIED", messageId: messageId),
                messageId,
                ServerReasonCodes.BusinessIdContentConflict);
        }
        else
        {
            await fixture.SendResultAsync(Guid.NewGuid().ToString("D"), AttemptId, "APPLIED", messageId: messageId);
            AssertRefused(
                await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "APPLIED", messageId: messageId),
                messageId,
                ServerReasonCodes.MessageIdContentConflict);
        }
        Assert.Equal(SlotFaultDeclarationStates.Pending, Assert.Single(await fixture.DeclarationsAsync()).State);
        Assert.Null(Assert.Single(await fixture.CommandsAsync()).AcknowledgedAt);

        string answer = await fixture.RefuseCommandAsync(declaration.CommandMessageId, ServerReasonCodes.SlotOperationConflict);

        Assert.Equal(generation, fixture.State.SessionGeneration);
        Assert.Empty(Lines(answer));
        Assert.Equal(SlotFaultDeclarationStates.Unreconciled, Assert.Single(await fixture.DeclarationsAsync()).State);
        Assert.Equal(Now, Assert.Single(await fixture.CommandsAsync()).AcknowledgedAt);
        Assert.Equal(picture, await fixture.BusinessPictureAsync());
        fixture.Peer.Lines.Clear();
        await fixture.ReconnectAsync();
        Assert.DoesNotContain(fixture.Peer.Lines, line => MessageType(line) == "SlotFaultDeclarationCommand");
    }

    /// <summary>
    /// The give-up still lands when there is no command line left to settle (review of control-server#481, S2): the line was
    /// settled already, or is gone, and the settle returns without saving. Without a save the declaration would read
    /// <c>PENDING</c> again, and its command -- replayed from the declaration, not from the line -- would come back on the next
    /// reconnect.
    /// </summary>
    /// <remarks>
    /// Two cells per line state. <c>wire</c> goes through the processor, where the inbox transaction's own save
    /// (<c>WireToGateStore.CaptureFirstResponseAsync</c>, same context) also carries the tracked declaration, so the observer's
    /// save is not what this cell depends on. <c>direct</c> calls <see cref="SlotFaultDeclarationResults.ObserveCommandRefusedAsync"/>
    /// itself, outside any inbox transaction: its contract is that it saves what it changed, and only that cell fails when the
    /// save after the settle is removed (mutation R13).
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("settled", "wire")]
    [InlineData("gone", "wire")]
    [InlineData("settled", "direct")]
    [InlineData("gone", "direct")]
    public async Task AGiveUpIsRecordedWhenTheCommandLineIsAlreadySettledOrGone(string line, string path)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        DateTimeOffset? settledAt = line == "settled" ? Now.AddMinutes(-1) : null;
        if (line == "settled")
        {
            await fixture.SettleCommandsAsync(Now.AddMinutes(-1));
        }
        else
        {
            await fixture.RemoveCommandsAsync();
        }

        if (path == "wire")
        {
            await fixture.RefuseCommandAsync(declaration.CommandMessageId, ServerReasonCodes.SlotOperationConflict);
        }
        else
        {
            using JsonDocument problem = JsonDocument.Parse(
                $$"""{"reasonCode":"{{ServerReasonCodes.SlotOperationConflict}}","fieldPath":null,"displayMessage":"本车已放弃对这项判定的应答"}""");
            Assert.True(await SlotFaultDeclarationResults.ObserveCommandRefusedAsync(
                fixture.Context, new WireToGateStore(fixture.Context), AgvId, Guid.NewGuid().ToString("D"),
                declaration.CommandMessageId, problem.RootElement, Now, NullLogger.Instance, Token));
        }

        SlotFaultDeclarationRow after = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal(SlotFaultDeclarationStates.Unreconciled, after.State);
        Assert.Equal(Now, after.ResultReceivedAt);
        Assert.Equal(settledAt, (await fixture.CommandsAsync()).SingleOrDefault()?.AcknowledgedAt);
    }

    /// <summary>
    /// The way out of an <c>UNRECONCILED</c> declaration, on the wire only (control-server#481): declared again, the vehicle
    /// -- which journals a declaration only when it applies it -- judges the new one afresh; with the slot still waiting it
    /// applies it, stops the operation and reports the slot <c>UNKNOWN</c>, and the operation goes to recovery as after any
    /// applied declaration. The way out is the operation's result, as for an applied declaration, not a load cancellation.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnUnreconciledDeclarationIsLeftByDeclaringAgainAndTheOperationsUnknownResult()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow first = await fixture.UnreconciledDeclarationAsync();

        Assert.IsType<Accepted<SlotFaultDeclarationResponse>>(
            (await fixture.PostAsync(Request() with { RequestId = Guid.NewGuid().ToString("D") })).Result);
        SlotFaultDeclarationRow second = (await fixture.DeclarationsAsync())
            .Single(row => row.State == SlotFaultDeclarationStates.Pending);
        Assert.NotEqual(first.DeclarationId, second.DeclarationId);
        Assert.Equal(["DurableAck"],
            Lines(await fixture.SendResultAsync(second.DeclarationId, AttemptId, "APPLIED")).Select(MessageType));
        string answer = await fixture.SendOperationResultAsync(DeclaredUnknownResult());

        Assert.Equal("DurableAck", MessageType(Lines(answer)[0]));
        Assert.Equal(StationOperationStatus.RecoveryRequired, (await fixture.OperationAsync()).Status);
        Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await fixture.DemandAsync()).Status);
        Assert.Equal(SessionReadiness.RecoveryRequired, fixture.State.Readiness);
        SlotFaultDeclarationRow[] declarations = await fixture.DeclarationsAsync();
        Assert.Equal(SlotFaultDeclarationStates.Unreconciled, declarations.Single(row => row.DeclarationId == first.DeclarationId).State);
        Assert.Equal(SlotFaultDeclarationStates.Applied, declarations.Single(row => row.DeclarationId == second.DeclarationId).State);
    }

    /// <summary>
    /// The guard beside that way out (control-server#481): a second declaration the vehicle answers <c>NOT_APPLICABLE</c>
    /// does not lift the hold on the attempt's load cancellation. The vehicle refuses a declaration on an attempt it already
    /// applied one to (its journaled declaration, or the executor's <c>AlreadyDeclared</c>), so that answer may mean the
    /// first one did take effect -- and a cancellation after an applied declaration is refused without a word, leaving it
    /// awaiting a result for good. The server cannot tell that refusal from the others except by its wording.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ANotApplicableSecondDeclarationDoesNotLiftTheCancellationHoldOfAnUnreconciledOne()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        await fixture.UnreconciledDeclarationAsync();
        Assert.IsType<Accepted<SlotFaultDeclarationResponse>>(
            (await fixture.PostAsync(Request() with { RequestId = Guid.NewGuid().ToString("D") })).Result);
        SlotFaultDeclarationRow second = (await fixture.DeclarationsAsync())
            .Single(row => row.State == SlotFaultDeclarationStates.Pending);
        await fixture.SendResultAsync(
            second.DeclarationId, AttemptId, "NOT_APPLICABLE",
            new { reasonCode = "ACTION_NOT_ALLOWED_IN_STATE", fieldPath = "payload.slotNo", displayMessage = "本次仓位操作已按另一项判定把1号仓报为UNKNOWN。" });
        Assert.Equal(SlotFaultDeclarationStates.NotApplicable, Assert.Single(
            await fixture.DeclarationsAsync(), row => row.DeclarationId == second.DeclarationId).State);

        string answer = await fixture.RequestLoadCancellationAsync();

        JsonElement authorization = Lines(answer).Select(line => JsonDocument.Parse(line).RootElement)
            .Single(line => line.GetProperty("messageType").GetString() == "LoadCancellationAuthorization");
        Assert.Equal("REJECTED", authorization.GetProperty("payload").GetProperty("decision").GetString());
        Assert.False(await LoadCancellationBeforeSublot.HasOpenCancellationAsync(fixture.Context, DemandId, Token));
    }

    /// <summary>
    /// Only the give-up closes a declaration, and only a pending one: a refusal of its command with another code (one that
    /// means "later", say), a <c>SLOT_OPERATION_CONFLICT</c> naming another message, or one naming another vehicle's
    /// declaration command leaves the declaration pending and its command replayed; one naming the command of a declaration
    /// already answered leaves that answer standing.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("other-code")]
    [InlineData("other-message")]
    [InlineData("other-vehicle")]
    [InlineData("answered")]
    public async Task ARefusalThatIsNotTheGiveUpLeavesTheDeclarationPending(string refusal)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = refusal == "other-vehicle"
            ? await fixture.SeedOtherVehiclesDeclarationAsync()
            : await fixture.DeclareAsync();
        if (refusal == "answered")
        {
            await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "NOT_APPLICABLE");
            declaration = Assert.Single(await fixture.DeclarationsAsync());
        }

        await (refusal switch
        {
            "other-code" => fixture.RefuseCommandAsync(declaration.CommandMessageId, ServerReasonCodes.ActionNotAllowedInState),
            "other-message" => fixture.RefuseCommandAsync(Guid.NewGuid().ToString("D"), ServerReasonCodes.SlotOperationConflict),
            _ => fixture.RefuseCommandAsync(declaration.CommandMessageId, ServerReasonCodes.SlotOperationConflict)
        });

        SlotFaultDeclarationRow after = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal(JsonSerializer.Serialize(declaration), JsonSerializer.Serialize(after));
        if (refusal is "other-code" or "other-message")
        {
            Assert.Null(Assert.Single(await fixture.CommandsAsync()).AcknowledgedAt);
            fixture.Peer.Lines.Clear();
            await fixture.ReconnectAsync();
            Assert.Contains(fixture.Peer.Lines, line => MessageType(line) == "SlotFaultDeclarationCommand");
        }
    }

    /// <summary>Once answered, the attempt is free again: the pending index only holds unanswered declarations.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnAnsweredDeclarationNoLongerHoldsTheAttempt()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "NOT_APPLICABLE");

        var again = await fixture.PostAsync(Request() with { RequestId = Guid.NewGuid().ToString("D") });

        Assert.IsType<Accepted<SlotFaultDeclarationResponse>>(again.Result);
        Assert.Equal(2, (await fixture.DeclarationsAsync()).Length);
    }

    /// <summary>An answered declaration's command is not replayed on the next reconnect.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnAnsweredDeclarationIsNotReplayedOnReconnect()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        fixture.Peer.Connected = false;
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        fixture.Peer.Connected = true;
        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "NOT_APPLICABLE");
        fixture.Peer.Lines.Clear();

        await fixture.ReconnectAsync();

        Assert.DoesNotContain(fixture.Peer.Lines, line => MessageType(line) == "SlotFaultDeclarationCommand");
    }

    // --- The command's outbox line (review of onboard-hmi#247, control-server#384) -----------------------------------

    /// <summary>
    /// The vehicle answers the command with its result, never with a DurableAck (both vectors: command, result, the server's
    /// DurableAck), so the result is what settles the command's outbox line, as for every command answered with a business
    /// result. Left unsettled, the line is under the identity of the build that wrote it forever, and the first start after a
    /// protocol identity change (control-server#393) is refused with <c>OUTBOX_PROTOCOL_IDENTITY_MISMATCH</c> -- a refusal
    /// whose way out, "let the vehicle acknowledge its outbox", the vehicle never takes.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("APPLIED")]
    [InlineData("NOT_APPLICABLE")]
    public async Task AnAnsweredDeclarationSettlesItsCommandSoTheServerStartsAfterAProtocolIdentityChange(string outcome)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();

        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, outcome);
        await fixture.WriteCommandsUnderThePreviousIdentityAsync();

        await ProtocolOutboxIdentityStartupCheck.EnsureAsync(fixture.Context, NullLogger.Instance, null, Token);
        Assert.Equal(Now, Assert.Single(await fixture.CommandsAsync()).AcknowledgedAt);
    }

    /// <summary>
    /// A resent result for a declaration already answered -- the vehicle resends until the server's DurableAck arrives --
    /// settles a command a build before this fix left unsettled. The declaration itself stays as first answered.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AResultForAnAlreadyAnsweredDeclarationSettlesACommandLeftUnsettled()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "APPLIED");
        await fixture.UnsettleCommandsAsync();
        SlotFaultDeclarationRow first = Assert.Single(await fixture.DeclarationsAsync());

        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "APPLIED");

        Assert.Equal(Now, Assert.Single(await fixture.CommandsAsync()).AcknowledgedAt);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(Assert.Single(await fixture.DeclarationsAsync())));
    }

    /// <summary>
    /// A store that took its answers before this fix: startup settles the commands of answered declarations, once and
    /// idempotently, before the identity check reads the outbox. A declaration still awaiting its answer keeps its command
    /// unsettled -- the vehicle still owes that answer, and the identity check is right to stop on it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task StartupSettlesTheCommandsOfDeclarationsAnsweredBeforeTheFixAndNoOther()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow answered = await fixture.DeclareAsync();
        await fixture.SendResultAsync(answered.DeclarationId, AttemptId, "NOT_APPLICABLE");
        await fixture.UnsettleCommandsAsync();
        await fixture.WriteCommandsUnderThePreviousIdentityAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProtocolOutboxIdentityStartupCheck.EnsureAsync(fixture.Context, NullLogger.Instance, null, Token));
        Assert.IsType<Accepted<SlotFaultDeclarationResponse>>(
            (await fixture.PostAsync(Request() with { RequestId = Guid.NewGuid().ToString("D") })).Result);
        string pendingCommand = (await fixture.DeclarationsAsync())
            .Single(row => row.State == SlotFaultDeclarationStates.Pending).CommandMessageId;

        int first = await SlotFaultDeclarationResults.SettleAnsweredCommandsAsync(fixture.Context, Now, Token);
        int second = await SlotFaultDeclarationResults.SettleAnsweredCommandsAsync(fixture.Context, Now, Token);

        Assert.Equal((1, 0), (first, second));
        ProtocolOutboxRow[] commands = await fixture.CommandsAsync();
        Assert.Equal(Now, commands.Single(row => row.MessageId == answered.CommandMessageId).AcknowledgedAt);
        Assert.Null(commands.Single(row => row.MessageId == pendingCommand).AcknowledgedAt);
        await ProtocolOutboxIdentityStartupCheck.EnsureAsync(fixture.Context, NullLogger.Instance, null, Token);
    }

    /// <summary>
    /// A result that settles no declaration of this server -- one naming another attempt, or a declaration this server never
    /// made -- settles no command either. Settled, the command's line would never be replayed again and the declaration it
    /// carries would stay PENDING for good (review of control-server#384, S2).
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("attempt-mismatch")]
    [InlineData("unknown-declaration")]
    public async Task AnAnswerThatSettlesNoDeclarationLeavesTheCommandUnsettled(string answer)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();

        await (answer == "attempt-mismatch"
            ? fixture.SendResultAsync(declaration.DeclarationId, "20000000-0000-4000-8000-00000000ffff", "APPLIED")
            : fixture.SendResultAsync(Guid.NewGuid().ToString("D"), AttemptId, "APPLIED"));

        Assert.Equal(SlotFaultDeclarationStates.Pending, Assert.Single(await fixture.DeclarationsAsync()).State);
        Assert.Null(Assert.Single(await fixture.CommandsAsync()).AcknowledgedAt);
    }

    /// <summary>
    /// The host's start order, read from <c>Program.cs</c> (review of control-server#384, S1): the backfill that settles
    /// answered declarations' commands runs after the database is migrated and before the outbox identity check, or a store
    /// that declared before this fix is refused at the first start under a new identity; the warning about declarations
    /// held while switched off runs before the check too, so a refusal is preceded by what explains it. Each call appears
    /// exactly once.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public void TheHostSettlesAnsweredDeclarationsAfterMigratingAndBeforeTheOutboxIdentityCheck()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "src", "ControlServer.Host", "Program.cs"));
        int Line(string call)
        {
            int[] found = [.. lines.Select((line, index) => (line.Trim(), index))
                .Where(item => !item.Item1.StartsWith("//", StringComparison.Ordinal)
                               && item.Item1.StartsWith("await " + call + "(app.Services", StringComparison.Ordinal))
                .Select(item => item.index)];
            return Assert.Single(found);
        }

        int migrate = Line("EnsureDatabaseAsync");
        int settle = Line("SlotFaultDeclarationResults.SettleAnsweredCommandsAsync");
        int warn = Line("SlotFaultDeclarationStartupCheck.WarnAsync");
        int identity = Line("ProtocolOutboxIdentityStartupCheck.EnsureAsync");

        Assert.True(migrate < settle, $"settle at line {settle + 1} runs before the migration at line {migrate + 1}");
        Assert.True(settle < identity, $"settle at line {settle + 1} runs after the identity check at line {identity + 1}");
        Assert.True(warn < identity, $"the warning at line {warn + 1} runs after the identity check at line {identity + 1}");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("ControlServer.sln not found above the test output.");
    }

    // --- Cancelling a declared attempt (review of onboard-hmi#247, control-server#384) -------------------------------

    /// <summary>
    /// A load cancellation is not authorized for an attempt with a declaration pending or applied: an applied declaration
    /// has already stopped the operation and sent it to recovery (its result is <c>UNKNOWN</c>), and a pending one may yet
    /// do so, so a cancellation would give the same attempt a second conclusion and drive the slot declared faulty
    /// through the cancellation flow. The wire code is the protocol's <c>ACTION_NOT_ALLOWED_IN_STATE</c> -- the registry
    /// allows <c>SLOT_FAULT_DECLARED</c> in <c>OperationResult</c> only -- and the display message says why. A declaration
    /// the vehicle refused withdraws itself and holds nothing back; with no declaration at all the same request is
    /// authorized, which pins that the refusal comes from the declaration and nothing else in the fixture. The onboard
    /// guards the same in onboard-hmi#247; this is the second line.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData(null, "AUTHORIZED")]
    [InlineData("PENDING", "REJECTED")]
    [InlineData("APPLIED", "REJECTED")]
    [InlineData("NOT_APPLICABLE", "AUTHORIZED")]
    [InlineData("UNRECONCILED", "REJECTED")]
    public async Task ALoadCancellationIsNotAuthorizedForAnAttemptWithAPendingOrAppliedDeclaration(
        string? declaration, string expected)
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        if (declaration is not null)
        {
            SlotFaultDeclarationRow declared = await fixture.DeclareAsync();
            if (declaration == "UNRECONCILED")
            {
                // The vehicle gave its answer up (control-server#481): nobody knows whether it applied the declaration, so
                // the cancellation is held back as for an applied one.
                await fixture.RefuseCommandAsync(declared.CommandMessageId, ServerReasonCodes.SlotOperationConflict);
                Assert.Equal(SlotFaultDeclarationStates.Unreconciled, Assert.Single(await fixture.DeclarationsAsync()).State);
            }
            else if (declaration != "PENDING")
            {
                await fixture.SendResultAsync(declared.DeclarationId, AttemptId, declaration);
            }
        }

        string answer = await fixture.RequestLoadCancellationAsync();

        // First, the consequence. What the runtime reads before it loads the next demand, ends the stop at its deadline or settles a determinate
        // failure (JourneyRuntimeEngine.OpenCancellationAtCurrentStopAsync and the failure settlement): authorized after a
        // declaration, the onboard never answers this cancellation, and the stop would stay held by it for good.
        Assert.Equal(
            expected == "AUTHORIZED",
            await LoadCancellationBeforeSublot.HasOpenCancellationAsync(fixture.Context, DemandId, Token));
        JsonElement authorization = Lines(answer).Select(line => JsonDocument.Parse(line).RootElement)
            .Single(line => line.GetProperty("messageType").GetString() == "LoadCancellationAuthorization");
        JsonElement payload = authorization.GetProperty("payload");
        Assert.Equal(expected, payload.GetProperty("decision").GetString());
        bool workflowRecorded = await fixture.Context.RecoveryWorkflows.AsNoTracking()
            .AnyAsync(row => row.WorkflowType == "LOAD_CANCELLATION", Token);
        Assert.Equal(expected == "AUTHORIZED", workflowRecorded);
        if (expected == "REJECTED")
        {
            JsonElement problem = payload.GetProperty("problem");
            Assert.Equal(ServerReasonCodes.ActionNotAllowedInState, problem.GetProperty("reasonCode").GetString());
            Assert.Equal(OnboardRecoveryCoordinator.SlotFaultDeclaredCancellationMessage,
                problem.GetProperty("displayMessage").GetString());
        }
    }

    /// <summary>
    /// The other order (control-server#384): a load cancellation of the attempt is already authorized and waiting for the
    /// vehicle's result, so the cancellation is how this operation ends. A declaration is refused with a reason the
    /// administrator sees on submitting, and nothing is written -- no declaration row, no command.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ADeclarationOnAnAttemptWhoseLoadCancellationIsAuthorizedIsRefusedAndWritesNothing()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        await fixture.RequestLoadCancellationAsync();
        Assert.True(await LoadCancellationBeforeSublot.HasOpenCancellationAsync(fixture.Context, DemandId, Token));

        var result = await fixture.PostAsync(Request());

        Assert.Equal([SlotFaultDeclarationRefusals.LoadCancellationInProgress], Conflict(result));
        Assert.Empty(await fixture.DeclarationsAsync());
        Assert.Empty(await fixture.CommandsAsync());
        Assert.DoesNotContain(fixture.Peer.Lines, line => MessageType(line) == "SlotFaultDeclarationCommand");
    }

    // --- The switch (review of control-server#383, S1 and S2) ---------------------------------------------------------

    /// <summary>
    /// Switched on, then off again with a declaration still unanswered: the reconnect does not replay its command. An onboard
    /// that does not know the command would otherwise be dropped on every reconnect.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ASwitchedOffEntryPointDoesNotReplayAPendingDeclarationOnReconnect()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        fixture.Peer.Connected = false;
        await fixture.DeclareAsync();
        fixture.Peer.Connected = true;
        fixture.Configuration["SlotFaultDeclaration:enabled"] = "false";

        await fixture.ReconnectAsync();

        Assert.DoesNotContain(fixture.Peer.Lines, line => MessageType(line) == "SlotFaultDeclarationCommand");
        Assert.Equal(SlotFaultDeclarationStates.Pending, Assert.Single(await fixture.DeclarationsAsync()).State);

        // Switched on again, the next reconnect does replay it: the switch holds the command back, it does not drop it.
        fixture.Configuration["SlotFaultDeclaration:enabled"] = "true";
        await fixture.ReconnectAsync();
        Assert.Single(fixture.Peer.Lines, line => MessageType(line) == "SlotFaultDeclarationCommand");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task StartupWarnsAboutPendingDeclarationsOnlyWhileTheSwitchIsOff()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        await fixture.DeclareAsync();
        RecordingLogger<SlotFaultDeclarationTests> log = new();

        IReadOnlyDictionary<string, int> whileOn = await SlotFaultDeclarationStartupCheck.WarnAsync(
            fixture.Context, enabled: true, log, Token);
        IReadOnlyDictionary<string, int> whileOff = await SlotFaultDeclarationStartupCheck.WarnAsync(
            fixture.Context, enabled: false, log, Token);

        Assert.Empty(whileOn);
        Assert.Equal(1, Assert.Single(whileOff, pair => pair.Key == AgvId).Value);
        (LogLevel level, string warning) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("1 declaration(s)", warning, StringComparison.Ordinal);
        Assert.Contains(AgvId + ": 1", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The route exists only while the switch is on: off, a POST is a 404 -- nothing can be declared, so nothing can be
    /// sent; on, the same POST reaches the handler, which refuses it for the missing credential.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData(false, 404)]
    [InlineData(true, 401)]
    public async Task TheRouteIsMappedOnlyWhileTheSwitchIsOn(bool enabled, int expectedStatus)
    {
        string credentialVariable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(credentialVariable, DeclarationCredential);
        try
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlotFaultDeclaration:enabled"] = enabled ? "true" : "false",
                ["SlotFaultDeclaration:credentialEnvironmentVariable"] = credentialVariable
            });
            builder.Services.Configure<SlotFaultDeclarationOptions>(
                builder.Configuration.GetSection(SlotFaultDeclarationOptions.SectionName));
            // The handler is never reached past authentication here, so the service needs none of its collaborators.
            builder.Services.AddScoped(_ => new SlotFaultDeclarationService(
                null!, null!, null!, TimeProvider.System, NullLogger<SlotFaultDeclarationService>.Instance));
            builder.Services.AddSingleton(new VehicleRoster(
                Options.Create(new JourneyRuntimeOptions { AgvId = AgvId, VehicleKey = VehicleKey })));
            await using WebApplication app = builder.Build();

            bool mapped = app.MapSlotFaultDeclarationWhenEnabled();
            await app.StartAsync(Token);
            string address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using HttpClient client = new() { BaseAddress = new Uri(address) };
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                SlotFaultDeclarationEndpoints.Route, Request(), Token);
            await app.StopAsync(Token);

            Assert.Equal(enabled, mapped);
            Assert.Equal(expectedStatus, (int)response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    /// <summary>
    /// The premise that makes an attempt filter on the overdue alarm meaningless (review of control-server#383, S4): on the
    /// wire an alarm has one subject. The overdue alarm's is the slot, so it never names an attempt; one raised with the
    /// attempt as its subject names no slot and is not an overdue slot at all, so a declaration is refused for it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnOverdueAlarmNamesTheSlotOnlyAndOneNamingAnAttemptDoesNotCountAsOverdue()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        OnboardAlarmEntry slotAlarm = Assert.Single(await new OnboardAlarmProjectionStore(fixture.Context)
            .ReadExpectedActionOverdueAsync(AgvId, Token));
        Assert.Equal(1, slotAlarm.PhysicalSlotNumber);
        Assert.Null(slotAlarm.SlotOperationAttemptId);

        await fixture.SendAlarmSnapshotAsync(2, new
        {
            alarmId = OverdueAlarmId,
            code = "SLOT_EXPECTED_ACTION_OVERDUE",
            severity = "WARNING",
            raisedAt = Now.AddMinutes(-2),
            subjectType = "SLOT_OPERATION",
            subjectId = "20000000-0000-4000-8000-00000000aaaa",
            displayMessage = "放入货物并关好1号仓门"
        });

        Assert.Empty(await new OnboardAlarmProjectionStore(fixture.Context).ReadExpectedActionOverdueAsync(AgvId, Token));
        Assert.Equal([SlotFaultDeclarationRefusals.ExpectedActionNotOverdue], Conflict(await fixture.PostAsync(Request())));
    }

    // --- The audit ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every item REQ-0359 names: who, in which role, when, vehicle, demand, attempt, slot, category, note, the vehicle's
    /// last readings for the slot with the time it observed them, and the vehicle's answer.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task TheDeclarationRecordsEveryItemReq0359Names()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "APPLIED");

        SlotFaultDeclarationRow audit = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal(OperatorId, audit.AdministratorId);
        Assert.Equal("MAINTENANCE_ADMINISTRATOR", audit.AdministratorRole);
        Assert.Equal(Now, audit.DeclaredAt);
        Assert.Equal(AgvId, audit.AgvId);
        Assert.Equal(DemandId, audit.DemandId);
        Assert.Equal(AttemptId, audit.SlotOperationAttemptId);
        Assert.Equal("LOAD", audit.OperationType);
        Assert.Equal(1, audit.SlotNo);
        Assert.Equal("LOCK", audit.FaultCategory);
        Assert.Equal("锁舌卡死，门推不开", audit.Note);
        Assert.Equal("APPLIED", audit.ResultOutcome);
        Assert.Equal(Now, audit.ResultReceivedAt);
        using JsonDocument readings = JsonDocument.Parse(audit.ReadingsJson!);
        JsonElement slot = readings.RootElement;
        Assert.Equal("UNLOCKED", slot.GetProperty("lockState").GetString());
        Assert.Equal("EMPTY", slot.GetProperty("physicalState").GetString());
        Assert.Equal("ACTIVE", slot.GetProperty("unlockOutputState").GetString());
        Assert.Equal(Fixture.ReadingsObservedAt, slot.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(2, slot.GetProperty("safetyStateVersion").GetInt64());
    }

    /// <summary>
    /// The readings are those of the latest snapshot; a safety change the vehicle reported for the slot after it, whose new
    /// readings have not arrived, is marked rather than hidden.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task TheReadingsAreTheLatestSnapshotsAndAChangeAfterItIsMarked()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        await fixture.SafetyChangedAsync(3, affectedSlots: [1]);

        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();

        using JsonDocument readings = JsonDocument.Parse(declaration.ReadingsJson!);
        Assert.Equal(2, readings.RootElement.GetProperty("safetyStateVersion").GetInt64());
        Assert.True(readings.RootElement.GetProperty("changedSinceObserved").GetBoolean());
    }

    // --- helpers -----------------------------------------------------------------------------------------------------

    private static SlotFaultDeclarationHttpRequest Request() => new(
        RequestId: "40000000-0000-4000-8000-000000000383",
        AgvId: AgvId,
        SlotNo: 1,
        FaultCategory: "LOCK",
        Note: "锁舌卡死，门推不开",
        OperatorId: OperatorId,
        AdministratorRole: "MAINTENANCE_ADMINISTRATOR");

    private static string[] Conflict(Results<Accepted<SlotFaultDeclarationResponse>, UnauthorizedHttpResult, ProblemHttpResult> result)
    {
        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        return [.. Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["reasons"])];
    }

    private static object DeclaredUnknownResult() => OperationResultPayload(
        "UNKNOWN",
        [
            SlotResult(1, "UNKNOWN", "UNKNOWN", "UNLOCKED", "RESET", [ServerReasonCodes.SlotFaultDeclared]),
            SlotResult(2, "NOT_STARTED", "EMPTY", "LOCKED", "RESET", [])
        ],
        "SLOT_FAULT_DECLARATION_APPLIED");

    private static object CompletedResult() => OperationResultPayload(
        "COMPLETED",
        [
            SlotResult(1, "COMPLETED", "OCCUPIED", "LOCKED", "RESET", []),
            SlotResult(2, "COMPLETED", "OCCUPIED", "LOCKED", "RESET", [])
        ],
        "RESULT_RECORDED");

    private static object SlotResult(
        int slotNo, string outcome, string finalPhysicalState, string lockState, string unlockOutputState, string[] reasonCodes) =>
        new { slotNo, outcome, finalPhysicalState, lockState, unlockOutputState, reasonCodes };

    private static object OperationResultPayload(string overallOutcome, object[] slotResults, string journalCheckpoint)
    {
        var withoutHash = new
        {
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            operationType = "LOAD",
            overallOutcome,
            slotResults,
            observedAt = Now.AddSeconds(1),
            journalCheckpoint
        };
        byte[] businessContent = JsonSerializer.SerializeToUtf8Bytes(withoutHash, WireJson);
        return new
        {
            withoutHash.demandId,
            withoutHash.slotOperationAttemptId,
            withoutHash.operationType,
            withoutHash.overallOutcome,
            withoutHash.slotResults,
            withoutHash.observedAt,
            withoutHash.journalCheckpoint,
            resultContentSha256 = Convert.ToHexString(SHA256.HashData(businessContent)).ToLowerInvariant()
        };
    }

    private static string[] Lines(string response) => response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The answer is a ProtocolProblem refusing exactly <paramref name="messageId"/> with <paramref name="reasonCode"/>.</summary>
    private static void AssertRefused(string answer, string messageId, string reasonCode)
    {
        using JsonDocument problem = JsonDocument.Parse(Assert.Single(Lines(answer)));
        JsonElement root = problem.RootElement;
        Assert.Equal("ProtocolProblem", root.GetProperty("messageType").GetString());
        Assert.Equal(messageId, root.GetProperty("correlationId").GetString());
        Assert.Equal(messageId, root.GetProperty("payload").GetProperty("rejectedMessageId").GetString());
        Assert.Equal(reasonCode, root.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
    }

    private static string MessageType(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    private static object[] Slots(string slot1Lock, string slot1Physical, string slot1Output) =>
        [.. Enumerable.Range(1, 8).Select(slot => new
        {
            slotNo = slot,
            operability = "OPERABLE",
            administrativeAvailability = "ENABLED",
            physicalState = slot == 1 ? slot1Physical : "EMPTY",
            lockState = slot == 1 ? slot1Lock : "LOCKED",
            unlockOutputState = slot == 1 ? slot1Output : "RESET",
            reasonCodes = Array.Empty<string>()
        })];

    private static object Safety(bool departureSafe) => new
    {
        departureSafe,
        vehicleStopped = true,
        allTargetSlotsLocked = departureSafe,
        allUnlockOutputsReset = departureSafe,
        unknownPresent = false,
        reasonCodes = departureSafe ? Array.Empty<string>() : new[] { "LOCK_NOT_CLOSED" }
    };

    private static object ReleaseIdentity() => new
    {
        repository = "8005-agv-protocol",
        releaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
        tag = ProtocolCandidateIdentity.Tag,
        commit = ProtocolCandidateIdentity.RepositoryCommit,
        protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        profileId = ProtocolCandidateIdentity.ProfileId,
        manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        schemaBundleSha256 = ProtocolCandidateIdentity.SchemaBundleSha256,
        vectorsSha256 = ProtocolCandidateIdentity.VectorsSha256
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The live session, or its absence: a send throws the way <c>OnboardPeer</c> does when nothing is attached.</summary>
    private sealed class SwitchablePeer : IOnboardPeer
    {
        public bool Connected { get; set; } = true;

        public List<string> Lines { get; } = [];

        public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            if (!Connected)
            {
                throw new OnboardConnectionUnavailableException("No onboard connection is attached for this vehicle.");
            }
            Lines.Add(Encoding.UTF8.GetString(ndjsonLine.Span));
            return Task.CompletedTask;
        }
    }

    /// <summary>Fails the save that inserts an outbox row: the crash point between the declaration and its command.</summary>
    private sealed class FailOnOutboxInsert : SaveChangesInterceptor
    {
        public bool Triggered { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ProtocolOutboxRow>().Any(entry => entry.State == EntityState.Added))
            {
                Triggered = true;
                throw new DbUpdateException("injected: the outbox row could not be written");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Runs another declaration to completion inside the first one's save, once.</summary>
    private sealed class RunBeforeSave(Func<Task> other) : SaveChangesInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Ran && eventData.Context!.ChangeTracker.Entries<SlotFaultDeclarationRow>()
                    .Any(entry => entry.State == EntityState.Added))
            {
                Ran = true;
                await other();
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// A vehicle through a real handshake, loading at its pickup station with slot 1 open and overdue, and slot 2 still
    /// to come.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly DateTimeOffset ReadingsObservedAt = Now.AddSeconds(-40);

        private readonly string _credentialVariable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");

        private Fixture(SqliteConnection connection, ControlServerDbContext context)
        {
            Connection = connection;
            Context = context;
            // The switch as the host reads it (SlotFaultDeclarationOptions.IsEnabled): on, as on a site that offers the entry
            // point. A test turns it off through Configuration, which the processor reads on every replay.
            Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = SessionCredentialVariable,
                    ["SlotFaultDeclaration:enabled"] = "true"
                })
                .Build();
            Processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), Clock, Configuration, Peer);
            Environment.SetEnvironmentVariable(_credentialVariable, DeclarationCredential);
        }

        public SqliteConnection Connection { get; }

        public IConfigurationRoot Configuration { get; }

        public ControlServerDbContext Context { get; }

        public TimeProvider Clock { get; } = new FixedClock(Now);

        public SwitchablePeer Peer { get; } = new();

        public OnboardMessageProcessor Processor { get; }

        public OnboardConnectionState State { get; private set; } = new();

        public static async Task<Fixture> AwaitingOperatorOnSlotOneAsync(
            int? overdueSlot = 1, bool seedOperation = true, bool migrate = false)
        {
            Environment.SetEnvironmentVariable(SessionCredentialVariable, SessionCredential);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            if (migrate)
            {
                await context.Database.MigrateAsync(Token);
            }
            else
            {
                await context.Database.EnsureCreatedAsync(Token);
            }
            Fixture fixture = new(connection, context);
            await fixture.HandshakeAsync();
            await fixture.SeedLoadAsync(seedOperation);
            if (overdueSlot is int slot)
            {
                await fixture.Send("OnboardAlarmSnapshot", new
                {
                    alarmSnapshotRevision = 1,
                    observedAt = Now.AddSeconds(-50),
                    alarms = new[]
                    {
                        new
                        {
                            alarmId = OverdueAlarmId,
                            code = "SLOT_EXPECTED_ACTION_OVERDUE",
                            severity = "WARNING",
                            raisedAt = Now.AddMinutes(-2),
                            subjectType = "SLOT",
                            subjectId = slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            displayMessage = "放入货物并关好1号仓门"
                        }
                    }
                });
            }
            // The snapshot the server asks for when the alarm appears: slot 1 unlocked, empty, its unlock output on.
            await fixture.Send("SafetyStateSnapshot", new
            {
                safetyStateVersion = 2,
                observedAt = ReadingsObservedAt,
                safety = Safety(departureSafe: false),
                slotStates = Slots("UNLOCKED", "EMPTY", "ACTIVE")
            });
            fixture.Peer.Lines.Clear();
            return fixture;
        }

        private async Task HandshakeAsync()
        {
            await Send("SessionHello", new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = SessionCredential });
            await Send("CapabilitySnapshot", new { capabilityVersion = 1, activeSlotConfigurationFingerprint = new string('0', 64) });
            await Send("SafetyStateSnapshot", new
            {
                safetyStateVersion = 1,
                observedAt = Now.AddMinutes(-5),
                safety = Safety(departureSafe: true),
                slotStates = Slots("LOCKED", "EMPTY", "RESET")
            });
            await RecoveryReportAsync();
        }

        /// <summary>A new connection: a new session generation, its handshake, and the replay its recovery report triggers.</summary>
        public async Task ReconnectAsync()
        {
            State = new OnboardConnectionState();
            await HandshakeAsync();
        }

        private Task<string> RecoveryReportAsync() => Send("RecoveryStateReport", new
        {
            reportId = Guid.NewGuid().ToString("D"),
            unsettledSlotOperationAttemptId = (string?)null,
            provenRecoveryCheckpoint = (string?)null,
            activeUnlockSlots = Array.Empty<int>(),
            forcedRecoveryGeneration = 0,
            pendingResults = Array.Empty<object>()
        });

        private async Task SeedLoadAsync(bool seedOperation)
        {
            Context.AcceptedDemands.Add(new AcceptedDemandRow
            {
                DemandId = DemandId,
                SeriesId = "SERIES-383",
                TransportDemandKey = "SUBLOT-383|WIRE_TO_GATE",
                WorkType = "WIRE_TO_GATE",
                Sublot = "SUBLOT-383",
                Generation = 1,
                DemandRevision = 1,
                HistoryEpoch = "history-1",
                CatalogRevision = 1,
                CreatedAt = Now.AddMinutes(-10),
                ValueObservedAt = Now.AddMinutes(-9),
                ValuePollTraceId = "TRACE-383",
                ValueProjectionCommitId = "COMMIT-383",
                LiveMesFieldsJson = "{}",
                AcceptedAt = Now.AddMinutes(-8),
                Status = DemandExecutionStatus.Accepted
            });
            JourneyRuntimeRow runtime = Runtime();
            Context.JourneyRuntimes.Add(runtime);
            JourneyMembershipSeed.Seed(Context, runtime);
            if (seedOperation)
            {
                Context.StationOperations.Add(new StationOperationRow
                {
                    SlotOperationAttemptId = AttemptId,
                    DemandId = DemandId,
                    SublotId = "SUBLOT-383",
                    TargetSlotsJson = "[1,2]",
                    OperationType = SlotOperationType.Load,
                    ForcedRecoveryGeneration = 0,
                    ContentHash = new string('a', 64),
                    Status = StationOperationStatus.Prepared,
                    CreatedAt = Now.AddMinutes(-4)
                });
            }
            await Context.SaveChangesAsync(Token);
            Context.ChangeTracker.Clear();
        }

        private static JourneyRuntimeRow Runtime()
        {
            JourneyRuntimeRow runtime = new()
            {
                JourneyId = JourneyIdentity.ForAnchorDemand(DemandId),
                DemandId = DemandId,
                Stage = JourneyRuntimeStage.AwaitingLoadResult,
                AgvId = AgvId,
                VehicleKey = VehicleKey,
                AgvLifecycleGeneration = 1,
                MapId = 26,
                MapIdentity = "MAP-26",
                DispatchZone = "ZONE-01",
                RouteEvidenceId = "ROUTE-01",
                PickupStationId = "PICKUP",
                PickupStationRiotId = 11,
                GateStationId = "GATE",
                GateStationRiotId = 22,
                ExpectedBasketCount = 2,
                TargetSlotsJson = "[1,2]",
                OperationSessionId = "c0000000-0000-4000-8000-000000000383",
                PickupMovementLegId = "pickup-leg",
                PickupUpperId = "UPPER-PICKUP",
                GateMovementLegId = "gate-leg",
                GateUpperId = "UPPER-GATE",
                DispatchGeneration = 1,
                VehicleBusinessRevision = 1,
                WorklistRevision = 1,
                PlanRevision = 1,
                VehicleBusinessMessageId = "d0000000-0000-4000-8000-000000000001",
                WorklistMessageId = "d0000000-0000-4000-8000-000000000002",
                PlanMessageId = "d0000000-0000-4000-8000-000000000003",
                SublotRequestMessageId = "d0000000-0000-4000-8000-000000000004",
                LoadCommandMessageId = "d0000000-0000-4000-8000-000000000005",
                LoadSlotOperationAttemptId = AttemptId,
                PreDepartureSafetyCheckMessageId = "d0000000-0000-4000-8000-000000000006",
                PreDepartureSafetyCheckId = "d0000000-0000-4000-8000-000000000007",
                GateVehicleBusinessMessageId = "d0000000-0000-4000-8000-000000000008",
                GateWorklistMessageId = "d0000000-0000-4000-8000-000000000009",
                GatePlanMessageId = "d0000000-0000-4000-8000-000000000010",
                UnloadCommandMessageId = "d0000000-0000-4000-8000-000000000011",
                UnloadSlotOperationAttemptId = "d0000000-0000-4000-8000-000000000012",
                CreatedAt = Now.AddMinutes(-8),
                UpdatedAt = Now.AddMinutes(-4)
            };
            runtime.SetBlockReason(null, Now.AddMinutes(-4));
            return runtime;
        }

        public Task<string> SendAlarmSnapshotAsync(long revision, params object[] alarms) => Send(
            "OnboardAlarmSnapshot",
            new { alarmSnapshotRevision = revision, observedAt = Now.AddSeconds(-10), alarms });

        public async Task SetOperationStatusAsync(StationOperationStatus status)
        {
            StationOperationRow operation = await Context.StationOperations.SingleAsync(Token);
            operation.Status = status;
            await Context.SaveChangesAsync(Token);
            Context.ChangeTracker.Clear();
        }

        public async Task HoldSessionUnreadyAsync()
        {
            SessionRecoveryRow session = await Context.SessionRecoveries.SingleAsync(Token);
            session.Readiness = SessionReadiness.RecoveryRequired;
            session.ReasonCode = "PENDING_FACT_RECONCILIATION_REQUIRED";
            await Context.SaveChangesAsync(Token);
            Context.ChangeTracker.Clear();
        }

        public Task<string> SafetyChangedAsync(long revision, int[] affectedSlots) => Send("SafetyStateChanged", new
        {
            safetyStateVersion = revision,
            observedAt = Now.AddSeconds(-5),
            safety = Safety(departureSafe: false),
            affectedSlots
        });

        public Task<Results<Accepted<SlotFaultDeclarationResponse>, UnauthorizedHttpResult, ProblemHttpResult>> PostAsync(
            SlotFaultDeclarationHttpRequest request,
            string authorization = "Bearer " + DeclarationCredential,
            string? credentialVariable = null) =>
            PostOnAsync(Context, request, authorization, credentialVariable);

        /// <summary>The same request through a second context on the same database, with an interceptor on its saves.</summary>
        public async Task<Results<Accepted<SlotFaultDeclarationResponse>, UnauthorizedHttpResult, ProblemHttpResult>> PostWithInterceptorAsync(
            SlotFaultDeclarationHttpRequest request,
            IInterceptor interceptor)
        {
            await using ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>()
                    .UseSqlite(Connection)
                    .AddInterceptors(interceptor)
                    .Options);
            return await PostOnAsync(context, request, "Bearer " + DeclarationCredential, null);
        }

        private async Task<Results<Accepted<SlotFaultDeclarationResponse>, UnauthorizedHttpResult, ProblemHttpResult>> PostOnAsync(
            ControlServerDbContext context,
            SlotFaultDeclarationHttpRequest request,
            string authorization,
            string? credentialVariable)
        {
            DefaultHttpContext http = new();
            http.Request.Headers.Authorization = authorization;
            WireToGateStore store = new(context);
            SlotFaultDeclarationService service = new(
                context,
                store,
                new OnboardJourneyPublisher(store, Peer, Clock),
                Clock,
                NullLogger<SlotFaultDeclarationService>.Instance);
            try
            {
                return await SlotFaultDeclarationEndpoints.HandleAsync(
                    http,
                    request,
                    service,
                    new VehicleRoster(Options.Create(new JourneyRuntimeOptions { AgvId = AgvId, VehicleKey = VehicleKey })),
                    Options.Create(new SlotFaultDeclarationOptions
                    {
                        Enabled = true,
                        CredentialEnvironmentVariable = credentialVariable ?? _credentialVariable
                    }),
                    Token);
            }
            finally
            {
                Context.ChangeTracker.Clear();
            }
        }

        public async Task<SlotFaultDeclarationRow> DeclareAsync()
        {
            Assert.IsType<Accepted<SlotFaultDeclarationResponse>>((await PostAsync(Request())).Result);
            return Assert.Single(await DeclarationsAsync());
        }

        public Task<string> RequestLoadCancellationAsync() => Send(
            "LoadCancellationStartRequested",
            new
            {
                cancellationId = "c0000000-0000-4000-8000-000000000384",
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                @operator = new { operatorId = "operator-384", verificationMethod = "BADGE", verifiedAt = Now },
                reason = "Operator cancels the load at the station."
            });

        public Task<string> SendResultAsync(
            string declarationId, string attemptId, string outcome, object? problem = null, string? messageId = null) => Send(
            "SlotFaultDeclarationResult",
            new
            {
                declarationId,
                slotOperationAttemptId = attemptId,
                outcome,
                problem = problem ?? (outcome == "NOT_APPLICABLE"
                    ? new
                    {
                        reasonCode = "ACTION_NOT_ALLOWED_IN_STATE",
                        fieldPath = "payload.slotNo",
                        displayMessage = "仓位已闭环"
                    }
                    : null)
            },
            messageId);

        /// <summary>The vehicle refusing one of the server's messages: a ProtocolProblem correlated to it.</summary>
        public Task<string> RefuseCommandAsync(string commandMessageId, string reasonCode, string? messageId = null) => Send(
            "ProtocolProblem",
            new
            {
                rejectedMessageId = commandMessageId,
                rejectedMessageType = "SlotFaultDeclarationCommand",
                problem = new
                {
                    reasonCode,
                    fieldPath = (string?)null,
                    displayMessage = "本车已放弃对这项判定的应答"
                },
                expectedProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                expectedProfileId = ProtocolCandidateIdentity.ProfileId,
                expectedProtocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256
            },
            messageId,
            correlationId: commandMessageId);

        /// <summary>
        /// A declaration whose answer the vehicle gave up, reached on the wire: its answer refused (another attempt), then the
        /// replayed command refused with <c>SLOT_OPERATION_CONFLICT</c>.
        /// </summary>
        public async Task<SlotFaultDeclarationRow> UnreconciledDeclarationAsync()
        {
            SlotFaultDeclarationRow declaration = await DeclareAsync();
            await SendResultAsync(declaration.DeclarationId, "20000000-0000-4000-8000-00000000ffff", "APPLIED");
            await ReconnectAsync();
            await RefuseCommandAsync(declaration.CommandMessageId, ServerReasonCodes.SlotOperationConflict);
            SlotFaultDeclarationRow after = Assert.Single(await DeclarationsAsync());
            Assert.Equal(SlotFaultDeclarationStates.Unreconciled, after.State);
            return after;
        }

        /// <summary>A pending declaration of another vehicle, as this server would have written it, without its command.</summary>
        public async Task<SlotFaultDeclarationRow> SeedOtherVehiclesDeclarationAsync()
        {
            SlotFaultDeclarationRow row = new()
            {
                DeclarationId = "30000000-0000-4000-8000-000000000481",
                RequestId = "30000000-0000-4000-8000-000000000482",
                RequestContentHash = new string('b', 64),
                AgvId = "AGV-002",
                DemandId = "10000000-0000-4000-8000-000000000481",
                SlotOperationAttemptId = "20000000-0000-4000-8000-000000000481",
                OperationType = "LOAD",
                SlotNo = 1,
                FaultCategory = "LOCK",
                Note = "另一台车的判定",
                AdministratorId = OperatorId,
                AdministratorRole = "MAINTENANCE_ADMINISTRATOR",
                DeclaredAt = Now.AddMinutes(-1),
                CommandMessageId = "30000000-0000-4000-8000-000000000483",
                State = SlotFaultDeclarationStates.Pending
            };
            Context.Set<SlotFaultDeclarationRow>().Add(row);
            await Context.SaveChangesAsync(Token);
            Context.ChangeTracker.Clear();
            return Assert.Single(await DeclarationsAsync());
        }

        public async Task<string> SendOperationResultAsync(object payload)
        {
            string answer = await Send("OperationResult", payload);
            await Processor.FlushDeferredOutboundAsync(State, Token);
            return answer;
        }

        public async Task<SlotFaultDeclarationRow[]> DeclarationsAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.Set<SlotFaultDeclarationRow>().AsNoTracking().ToArrayAsync(Token);
        }

        public async Task<ProtocolOutboxRow[]> CommandsAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "SlotFaultDeclarationCommand")
                .ToArrayAsync(Token);
        }

        /// <summary>
        /// What a protocol identity change looks like to the outbox: the command lines were written by the build before.
        /// Only the commands are rewritten -- the handshake's own lines stay under this build's identity.
        /// </summary>
        public async Task WriteCommandsUnderThePreviousIdentityAsync()
        {
            Context.ChangeTracker.Clear();
            foreach (ProtocolOutboxRow row in await Context.ProtocolOutbox
                         .Where(item => item.MessageType == "SlotFaultDeclarationCommand").ToArrayAsync(Token))
            {
                System.Text.Json.Nodes.JsonObject envelope =
                    System.Text.Json.Nodes.JsonNode.Parse(row.PayloadJson)!.AsObject();
                envelope["protocolVersion"] = 3;
                envelope["protocolReleaseVersion"] = "2.0.0";
                envelope["protocolReleaseManifestSha256"] = "4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7";
                row.PayloadJson = envelope.ToJsonString();
            }
            await Context.SaveChangesAsync(Token);
            Context.ChangeTracker.Clear();
        }

        /// <summary>The state a build before control-server#384 left an answered declaration's command in.</summary>
        public async Task UnsettleCommandsAsync()
        {
            Context.ChangeTracker.Clear();
            foreach (ProtocolOutboxRow row in await Context.ProtocolOutbox
                         .Where(item => item.MessageType == "SlotFaultDeclarationCommand").ToArrayAsync(Token))
            {
                row.AcknowledgedAt = null;
            }
            await Context.SaveChangesAsync(Token);
            Context.ChangeTracker.Clear();
        }

        /// <summary>The declaration commands' outbox lines, settled at <paramref name="settledAt"/>.</summary>
        public async Task SettleCommandsAsync(DateTimeOffset settledAt)
        {
            Context.ChangeTracker.Clear();
            foreach (ProtocolOutboxRow row in await Context.ProtocolOutbox
                         .Where(item => item.MessageType == "SlotFaultDeclarationCommand").ToArrayAsync(Token))
            {
                row.AcknowledgedAt = settledAt;
            }
            await Context.SaveChangesAsync(Token);
            Context.ChangeTracker.Clear();
        }

        /// <summary>The declaration commands' outbox lines removed.</summary>
        public async Task RemoveCommandsAsync()
        {
            Context.ChangeTracker.Clear();
            Context.ProtocolOutbox.RemoveRange(await Context.ProtocolOutbox
                .Where(item => item.MessageType == "SlotFaultDeclarationCommand").ToArrayAsync(Token));
            await Context.SaveChangesAsync(Token);
            Context.ChangeTracker.Clear();
        }

        public async Task<StationOperationRow> OperationAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.StationOperations.AsNoTracking().SingleAsync(Token);
        }

        public async Task<AcceptedDemandRow> DemandAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.AcceptedDemands.AsNoTracking().SingleAsync(Token);
        }

        public async Task<OperationResultRow[]> OperationResultsAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.OperationResults.AsNoTracking().ToArrayAsync(Token);
        }

        /// <summary>The three tables a declaration must not touch on its own, every column of every row.</summary>
        public async Task<string> BusinessPictureAsync()
        {
            Context.ChangeTracker.Clear();
            return JsonSerializer.Serialize(new
            {
                demands = await Context.AcceptedDemands.AsNoTracking().OrderBy(row => row.DemandId).ToArrayAsync(Token),
                operations = await Context.StationOperations.AsNoTracking()
                    .OrderBy(row => row.SlotOperationAttemptId).ToArrayAsync(Token),
                journeys = await Context.JourneyRuntimes.AsNoTracking().OrderBy(row => row.JourneyId).ToArrayAsync(Token)
            });
        }

        private Task<string> Send(
            string messageType, object payload, string? messageId = null, string? correlationId = null) =>
            Processor.ProcessAsync(
                JsonSerializer.Serialize(new
                {
                    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                    profileId = ProtocolCandidateIdentity.ProfileId,
                    protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                    protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                    messageType,
                    messageId = messageId ?? Guid.NewGuid().ToString("D"),
                    correlationId,
                    agvId = AgvId,
                    sessionGeneration = State.SessionGeneration,
                    sentAt = Now,
                    payload = JsonSerializer.SerializeToElement(payload, WireJson)
                }),
                State,
                Token);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
            Environment.SetEnvironmentVariable(_credentialVariable, null);
        }
    }
}
