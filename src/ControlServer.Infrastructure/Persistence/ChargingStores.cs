using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

// 批次 9 建表票 control-server#399 的存储。没有运行时读者：取用者是批次9-02～9-12，它们零迁移。

/// <summary>
/// 充电桩名册。版本行、桩行、候选车辆行与治理快照、业务审计在同一个事务里；桩的内容与当前版本相同时不写。
/// </summary>
public sealed class ChargerRosterStore(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher) : IChargerRoster
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));

    public async Task<ChargerRosterVersion> WriteVersionAsync(
        IReadOnlyList<ChargerRosterEntry> chargers,
        ChargerRosterApproval approval,
        DateTimeOffset loadedAt,
        CancellationToken cancellationToken)
    {
        ChargerRosterEntry[] ordered = Normalise(chargers);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.ApprovedBy, nameof(approval));
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.ApprovalBasis, nameof(approval));
        string contentJson = ContentJson(ordered);

        await using IDbContextTransaction? transaction = _context.Database.CurrentTransaction is null
            ? await _context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        ChargerRosterVersion? current = await ReadCurrentAsync(cancellationToken);
        if (current is not null && current.ContentSha256 == ChargingContent.Sha256(contentJson))
        {
            return current;
        }

        long version = (current?.Version ?? 0) + 1;
        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.ChargerRoster,
            ChargingGovernance.RosterObjectId,
            version,
            contentJson,
            ChargingGovernance.RosterVersionImportedAction,
            loadedAt,
            cancellationToken);

        _context.Set<ChargerRosterVersionRow>().Add(new ChargerRosterVersionRow
        {
            Version = version,
            ContentSha256 = snapshot.ContentSha256,
            SnapshotId = snapshot.SnapshotId,
            LoadedAt = loadedAt,
            Source = ChargerRosterSources.GovernedImport,
            ApprovedBy = approval.ApprovedBy,
            ApprovalBasis = approval.ApprovalBasis,
            ChangeNote = approval.ChangeNote
        });
        _context.Set<ChargerRosterEntryRow>().AddRange(ordered.Select(charger => new ChargerRosterEntryRow
        {
            Version = version,
            MapId = charger.MapId,
            StationId = charger.StationId,
            StationName = charger.StationName,
            EntryStationId = charger.EntryStationId,
            ExitStationId = charger.ExitStationId
        }));
        _context.Set<ChargerRosterVehicleScopeRow>().AddRange(ordered.SelectMany(charger => charger.VehicleScope.Select(
            vehicleKey => new ChargerRosterVehicleScopeRow
            {
                Version = version,
                MapId = charger.MapId,
                StationId = charger.StationId,
                VehicleKey = vehicleKey
            })));
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ChargerRosterVersion(
            version, snapshot.ContentSha256, snapshot.SnapshotId, loadedAt, ChargerRosterSources.GovernedImport, approval,
            ordered);
    }

    public async Task<ChargerRosterVersion?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        long? current = await _context.Set<ChargerRosterVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken);
        return current is null ? null : await ReadVersionAsync(current.Value, cancellationToken);
    }

    public async Task<ChargerRosterVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken)
    {
        ChargerRosterVersionRow? header = await _context.Set<ChargerRosterVersionRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Version == version, cancellationToken);
        if (header is null)
        {
            return null;
        }
        ChargerRosterEntryRow[] rows = await _context.Set<ChargerRosterEntryRow>().AsNoTracking()
            .Where(row => row.Version == version)
            .ToArrayAsync(cancellationToken);
        ChargerRosterVehicleScopeRow[] scopes = await _context.Set<ChargerRosterVehicleScopeRow>().AsNoTracking()
            .Where(row => row.Version == version)
            .ToArrayAsync(cancellationToken);
        ChargerRosterEntry[] chargers =
        [
            .. rows.OrderBy(row => row.MapId).ThenBy(row => row.StationId).Select(row => new ChargerRosterEntry(
                row.MapId,
                row.StationId,
                row.StationName,
                row.EntryStationId,
                row.ExitStationId,
                [
                    .. scopes.Where(scope => scope.MapId == row.MapId && scope.StationId == row.StationId)
                        .Select(scope => scope.VehicleKey)
                        .Order(StringComparer.Ordinal)
                ]))
        ];
        return new ChargerRosterVersion(
            header.Version,
            header.ContentSha256,
            header.SnapshotId,
            header.LoadedAt,
            header.Source,
            new ChargerRosterApproval(header.ApprovedBy, header.ApprovalBasis, header.ChangeNote),
            chargers);
    }

    // Ordered so that the same roster always freezes to the same content and the same SHA-256. An empty list is a roster.
    private static ChargerRosterEntry[] Normalise(IReadOnlyList<ChargerRosterEntry> chargers)
    {
        ArgumentNullException.ThrowIfNull(chargers);
        foreach (ChargerRosterEntry charger in chargers)
        {
            ArgumentNullException.ThrowIfNull(charger, nameof(chargers));
            ArgumentException.ThrowIfNullOrWhiteSpace(charger.StationName, nameof(chargers));
            ArgumentNullException.ThrowIfNull(charger.VehicleScope, nameof(chargers));
            foreach (string vehicleKey in charger.VehicleScope)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey, nameof(chargers));
            }
        }
        string[] duplicated =
        [
            .. chargers.GroupBy(charger => (charger.MapId, charger.StationId))
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key.MapId}/{group.Key.StationId}")
        ];
        if (duplicated.Length > 0)
        {
            throw new ArgumentException(
                $"A charger roster names each station at most once; repeated: {string.Join(", ", duplicated)}.",
                nameof(chargers));
        }
        return
        [
            .. chargers.OrderBy(charger => charger.MapId).ThenBy(charger => charger.StationId).Select(charger => charger with
            {
                VehicleScope = [.. charger.VehicleScope.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
            })
        ];
    }

    private static string ContentJson(IEnumerable<ChargerRosterEntry> ordered) =>
        JsonSerializer.Serialize(new
        {
            chargers = ordered.Select(charger => new
            {
                mapId = charger.MapId,
                stationId = charger.StationId,
                stationName = charger.StationName,
                entryStationId = charger.EntryStationId,
                exitStationId = charger.ExitStationId,
                vehicleScope = charger.VehicleScope
            })
        });
}

/// <summary>
/// 充电策略版本。版本行与适用范围行和治理快照、业务审计在同一个事务里；批准与激活各自只追加。
/// </summary>
public sealed class ChargingPolicyStore(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher) : IChargingPolicyStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));

    public async Task<ChargingPolicyVersion> WriteVersionAsync(
        ChargingPolicyContent content, string? changeNote, DateTimeOffset writtenAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(content.VehicleScope, nameof(content));
        foreach (string vehicleKey in content.VehicleScope)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey, nameof(content));
        }
        ChargingPolicyContent ordered = content with
        {
            VehicleScope = [.. content.VehicleScope.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
        };
        string contentJson = ContentJson(ordered);

        await using IDbContextTransaction? transaction = _context.Database.CurrentTransaction is null
            ? await _context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        long? latest = await _context.Set<ChargingPolicyVersionRow>().MaxAsync(row => (long?)row.Version, cancellationToken);
        ChargingPolicyVersion? current = latest is null ? null : await ReadVersionAsync(latest.Value, cancellationToken);
        if (current is not null && current.ContentSha256 == ChargingContent.Sha256(contentJson))
        {
            return current;
        }

        long version = (latest ?? 0) + 1;
        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.ChargingPolicy,
            ChargingGovernance.PolicyObjectId,
            version,
            contentJson,
            ChargingGovernance.PolicyVersionWrittenAction,
            writtenAt,
            cancellationToken);

        _context.Set<ChargingPolicyVersionRow>().Add(new ChargingPolicyVersionRow
        {
            Version = version,
            ContentSha256 = snapshot.ContentSha256,
            SnapshotId = snapshot.SnapshotId,
            WrittenAt = writtenAt,
            ChangeNote = changeNote,
            MinimumPostTaskBatteryMarginPercent = ordered.MinimumPostTaskBatteryMarginPercent,
            MandatoryChargeEntryThresholdPercent = ordered.MandatoryChargeEntryThresholdPercent,
            ChargingCompletionThresholdPercent = ordered.ChargingCompletionThresholdPercent,
            EstimatedTaskConsumptionPercent = ordered.EstimatedTaskConsumptionPercent,
            ProgressStabilizationSeconds = ordered.ProgressStabilizationSeconds,
            ProgressObservationWindowSeconds = ordered.ProgressObservationWindowSeconds,
            ProgressMinimumIncreasePercent = ordered.ProgressMinimumIncreasePercent
        });
        _context.Set<ChargingPolicyVehicleScopeRow>().AddRange(ordered.VehicleScope.Select(vehicleKey =>
            new ChargingPolicyVehicleScopeRow { Version = version, VehicleKey = vehicleKey }));
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ChargingPolicyVersion(version, snapshot.ContentSha256, snapshot.SnapshotId, writtenAt, changeNote, ordered);
    }

    public async Task<ChargingPolicyApproval> ApproveAsync(
        long version,
        string approvedBy,
        string approverRole,
        DateTimeOffset approvedAt,
        string basisReference,
        string source,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(approverRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(basisReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!await _context.Set<ChargingPolicyVersionRow>().AnyAsync(row => row.Version == version, cancellationToken))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Charging policy version {version} does not exist and cannot be approved."));
        }
        // The source itself is not checked here: the CHECK constraint is what refuses a fourth one.
        ChargingPolicyApprovalRow row = new()
        {
            ApprovalId = Guid.NewGuid().ToString("D"),
            Version = version,
            ApprovedBy = approvedBy,
            ApproverRole = approverRole,
            ApprovedAt = approvedAt,
            BasisReference = basisReference,
            Source = source
        };
        _context.Set<ChargingPolicyApprovalRow>().Add(row);
        await _context.SaveChangesAsync(cancellationToken);
        return ToModel(row);
    }

    public async Task<ChargingPolicyActivation> ActivateAsync(
        long version, string activatedBy, DateTimeOffset activatedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activatedBy);
        if (!await _context.Set<ChargingPolicyApprovalRow>().AnyAsync(row => row.Version == version, cancellationToken))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Charging policy version {version} has no approval and cannot be activated (REQ-0282)."));
        }
        long sequence = (await _context.Set<ChargingPolicyActivationRow>()
            .MaxAsync(row => (long?)row.Sequence, cancellationToken) ?? 0) + 1;
        ChargingPolicyActivationRow row = new()
        {
            ActivationId = Guid.NewGuid().ToString("D"),
            Sequence = sequence,
            Version = version,
            ActivatedAt = activatedAt,
            ActivatedBy = activatedBy
        };
        _context.Set<ChargingPolicyActivationRow>().Add(row);
        await _context.SaveChangesAsync(cancellationToken);
        return new ChargingPolicyActivation(row.ActivationId, row.Sequence, row.Version, row.ActivatedAt, row.ActivatedBy);
    }

    public async Task<EffectiveChargingPolicy?> ReadEffectiveForVehicleAsync(
        string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ChargingPolicyActivationRow? latest = await _context.Set<ChargingPolicyActivationRow>().AsNoTracking()
            .OrderByDescending(row => row.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
        {
            return null;
        }
        IReadOnlyList<ChargingPolicyApproval> approvals = await ListApprovalsAsync(latest.Version, cancellationToken);
        ChargingPolicyVersion? policy = await ReadVersionAsync(latest.Version, cancellationToken);
        // Activation refuses an unapproved version; the approval is checked again here so that a row written around the
        // store still reads as "no effective policy" rather than as an approved one.
        if (policy is null || approvals.Count == 0)
        {
            return null;
        }
        IReadOnlyList<string> scope = policy.Content.VehicleScope;
        if (scope.Count > 0 && !scope.Contains(vehicleKey, StringComparer.Ordinal))
        {
            return null;
        }
        return new EffectiveChargingPolicy(
            policy,
            approvals,
            new ChargingPolicyActivation(
                latest.ActivationId, latest.Sequence, latest.Version, latest.ActivatedAt, latest.ActivatedBy));
    }

    public async Task<ChargingPolicyVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken)
    {
        ChargingPolicyVersionRow? row = await _context.Set<ChargingPolicyVersionRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Version == version, cancellationToken);
        if (row is null)
        {
            return null;
        }
        string[] scope = await _context.Set<ChargingPolicyVehicleScopeRow>().AsNoTracking()
            .Where(item => item.Version == version)
            .Select(item => item.VehicleKey)
            .ToArrayAsync(cancellationToken);
        return new ChargingPolicyVersion(
            row.Version,
            row.ContentSha256,
            row.SnapshotId,
            row.WrittenAt,
            row.ChangeNote,
            new ChargingPolicyContent(
                row.MinimumPostTaskBatteryMarginPercent,
                row.MandatoryChargeEntryThresholdPercent,
                row.ChargingCompletionThresholdPercent,
                row.EstimatedTaskConsumptionPercent,
                row.ProgressStabilizationSeconds,
                row.ProgressObservationWindowSeconds,
                row.ProgressMinimumIncreasePercent,
                [.. scope.Order(StringComparer.Ordinal)]));
    }

    public async Task<IReadOnlyList<ChargingPolicyApproval>> ListApprovalsAsync(
        long version, CancellationToken cancellationToken)
    {
        ChargingPolicyApprovalRow[] rows = await _context.Set<ChargingPolicyApprovalRow>().AsNoTracking()
            .Where(row => row.Version == version)
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot order by a DateTimeOffset column.
        return
        [
            .. rows.OrderBy(row => row.ApprovedAt).ThenBy(row => row.ApprovalId, StringComparer.Ordinal).Select(ToModel)
        ];
    }

    private static ChargingPolicyApproval ToModel(ChargingPolicyApprovalRow row) =>
        new(row.ApprovalId, row.Version, row.ApprovedBy, row.ApproverRole, row.ApprovedAt, row.BasisReference, row.Source);

    private static string ContentJson(ChargingPolicyContent content) =>
        JsonSerializer.Serialize(new
        {
            minimumPostTaskBatteryMarginPercent = content.MinimumPostTaskBatteryMarginPercent,
            mandatoryChargeEntryThresholdPercent = content.MandatoryChargeEntryThresholdPercent,
            chargingCompletionThresholdPercent = content.ChargingCompletionThresholdPercent,
            estimatedTaskConsumptionPercent = content.EstimatedTaskConsumptionPercent,
            progressStabilizationSeconds = content.ProgressStabilizationSeconds,
            progressObservationWindowSeconds = content.ProgressObservationWindowSeconds,
            progressMinimumIncreasePercent = content.ProgressMinimumIncreasePercent,
            vehicleScope = content.VehicleScope
        });
}

/// <summary>
/// 充电周期。开始时周期行、<c>CHARGING</c> 用途占有与它的记录、<c>CHARGER</c> 预占与它的经过在同一次保存里插入，
/// 谁占到由约束决定，不先读后写。
/// </summary>
public sealed class ChargingCycleStore(ControlServerDbContext context) : IChargingCycleStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<ChargingCycleStartOutcome> TryStartAsync(
        ChargingCycleStart start, IReadOnlyCollection<object>? sameSave, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.CycleId, nameof(start));
        ArgumentException.ThrowIfNullOrWhiteSpace(start.VehicleKey, nameof(start));
        ArgumentException.ThrowIfNullOrWhiteSpace(start.JourneyId, nameof(start));

        VehiclePurposeClaim claim = new(start.VehicleKey, VehiclePurposes.Charging, start.JourneyId, start.AllocatedAt);
        StationExclusivityRequest station = new(
            start.MapId, start.StationId, StationExclusivityKinds.Charger, StationExclusivityStates.Reserved,
            WaitingPointVersion: null, ChargerRosterVersion: start.ChargerRosterVersion);
        StationExclusivityWrites.ForgetUnchangedClaims(_context, start.VehicleKey);
        StationExclusivityWrites.ForgetUnchangedStation(_context, start.MapId, start.StationId);
        List<object> staged =
        [
            new ChargingCycleRow
            {
                CycleId = start.CycleId,
                VehicleKey = start.VehicleKey,
                JourneyId = start.JourneyId,
                MapId = start.MapId,
                StationId = start.StationId,
                ChargerRosterVersion = start.ChargerRosterVersion,
                ChargingPolicyVersion = start.ChargingPolicyVersion,
                WireState = ChargingCycleWireStates.Allocated,
                Phase = ChargingCyclePhases.Active,
                AllocatedAt = start.AllocatedAt,
                Version = 1
            },
            .. VehiclePurposeClaimWrites.NewRows(claim),
            .. StationExclusivityWrites.NewRows(station, start.VehicleKey, start.JourneyId, start.AllocatedAt),
            .. sameSave ?? [],
        ];
        _context.AddRange(staged);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return ChargingCycleStartOutcome.Started;
        }
        catch (DbUpdateException failure) when (StationExclusivityWrites.IsKeyConflict(failure))
        {
            // One of the keys refused the save, and it took every row of this start back with it. Which key refused says
            // little -- EF may insert in any order -- so the answer is read from who holds what now.
            foreach (object row in staged)
            {
                _context.Entry(row).State = EntityState.Detached;
            }
            if (await ReadAsync(start.CycleId, cancellationToken) is not null)
            {
                return ChargingCycleStartOutcome.AlreadyStarted;
            }
            if (await ReadOpenAsync(start.VehicleKey, cancellationToken) is not null)
            {
                return ChargingCycleStartOutcome.OpenCycleExists;
            }
            VehiclePurposeClaimRow? vehicleHolder = await _context.Set<VehiclePurposeClaimRow>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.VehicleKey == start.VehicleKey, cancellationToken);
            if (vehicleHolder is not null)
            {
                return ChargingCycleStartOutcome.VehicleHeld;
            }
            StationExclusivityRow? stationHolder = await _context.Set<StationExclusivityRow>().AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.MapId == start.MapId && row.StationId == start.StationId, cancellationToken);
            if (stationHolder is not null)
            {
                return ChargingCycleStartOutcome.StationHeld;
            }
            // Nothing is held now: whoever refused this one let go in between, and the caller's next round decides again.
            return StationExclusivityWrites.IsStationConflict(failure)
                ? ChargingCycleStartOutcome.StationHeld
                : ChargingCycleStartOutcome.VehicleHeld;
        }
    }

    public async Task<bool> UpdateAsync(ChargingCycle cycle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentException.ThrowIfNullOrWhiteSpace(cycle.WireState, nameof(cycle));
        ArgumentException.ThrowIfNullOrWhiteSpace(cycle.Phase, nameof(cycle));
        foreach (var stale in _context.ChangeTracker.Entries<ChargingCycleRow>()
                     .Where(entry => entry.Entity.CycleId == cycle.CycleId).ToArray())
        {
            stale.State = EntityState.Detached;
        }
        ChargingCycleRow? row = await _context.Set<ChargingCycleRow>()
            .SingleOrDefaultAsync(item => item.CycleId == cycle.CycleId, cancellationToken);
        if (row is null || row.Version != cycle.Version)
        {
            return false;
        }
        row.WireState = cycle.WireState;
        row.Phase = cycle.Phase;
        row.UpperId = cycle.UpperId;
        row.OrderConfirmedAt = cycle.OrderConfirmedAt;
        row.ArrivedAt = cycle.ArrivedAt;
        row.FirstChargingSeenAt = cycle.FirstChargingSeenAt;
        row.CompletedAt = cycle.CompletedAt;
        row.DepartedAt = cycle.DepartedAt;
        row.ReleasedAt = cycle.ReleasedAt;
        row.EndedAt = cycle.EndedAt;
        row.EndReason = cycle.EndReason;
        row.ObservationWindowStartedAt = cycle.ObservationWindowStartedAt;
        row.ObservationWindowStartPercent = cycle.ObservationWindowStartPercent;
        row.LastSampleAt = cycle.LastSampleAt;
        row.LastSamplePercent = cycle.LastSamplePercent;
        row.Version = cycle.Version + 1;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Advanced through another context after this one read the row: nothing of this change was written. Only
            // this row is forgotten; whatever else the caller tracks stays as it was.
            _context.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<ChargingCycle?> ReadAsync(string cycleId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleId);
        ChargingCycleRow? row = await _context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.CycleId == cycleId, cancellationToken);
        return row is null ? null : ToModel(row);
    }

    public async Task<ChargingCycle?> ReadOpenAsync(string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ChargingCycleRow? row = await _context.Set<ChargingCycleRow>().AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.VehicleKey == vehicleKey && item.Phase != ChargingCyclePhases.Ended, cancellationToken);
        return row is null ? null : ToModel(row);
    }

    private static ChargingCycle ToModel(ChargingCycleRow row) => new(
        row.CycleId, row.VehicleKey, row.JourneyId, row.MapId, row.StationId, row.ChargerRosterVersion,
        row.ChargingPolicyVersion, row.WireState, row.Phase, row.UpperId, row.AllocatedAt, row.OrderConfirmedAt,
        row.ArrivedAt, row.FirstChargingSeenAt, row.CompletedAt, row.DepartedAt, row.ReleasedAt, row.EndedAt,
        row.EndReason, row.ObservationWindowStartedAt, row.ObservationWindowStartPercent, row.LastSampleAt,
        row.LastSamplePercent, row.Version);
}

/// <summary>
/// 桩分配暂停与车辆充电资格暂停。暂停由幂等键的唯一约束去重，恢复由暂停 id 的唯一约束去重；两处都不先读后写。
/// </summary>
public sealed class ChargingHoldStore(ControlServerDbContext context) : IChargingHoldStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<HoldWriteResult<ChargingStationAllocationHold>> RecordStationHoldAsync(
        ChargingStationAllocationHold hold, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentException.ThrowIfNullOrWhiteSpace(hold.HoldId, nameof(hold));
        ArgumentException.ThrowIfNullOrWhiteSpace(hold.IdempotencyKey, nameof(hold));
        ArgumentException.ThrowIfNullOrWhiteSpace(hold.Trigger, nameof(hold));
        ChargingStationAllocationHoldRow row = new()
        {
            HoldId = hold.HoldId,
            IdempotencyKey = hold.IdempotencyKey,
            Trigger = hold.Trigger,
            RootCause = ChargingHoldRootCauses.Unknown,
            MapId = hold.MapId,
            StationId = hold.StationId,
            ChargerRosterVersion = hold.ChargerRosterVersion,
            VehicleKey = hold.VehicleKey,
            ReservationRecordId = hold.ReservationRecordId,
            CycleId = hold.CycleId,
            UpperId = hold.UpperId,
            OrderId = hold.OrderId,
            ArrivedAt = hold.ArrivedAt,
            ChargingStartedAt = hold.ChargingStartedAt,
            FailedAt = hold.FailedAt,
            FinalHangAt = hold.FinalHangAt,
            ConfirmedAt = hold.ConfirmedAt,
            HeldAt = hold.HeldAt,
            RawPositionJson = hold.RawPositionJson,
            RawOrderJson = hold.RawOrderJson,
            RawActionResultJson = hold.RawActionResultJson,
            RawBatteryJson = hold.RawBatteryJson,
            EvidenceReference = hold.EvidenceReference,
            RiotBuild = hold.RiotBuild,
            RiotContractVersion = hold.RiotContractVersion,
            ConfirmedByPersonId = hold.ConfirmedByPersonId,
            ConfirmedByRole = hold.ConfirmedByRole,
            ConfirmedAuthenticatedAt = hold.ConfirmedAuthenticatedAt,
            SiteDisposition = hold.SiteDisposition
        };
        if (await ChargingWrites.TryInsertAsync(_context, row, cancellationToken))
        {
            return new(ToModel(row), Created: true);
        }
        ChargingStationAllocationHoldRow existing = await _context.Set<ChargingStationAllocationHoldRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.IdempotencyKey == hold.IdempotencyKey, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Charging station hold {hold.HoldId} was refused by a key, but no hold carries its idempotency key.");
        return new(ToModel(existing), Created: false);
    }

    public Task<ChargingHoldRecovery> RecoverStationHoldAsync(
        ChargingHoldRecovery recovery, CancellationToken cancellationToken) =>
        RecoverAsync<ChargingStationAllocationHoldRow, ChargingStationRecoveryRow>(
            recovery,
            hold => hold.HoldId == recovery.HoldId,
            () => new ChargingStationRecoveryRow
            {
                RecoveryId = recovery.RecoveryId,
                HoldId = recovery.HoldId,
                RecoveredBy = recovery.RecoveredBy,
                RecovererRole = recovery.RecovererRole,
                RecoveredAt = recovery.RecoveredAt,
                Basis = recovery.Basis
            },
            row => new ChargingHoldRecovery(
                row.RecoveryId, row.HoldId, row.RecoveredBy, row.RecovererRole, row.RecoveredAt, row.Basis),
            row => row.HoldId == recovery.HoldId,
            cancellationToken);

    public async Task<IReadOnlyList<ChargingStationAllocationHold>> ListActiveStationHoldsAsync(
        int mapId, int stationId, CancellationToken cancellationToken)
    {
        IQueryable<string> recovered = _context.Set<ChargingStationRecoveryRow>().Select(row => row.HoldId);
        ChargingStationAllocationHoldRow[] rows = await _context.Set<ChargingStationAllocationHoldRow>().AsNoTracking()
            .Where(row => row.MapId == mapId && row.StationId == stationId && !recovered.Contains(row.HoldId))
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot order by a DateTimeOffset column.
        return [.. rows.OrderBy(row => row.HeldAt).ThenBy(row => row.HoldId, StringComparer.Ordinal).Select(ToModel)];
    }

    public async Task<ChargingStationAllocationHold?> ReadStationHoldAsync(
        string holdId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holdId);
        ChargingStationAllocationHoldRow? row = await _context.Set<ChargingStationAllocationHoldRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.HoldId == holdId, cancellationToken);
        return row is null ? null : ToModel(row);
    }

    public async Task<HoldWriteResult<VehicleChargingEligibilityHold>> RecordVehicleHoldAsync(
        VehicleChargingEligibilityHold hold, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentException.ThrowIfNullOrWhiteSpace(hold.HoldId, nameof(hold));
        ArgumentException.ThrowIfNullOrWhiteSpace(hold.IdempotencyKey, nameof(hold));
        ArgumentException.ThrowIfNullOrWhiteSpace(hold.VehicleKey, nameof(hold));
        ArgumentException.ThrowIfNullOrWhiteSpace(hold.Reason, nameof(hold));
        VehicleChargingEligibilityHoldRow row = new()
        {
            HoldId = hold.HoldId,
            IdempotencyKey = hold.IdempotencyKey,
            VehicleKey = hold.VehicleKey,
            CycleId = hold.CycleId,
            Reason = hold.Reason,
            HeldAt = hold.HeldAt,
            EvidenceReference = hold.EvidenceReference
        };
        if (await ChargingWrites.TryInsertAsync(_context, row, cancellationToken))
        {
            return new(ToModel(row), Created: true);
        }
        VehicleChargingEligibilityHoldRow existing = await _context.Set<VehicleChargingEligibilityHoldRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.IdempotencyKey == hold.IdempotencyKey, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Vehicle charging hold {hold.HoldId} was refused by a key, but no hold carries its idempotency key.");
        return new(ToModel(existing), Created: false);
    }

    public Task<ChargingHoldRecovery> RecoverVehicleHoldAsync(
        ChargingHoldRecovery recovery, CancellationToken cancellationToken) =>
        RecoverAsync<VehicleChargingEligibilityHoldRow, VehicleChargingEligibilityRecoveryRow>(
            recovery,
            hold => hold.HoldId == recovery.HoldId,
            () => new VehicleChargingEligibilityRecoveryRow
            {
                RecoveryId = recovery.RecoveryId,
                HoldId = recovery.HoldId,
                RecoveredBy = recovery.RecoveredBy,
                RecovererRole = recovery.RecovererRole,
                RecoveredAt = recovery.RecoveredAt,
                Basis = recovery.Basis
            },
            row => new ChargingHoldRecovery(
                row.RecoveryId, row.HoldId, row.RecoveredBy, row.RecovererRole, row.RecoveredAt, row.Basis),
            row => row.HoldId == recovery.HoldId,
            cancellationToken);

    public async Task<IReadOnlyList<VehicleChargingEligibilityHold>> ListActiveVehicleHoldsAsync(
        string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        IQueryable<string> recovered = _context.Set<VehicleChargingEligibilityRecoveryRow>().Select(row => row.HoldId);
        VehicleChargingEligibilityHoldRow[] rows = await _context.Set<VehicleChargingEligibilityHoldRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey && !recovered.Contains(row.HoldId))
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot order by a DateTimeOffset column.
        return [.. rows.OrderBy(row => row.HeldAt).ThenBy(row => row.HoldId, StringComparer.Ordinal).Select(ToModel)];
    }

    private async Task<ChargingHoldRecovery> RecoverAsync<THold, TRecovery>(
        ChargingHoldRecovery recovery,
        System.Linq.Expressions.Expression<Func<THold, bool>> isHold,
        Func<TRecovery> newRow,
        Func<TRecovery, ChargingHoldRecovery> toModel,
        System.Linq.Expressions.Expression<Func<TRecovery, bool>> ofHold,
        CancellationToken cancellationToken)
        where THold : class
        where TRecovery : class
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentException.ThrowIfNullOrWhiteSpace(recovery.RecoveryId, nameof(recovery));
        ArgumentException.ThrowIfNullOrWhiteSpace(recovery.HoldId, nameof(recovery));
        ArgumentException.ThrowIfNullOrWhiteSpace(recovery.RecoveredBy, nameof(recovery));
        ArgumentException.ThrowIfNullOrWhiteSpace(recovery.RecovererRole, nameof(recovery));
        ArgumentException.ThrowIfNullOrWhiteSpace(recovery.Basis, nameof(recovery));
        if (!await _context.Set<THold>().AnyAsync(isHold, cancellationToken))
        {
            throw new InvalidOperationException($"Hold {recovery.HoldId} does not exist and cannot be recovered.");
        }
        TRecovery row = newRow();
        if (await ChargingWrites.TryInsertAsync(_context, row, cancellationToken))
        {
            return toModel(row);
        }
        TRecovery existing = await _context.Set<TRecovery>().AsNoTracking().SingleAsync(ofHold, cancellationToken);
        return toModel(existing);
    }

    private static ChargingStationAllocationHold ToModel(ChargingStationAllocationHoldRow row) => new(
        row.HoldId, row.IdempotencyKey, row.Trigger, row.MapId, row.StationId, row.ChargerRosterVersion, row.VehicleKey,
        row.ReservationRecordId, row.CycleId, row.UpperId, row.OrderId, row.ArrivedAt, row.ChargingStartedAt, row.FailedAt,
        row.FinalHangAt, row.ConfirmedAt, row.HeldAt, row.RawPositionJson, row.RawOrderJson, row.RawActionResultJson,
        row.RawBatteryJson, row.EvidenceReference, row.RiotBuild, row.RiotContractVersion, row.ConfirmedByPersonId,
        row.ConfirmedByRole, row.ConfirmedAuthenticatedAt, row.SiteDisposition);

    private static VehicleChargingEligibilityHold ToModel(VehicleChargingEligibilityHoldRow row) =>
        new(row.HoldId, row.IdempotencyKey, row.VehicleKey, row.CycleId, row.Reason, row.HeldAt, row.EvidenceReference);
}

/// <summary>清桩记录。一个周期一条由 <c>CycleId</c> 的唯一约束保证；完成只写一次，由并发令牌保证。</summary>
public sealed class StationClearanceStore(ControlServerDbContext context) : IStationClearanceStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<StationClearance> StartAsync(
        string clearanceId,
        string cycleId,
        string vehicleKey,
        int mapId,
        int stationId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clearanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        StationClearanceRow row = new()
        {
            ClearanceId = clearanceId,
            CycleId = cycleId,
            VehicleKey = vehicleKey,
            MapId = mapId,
            StationId = stationId,
            StartedAt = startedAt,
            AssistantsJson = "[]"
        };
        if (await ChargingWrites.TryInsertAsync(_context, row, cancellationToken))
        {
            return ToModel(row);
        }
        return await ReadByCycleAsync(cycleId, cancellationToken)
               ?? throw new InvalidOperationException(
                   $"Station clearance {clearanceId} was refused by a key, but cycle {cycleId} has no clearance.");
    }

    public async Task<bool> CompleteAsync(
        string clearanceId, StationClearanceCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clearanceId);
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentException.ThrowIfNullOrWhiteSpace(completion.Proof, nameof(completion));
        ArgumentNullException.ThrowIfNull(completion.Assistants, nameof(completion));
        foreach (var stale in _context.ChangeTracker.Entries<StationClearanceRow>()
                     .Where(entry => entry.Entity.ClearanceId == clearanceId).ToArray())
        {
            stale.State = EntityState.Detached;
        }
        StationClearanceRow row = await _context.Set<StationClearanceRow>()
            .SingleOrDefaultAsync(item => item.ClearanceId == clearanceId, cancellationToken)
            ?? throw new InvalidOperationException($"Station clearance {clearanceId} does not exist.");
        if (row.CompletedAt is not null)
        {
            return false;
        }
        row.CompletedAt = completion.CompletedAt;
        row.Proof = completion.Proof;
        row.WaitingPointMapId = completion.WaitingPointMapId;
        row.WaitingPointStationId = completion.WaitingPointStationId;
        row.ConfirmedBy = completion.ConfirmedBy;
        row.ConfirmedByRole = completion.ConfirmedByRole;
        row.ConfirmedAt = completion.ConfirmedAt;
        row.VehicleFinalPosition = completion.VehicleFinalPosition;
        row.OldOrderDisposition = completion.OldOrderDisposition;
        row.AssistantsJson = JsonSerializer.Serialize(completion.Assistants);
        row.ClearedCondition = completion.ClearedCondition;
        row.ConfirmationRequestId = completion.ConfirmationRequestId;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Completed through another context after this one read the row. Only this row is forgotten.
            _context.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<StationClearance?> ReadByCycleAsync(string cycleId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleId);
        StationClearanceRow? row = await _context.Set<StationClearanceRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.CycleId == cycleId, cancellationToken);
        return row is null ? null : ToModel(row);
    }

    private static StationClearance ToModel(StationClearanceRow row) => new(
        row.ClearanceId, row.CycleId, row.VehicleKey, row.MapId, row.StationId, row.StartedAt, row.CompletedAt, row.Proof,
        row.WaitingPointMapId, row.WaitingPointStationId, row.ConfirmedBy, row.ConfirmedByRole, row.ConfirmedAt,
        row.VehicleFinalPosition, row.OldOrderDisposition,
        JsonSerializer.Deserialize<string[]>(row.AssistantsJson) ?? [],
        row.ClearedCondition,
        row.ConfirmationRequestId);
}

/// <summary>
/// 服务端持有的人工充电等待。当前行（主键一车一行）与它的经过同一次保存生灭，谁置上由主键决定。
/// </summary>
public sealed class ManualChargingHoldStore(ControlServerDbContext context) : IManualChargingHoldStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<ManualChargingHoldPlacement> PlaceAsync(
        string holdId, string vehicleKey, string reason, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holdId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ForgetUnchanged(vehicleKey);
        object[] staged =
        [
            new ManualChargingHoldRow { VehicleKey = vehicleKey, HoldId = holdId, Reason = reason, Since = since },
            new ManualChargingHoldRecordRow { HoldId = holdId, VehicleKey = vehicleKey, Reason = reason, Since = since },
        ];
        _context.AddRange(staged);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return ManualChargingHoldPlacement.Placed;
        }
        catch (DbUpdateException failure) when (StationExclusivityWrites.IsKeyConflict(failure))
        {
            foreach (object row in staged)
            {
                _context.Entry(row).State = EntityState.Detached;
            }
            return ManualChargingHoldPlacement.AlreadyHeld;
        }
    }

    public async Task<bool> MarkWarnedAsync(string vehicleKey, DateTimeOffset warnedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ForgetUnchanged(vehicleKey);
        ManualChargingHoldRow? row = await _context.Set<ManualChargingHoldRow>()
            .SingleOrDefaultAsync(item => item.VehicleKey == vehicleKey, cancellationToken);
        if (row is null)
        {
            return false;
        }
        ManualChargingHoldRecordRow record = await _context.Set<ManualChargingHoldRecordRow>()
            .SingleAsync(item => item.HoldId == row.HoldId, cancellationToken);
        row.WarnedAt = warnedAt;
        record.WarnedAt = warnedAt;
        return await SaveUnlessMovedAsync([row, record], cancellationToken);
    }

    public async Task<bool> ReleaseAsync(
        string vehicleKey, string releaseRequestId, DateTimeOffset releasedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseRequestId);
        ForgetUnchanged(vehicleKey);
        ManualChargingHoldRow? row = await _context.Set<ManualChargingHoldRow>()
            .SingleOrDefaultAsync(item => item.VehicleKey == vehicleKey, cancellationToken);
        if (row is null)
        {
            return false;
        }
        ManualChargingHoldRecordRow record = await _context.Set<ManualChargingHoldRecordRow>()
            .SingleAsync(item => item.HoldId == row.HoldId, cancellationToken);
        _context.Set<ManualChargingHoldRow>().Remove(row);
        record.ReleasedAt = releasedAt;
        record.ReleaseRequestId = releaseRequestId;
        return await SaveUnlessMovedAsync([row, record], cancellationToken);
    }

    public async Task<ManualChargingHold?> ReadAsync(string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ManualChargingHoldRow? row = await _context.Set<ManualChargingHoldRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.VehicleKey == vehicleKey, cancellationToken);
        return row is null
            ? null
            : new ManualChargingHold(row.HoldId, row.VehicleKey, row.Reason, row.Since, row.WarnedAt, null, null);
    }

    public async Task<IReadOnlyList<ManualChargingHold>> ListHistoryAsync(
        string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ManualChargingHoldRecordRow[] rows = await _context.Set<ManualChargingHoldRecordRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey)
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot order by a DateTimeOffset column.
        return
        [
            .. rows.OrderBy(row => row.Since).ThenBy(row => row.HoldId, StringComparer.Ordinal).Select(row =>
                new ManualChargingHold(
                    row.HoldId, row.VehicleKey, row.Reason, row.Since, row.WarnedAt, row.ReleasedAt, row.ReleaseRequestId))
        ];
    }

    private async Task<bool> SaveUnlessMovedAsync(object[] rows, CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Released, or released and placed again, through another context after this one read the row. Only the
            // rows this change touched are forgotten; whatever else the caller tracks stays as it was.
            foreach (object row in rows)
            {
                _context.Entry(row).State = EntityState.Detached;
            }
            return false;
        }
    }

    // See StationExclusivityWrites.ForgetUnchangedClaims: a row this context saw earlier may since have been released
    // through another context.
    private void ForgetUnchanged(string vehicleKey)
    {
        foreach (var stale in _context.ChangeTracker.Entries<ManualChargingHoldRow>()
                     .Where(entry => entry.State == EntityState.Unchanged && entry.Entity.VehicleKey == vehicleKey)
                     .ToArray())
        {
            stale.State = EntityState.Detached;
        }
    }
}

/// <summary>
/// 两类现场确认请求。第一次的判定由主键 <c>ConfirmationRequestId</c> 决定：谁先插入谁的判定算数，之后同一个 id 一律拿回它。
/// </summary>
public sealed class FieldConfirmationRequestStore(ControlServerDbContext context) : IFieldConfirmationRequestStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<UnableToChargeFieldConfirmation> DecideUnableToChargeAsync(
        UnableToChargeFieldConfirmation confirmation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        FieldConfirmationRequestIdentity request = Validate(confirmation.Request, confirmation.Decision);
        UnableToChargeFieldConfirmation? decided =
            await ReadUnableToChargeAsync(request.ConfirmationRequestId, cancellationToken);
        if (decided is null)
        {
            UnableToChargeFieldConfirmationRow row = new()
            {
                ConfirmationRequestId = request.ConfirmationRequestId,
                AgvId = request.AgvId,
                SessionGeneration = request.SessionGeneration,
                RequestMessageId = request.RequestMessageId,
                RequestContentHash = request.RequestContentHash,
                OperatorId = request.OperatorId,
                VerificationMethod = request.VerificationMethod,
                VerifiedAt = request.VerifiedAt,
                ObservedAt = request.ObservedAt,
                ChargerStationId = confirmation.ChargerStationId,
                ObservedCondition = confirmation.ObservedCondition,
                Outcome = confirmation.Decision.Outcome,
                ProblemReasonCode = confirmation.Decision.ProblemReasonCode,
                ProblemFieldPath = confirmation.Decision.ProblemFieldPath,
                ProblemDisplayMessage = confirmation.Decision.ProblemDisplayMessage,
                ChargingPolicyDecision = confirmation.ChargingPolicyDecision,
                DecidedAt = confirmation.Decision.DecidedAt
            };
            if (await ChargingWrites.TryInsertAsync(_context, row, cancellationToken))
            {
                return confirmation;
            }
            // Decided through another context between the read and the insert: its decision is the one.
            decided = await ReadUnableToChargeAsync(request.ConfirmationRequestId, cancellationToken)
                      ?? throw new InvalidOperationException(
                          $"Confirmation request {request.ConfirmationRequestId} was refused by a key it does not hold.");
        }
        EnsureSameRequest(decided.Request, request);
        return decided;
    }

    public async Task<UnableToChargeFieldConfirmation?> ReadUnableToChargeAsync(
        string confirmationRequestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationRequestId);
        UnableToChargeFieldConfirmationRow? row = await _context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.ConfirmationRequestId == confirmationRequestId, cancellationToken);
        return row is null
            ? null
            : new UnableToChargeFieldConfirmation(
                new FieldConfirmationRequestIdentity(
                    row.ConfirmationRequestId, row.AgvId, row.SessionGeneration, row.RequestMessageId, row.RequestContentHash,
                    row.OperatorId, row.VerificationMethod, row.VerifiedAt, row.ObservedAt),
                row.ChargerStationId,
                row.ObservedCondition,
                new FieldConfirmationDecision(
                    row.Outcome, row.ProblemReasonCode, row.ProblemFieldPath, row.ProblemDisplayMessage, row.DecidedAt),
                row.ChargingPolicyDecision);
    }

    public async Task<ManualStationClearanceConfirmation> DecideManualStationClearanceAsync(
        ManualStationClearanceConfirmation confirmation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        FieldConfirmationRequestIdentity request = Validate(confirmation.Request, confirmation.Decision);
        ManualStationClearanceConfirmation? decided =
            await ReadManualStationClearanceAsync(request.ConfirmationRequestId, cancellationToken);
        if (decided is null)
        {
            ManualStationClearanceConfirmationRow row = new()
            {
                ConfirmationRequestId = request.ConfirmationRequestId,
                AgvId = request.AgvId,
                SessionGeneration = request.SessionGeneration,
                RequestMessageId = request.RequestMessageId,
                RequestContentHash = request.RequestContentHash,
                OperatorId = request.OperatorId,
                VerificationMethod = request.VerificationMethod,
                VerifiedAt = request.VerifiedAt,
                ObservedAt = request.ObservedAt,
                StationId = confirmation.StationId,
                PublicStationFunction = confirmation.PublicStationFunction,
                ClearedCondition = confirmation.ClearedCondition,
                Outcome = confirmation.Decision.Outcome,
                ProblemReasonCode = confirmation.Decision.ProblemReasonCode,
                ProblemFieldPath = confirmation.Decision.ProblemFieldPath,
                ProblemDisplayMessage = confirmation.Decision.ProblemDisplayMessage,
                StationReleased = confirmation.StationReleased,
                DecidedAt = confirmation.Decision.DecidedAt
            };
            if (await ChargingWrites.TryInsertAsync(_context, row, cancellationToken))
            {
                return confirmation;
            }
            decided = await ReadManualStationClearanceAsync(request.ConfirmationRequestId, cancellationToken)
                      ?? throw new InvalidOperationException(
                          $"Confirmation request {request.ConfirmationRequestId} was refused by a key it does not hold.");
        }
        EnsureSameRequest(decided.Request, request);
        return decided;
    }

    public async Task<ManualStationClearanceConfirmation?> ReadManualStationClearanceAsync(
        string confirmationRequestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationRequestId);
        ManualStationClearanceConfirmationRow? row = await _context.Set<ManualStationClearanceConfirmationRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ConfirmationRequestId == confirmationRequestId, cancellationToken);
        return row is null
            ? null
            : new ManualStationClearanceConfirmation(
                new FieldConfirmationRequestIdentity(
                    row.ConfirmationRequestId, row.AgvId, row.SessionGeneration, row.RequestMessageId, row.RequestContentHash,
                    row.OperatorId, row.VerificationMethod, row.VerifiedAt, row.ObservedAt),
                row.StationId,
                row.PublicStationFunction,
                row.ClearedCondition,
                new FieldConfirmationDecision(
                    row.Outcome, row.ProblemReasonCode, row.ProblemFieldPath, row.ProblemDisplayMessage, row.DecidedAt),
                row.StationReleased);
    }

    private static FieldConfirmationRequestIdentity Validate(
        FieldConfirmationRequestIdentity request, FieldConfirmationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConfirmationRequestId, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgvId, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestContentHash, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.Outcome, nameof(decision));
        return request;
    }

    // The same id with other content is a conflict, never a replay: answering it with the first decision would confirm
    // something nobody asked about.
    private static void EnsureSameRequest(FieldConfirmationRequestIdentity decided, FieldConfirmationRequestIdentity asked)
    {
        if (decided.RequestContentHash != asked.RequestContentHash || decided.AgvId != asked.AgvId)
        {
            throw new FieldConfirmationContentConflictException(
                $"Confirmation request {asked.ConfirmationRequestId} was replayed with different content.");
        }
    }
}

/// <summary>What the charging stores share.</summary>
internal static class ChargingWrites
{
    /// <summary>
    /// Inserts one row and saves. A primary key or unique index refusing it answers false with nothing written and the
    /// row forgotten; any other failure (a CHECK, NOT NULL) propagates, because it is a bad request, not a conflict.
    /// </summary>
    internal static async Task<bool> TryInsertAsync(
        ControlServerDbContext context, object row, CancellationToken cancellationToken)
    {
        context.Add(row);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException failure) when (StationExclusivityWrites.IsKeyConflict(failure))
        {
            context.Entry(row).State = EntityState.Detached;
            return false;
        }
    }
}

/// <summary>The content digest the snapshot store records (<c>GovernanceStore</c>), so unchanged content is recognised by it.</summary>
internal static class ChargingContent
{
    internal static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
