using ControlServer.Domain;

namespace ControlServer.Application;

public interface IMovementIntentStore
{
    Task<StoredMovementIntent?> GetByUpperIdAsync(string upperId, CancellationToken cancellationToken);

    Task PersistExperimentalCreateAuthorizationAsync(
        ExperimentalRiotCreateAuthorization authorization,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken);

    Task RecordReconciliationAsync(
        string upperId,
        DispatchAuditWrite audit,
        bool markResultUnknown,
        CancellationToken cancellationToken);

    Task<CreateDispatchAttempt> ArmCreateDispatchAsync(
        string upperId,
        string requestSemanticSha256,
        DateTimeOffset armedAt,
        CancellationToken cancellationToken);

    Task<CreateDispatchAttempt> ArmExperimentalCreateDispatchAsync(
        string upperId,
        string requestSemanticSha256,
        ExperimentalRiotCreateAuthorization authorization,
        string eligibilityBasis,
        DateTimeOffset armedAt,
        CancellationToken cancellationToken);

    Task RecordCreateStartedAsync(
        string upperId,
        CreateDispatchAttempt attempt,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);

    Task RecordCreateResponseAsync(
        string upperId,
        CreateDispatchAttempt attempt,
        DispatchAuditWrite audit,
        bool markResultUnknown,
        CancellationToken cancellationToken);

    Task MarkTerminalReconciliationRequiredAsync(
        string upperId,
        string orderId,
        DispatchAuditWrite audit,
        CancellationToken cancellationToken);

    Task ConfirmAsync(
        string upperId,
        string orderId,
        DispatchAuditWrite audit,
        CancellationToken cancellationToken);
}

public sealed record StoredMovementIntent(
    OrderIntent Intent,
    string Status,
    string? OrderId,
    int? DispatchAuditVersion,
    string? CreateAttemptId,
    int? CreateAttemptCount,
    string? ExperimentalAuthorizationId = null,
    string? EligibilityBasis = null);
