using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
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
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task TwoAdministratorsDeclaringTheSameSlotAtOnceAreHeldToOneDeclarationByTheDatabase()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
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
    /// A second answer for the same declaration -- a resend under a new messageId, or a contradicting one -- changes
    /// nothing: the first answer is the record.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ASecondAnswerForTheSameDeclarationChangesNothing()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "NOT_APPLICABLE");
        SlotFaultDeclarationRow first = Assert.Single(await fixture.DeclarationsAsync());
        string picture = await fixture.BusinessPictureAsync();

        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "NOT_APPLICABLE");
        await fixture.SendResultAsync(declaration.DeclarationId, AttemptId, "APPLIED");

        SlotFaultDeclarationRow after = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(after));
        Assert.Equal(picture, await fixture.BusinessPictureAsync());
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

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnAnswerNamingAnotherAttemptIsAcknowledgedAndLeavesTheDeclarationPending()
    {
        await using Fixture fixture = await Fixture.AwaitingOperatorOnSlotOneAsync();
        SlotFaultDeclarationRow declaration = await fixture.DeclareAsync();
        string picture = await fixture.BusinessPictureAsync();

        string ack = await fixture.SendResultAsync(
            declaration.DeclarationId, "20000000-0000-4000-8000-00000000ffff", "APPLIED");

        Assert.Equal(["DurableAck"], Lines(ack).Select(MessageType));
        Assert.Equal(picture, await fixture.BusinessPictureAsync());
        SlotFaultDeclarationRow after = Assert.Single(await fixture.DeclarationsAsync());
        Assert.Equal(SlotFaultDeclarationStates.Pending, after.State);
        Assert.Null(after.ResultOutcome);
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
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = SessionCredentialVariable
                })
                .Build();
            Processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), Clock, configuration, Peer);
            Environment.SetEnvironmentVariable(_credentialVariable, DeclarationCredential);
        }

        public SqliteConnection Connection { get; }

        public ControlServerDbContext Context { get; }

        public TimeProvider Clock { get; } = new FixedClock(Now);

        public SwitchablePeer Peer { get; } = new();

        public OnboardMessageProcessor Processor { get; }

        public OnboardConnectionState State { get; private set; } = new();

        public static async Task<Fixture> AwaitingOperatorOnSlotOneAsync(int? overdueSlot = 1, bool seedOperation = true)
        {
            Environment.SetEnvironmentVariable(SessionCredentialVariable, SessionCredential);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(Token);
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

        public Task<string> SendResultAsync(string declarationId, string attemptId, string outcome) => Send(
            "SlotFaultDeclarationResult",
            new
            {
                declarationId,
                slotOperationAttemptId = attemptId,
                outcome,
                problem = outcome == "NOT_APPLICABLE"
                    ? new
                    {
                        reasonCode = "ACTION_NOT_ALLOWED_IN_STATE",
                        fieldPath = "payload.slotNo",
                        displayMessage = "仓位已闭环"
                    }
                    : null
            });

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

        private Task<string> Send(string messageType, object payload) =>
            Processor.ProcessAsync(
                JsonSerializer.Serialize(new
                {
                    protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                    profileId = ProtocolCandidateIdentity.ProfileId,
                    protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                    protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                    messageType,
                    messageId = Guid.NewGuid().ToString("D"),
                    correlationId = (string?)null,
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
