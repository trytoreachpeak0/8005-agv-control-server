using ControlServer.Application;
using ControlServer.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Infrastructure.Adapters;

/// <summary>
/// The six approved RIoT commands, each through its named Facade.
/// </summary>
/// <remarks>
/// <para>
/// Every call here goes through a named Facade — <c>.Raw</c> appears nowhere in <c>src/</c> and
/// this adapter does not introduce the first exception (REQ-0309). The six methods it reaches are
/// exactly the ones the allowlist approves in 1.3 and 1.5, and
/// <c>RiotCallAllowlistArchitectureTests</c> proves it against the compiled assembly rather than
/// against this sentence.
/// </para>
/// <para>
/// <b>The whole job of this class is to keep three answers apart</b>: RIoT accepted the call, RIoT
/// refused it, and nobody knows. The SDK signals all three by returning or throwing, and an
/// exception that escaped here would collapse "refused" and "never arrived" into one — after
/// which a retry of a command that may already have taken effect looks safe. Nothing above this
/// layer can recover that distinction, so nothing here throws for a RIoT failure.
/// </para>
/// <para>
/// <b>It also does not reconcile.</b> An accepted call is not a held order and not a stopped
/// vehicle; the SDK's own summaries say so for HangContinue and for both emergency commands.
/// Reading the terminal state back belongs to the caller, which is why this returns a receipt and
/// no verdict.
/// </para>
/// </remarks>
public sealed class HttpRiotOrderCommandGateway : IRiotOrderCommandGateway, IRiotVehicleEmergencyFacts
{
    private readonly RiotSession riotSession;
    private readonly TimeProvider timeProvider;

    public HttpRiotOrderCommandGateway(RiotSession riotSession)
        : this(riotSession, TimeProvider.System)
    {
    }

    [ActivatorUtilitiesConstructor]
    public HttpRiotOrderCommandGateway(RiotSession riotSession, TimeProvider timeProvider)
    {
        this.riotSession = riotSession;
        this.timeProvider = timeProvider;
    }

    public async Task<RiotCommandCallResult> IssueOrderCommandAsync(
        RiotOrderCommandKind kind,
        string orderId,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        string operation = RiotCommandTypeNames.For(kind);
        try
        {
            Task call = kind switch
            {
                RiotOrderCommandKind.Cancel =>
                    riotSession.Tasks.CancelOrderAsync(orderId, reason, cancellationToken),
                RiotOrderCommandKind.Hold =>
                    riotSession.Tasks.OrderHoldAsync(orderId, reason, cancellationToken),
                RiotOrderCommandKind.ContinueFromHeld =>
                    riotSession.Tasks.OrderContinueAsync(orderId, reason, cancellationToken),
                RiotOrderCommandKind.ContinueFromHang =>
                    riotSession.Tasks.HangContinueAsync(orderId, reason, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown RIoT order command.")
            };
            await call.ConfigureAwait(false);
            return Accepted(operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return Failure(operation, error);
        }
    }

    public async Task<RiotCommandCallResult> IssueEmergencyCommandAsync(
        RiotEmergencyCommandKind kind,
        string deviceKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        string operation = RiotCommandTypeNames.For(kind);
        try
        {
            Task call = kind switch
            {
                RiotEmergencyCommandKind.Trigger =>
                    riotSession.Device.TriggerEmergencyStopAsync(deviceKey, cancellationToken),
                RiotEmergencyCommandKind.Cancel =>
                    riotSession.Device.CancelEmergencyStopAsync(deviceKey, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown RIoT emergency command.")
            };
            await call.ConfigureAwait(false);
            return Accepted(operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return Failure(operation, error);
        }
    }

    public async Task<RiotVehicleEmergencyObservation> ReadEmergencyStateAsync(
        string deviceKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        try
        {
            VehicleExecutionFacts vehicle = await riotSession.Tasks.GetVehicleExecutionFactsAsync(
                deviceKey, cancellationToken).ConfigureAwait(false);
            return new RiotVehicleEmergencyObservation(
                deviceKey,
                string.IsNullOrWhiteSpace(vehicle.EmergencyState) ? null : vehicle.EmergencyState,
                timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            // No answer is not OK. An unknown latch keeps REQ-0248's retry running, which is the
            // safe direction: the alternative reads "we could not ask" as "the vehicle is fine".
            return new RiotVehicleEmergencyObservation(deviceKey, null, timeProvider.GetUtcNow());
        }
    }

    private RiotCommandCallResult Accepted(string operation) => new(
        RiotCommandCallDisposition.Accepted,
        new RiotOrderCallReceipt(
            operation,
            "SdkAccepted",
            timeProvider.GetUtcNow(),
            ResultPresent: true));

    private RiotCommandCallResult Failure(string operation, Exception error)
    {
        bool unknown = RiotCallFailureClassification.IsTimeout(error) ||
            error is HttpRequestException or IOException;
        return new RiotCommandCallResult(
            unknown ? RiotCommandCallDisposition.Unknown : RiotCommandCallDisposition.Failed,
            new RiotOrderCallReceipt(
                operation,
                "SdkFailure",
                timeProvider.GetUtcNow(),
                RiotCallFailureClassification.HttpStatusCode(error),
                RiotAuditSanitizer.BusinessCode(RiotCallFailureClassification.RawBusinessCode(error)),
                ResultPresent: false,
                FailureCategory: RiotCallFailureClassification.FailureCategory(error)));
    }
}
