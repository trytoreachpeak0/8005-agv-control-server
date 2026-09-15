using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 分区归属表（含开门侧列）的版本存取（REQ-0350）。
/// </summary>
/// <remarks>
/// 每写一版都经 <see cref="GovernedConfigurationPublisher"/> 冻结整表快照并写业务审计，与版本行、指派行在同一个事务里，
/// 所以不会出现「有快照没版本」或「有版本没快照」。版本行与指派行写入即不可改，由
/// <see cref="PublishedVersionImmutabilityGuard"/> 在上下文上拦。
/// </remarks>
public sealed class AreaAssignmentStore(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher) : IAreaAssignmentStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));

    public async Task<AreaAssignmentTableVersion?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        long? current = await _context.Set<DispatchZoneAreaAssignmentVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken);
        return current is null ? null : await ReadVersionAsync(current.Value, cancellationToken);
    }

    public async Task<AreaAssignmentTableVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken)
    {
        DispatchZoneAreaAssignmentVersionRow? header = await _context.Set<DispatchZoneAreaAssignmentVersionRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Version == version, cancellationToken);
        if (header is null)
        {
            return null;
        }

        DispatchZoneAreaAssignmentRow[] rows = await _context.Set<DispatchZoneAreaAssignmentRow>()
            .AsNoTracking()
            .Where(row => row.Version == version)
            .ToArrayAsync(cancellationToken);
        return new AreaAssignmentTableVersion(
            header.Version,
            header.ContentSha256,
            header.SnapshotId,
            header.ImportedAt,
            rows.ToDictionary(
                row => row.Area,
                row => new AreaAssignment(row.Area, row.DispatchZone, row.SlotPosition),
                StringComparer.Ordinal));
    }

    public async Task<AreaAssignmentTableVersion> WriteVersionAsync(
        IReadOnlyList<AreaAssignment> assignments,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        foreach (AreaAssignment assignment in assignments)
        {
            ArgumentNullException.ThrowIfNull(assignment, nameof(assignments));
            ArgumentException.ThrowIfNullOrWhiteSpace(assignment.Area, nameof(assignments));
            ArgumentException.ThrowIfNullOrWhiteSpace(assignment.DispatchZone, nameof(assignments));
            ArgumentException.ThrowIfNullOrWhiteSpace(assignment.SlotPosition, nameof(assignments));
        }
        string[] duplicated =
        [
            .. assignments.GroupBy(assignment => assignment.Area, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .Order(StringComparer.Ordinal)
        ];
        if (duplicated.Length > 0)
        {
            throw new ArgumentException(
                $"An area assignment table names each AREA at most once; repeated: {string.Join(", ", duplicated)}.",
                nameof(assignments));
        }

        // Ordered so that the same table always freezes to the same content and the same SHA-256.
        AreaAssignment[] ordered = [.. assignments.OrderBy(assignment => assignment.Area, StringComparer.Ordinal)];

        await using IDbContextTransaction? transaction = _context.Database.CurrentTransaction is null
            ? await _context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        long version = (await _context.Set<DispatchZoneAreaAssignmentVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken) ?? 0) + 1;
        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.DispatchZoneAreaAssignment,
            DispatchZoneAreaAssignmentGovernance.ObjectId,
            version,
            ContentJson(ordered),
            DispatchZoneAreaAssignmentGovernance.VersionImportedAction,
            importedAt,
            cancellationToken);

        _context.Set<DispatchZoneAreaAssignmentVersionRow>().Add(new DispatchZoneAreaAssignmentVersionRow
        {
            Version = version,
            ContentSha256 = snapshot.ContentSha256,
            SnapshotId = snapshot.SnapshotId,
            EntryCount = ordered.Length,
            ImportedAt = importedAt
        });
        _context.Set<DispatchZoneAreaAssignmentRow>().AddRange(ordered.Select(assignment =>
            new DispatchZoneAreaAssignmentRow
            {
                Version = version,
                Area = assignment.Area,
                DispatchZone = assignment.DispatchZone,
                SlotPosition = assignment.SlotPosition
            }));
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new AreaAssignmentTableVersion(
            version,
            snapshot.ContentSha256,
            snapshot.SnapshotId,
            importedAt,
            ordered.ToDictionary(assignment => assignment.Area, StringComparer.Ordinal));
    }

    private static string ContentJson(IEnumerable<AreaAssignment> ordered) =>
        JsonSerializer.Serialize(new
        {
            entries = ordered.Select(assignment => new
            {
                area = assignment.Area,
                dispatchZone = assignment.DispatchZone,
                slotPosition = assignment.SlotPosition
            })
        });
}

/// <summary>
/// 需求冻结指派版本（REQ-0350），存在批次 3 的通用冻结表 <c>ConfigurationConsumerBindings</c> 里。
/// </summary>
/// <remarks>
/// 不给 <c>JourneyRuntimes</c> 加列：那张表批次 7 要改主键，现在加列等于让批次 7 多迁移一次。冻结表的主键
/// <c>(ConsumerKind, ConsumerId, ObjectKind, ObjectId)</c> 在这里正好是一需求一行。写入调用在 control-server#72。
/// </remarks>
public sealed class DemandAreaAssignmentFreezeStore(ControlServerDbContext context) : IDemandAreaAssignmentFreeze
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<DemandAreaAssignmentFreeze> FreezeAsync(
        string demandId,
        long version,
        DateTimeOffset frozenAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);

        ConfigurationConsumerBindingRow? existing = await DemandFreezes()
            .SingleOrDefaultAsync(row => row.ConsumerId == demandId, cancellationToken);
        if (existing is not null)
        {
            return existing.FrozenVersion == version
                ? Project(existing)
                : throw new DemandAreaAssignmentFreezeConflictException(
                    FormattableString.Invariant(
                        $"Demand {demandId} already froze area assignment version {existing.FrozenVersion}; ")
                    + FormattableString.Invariant(
                        $"it cannot be frozen again at version {version}. Later versions do not change a frozen demand."));
        }

        DispatchZoneAreaAssignmentVersionRow header = await _context.Set<DispatchZoneAreaAssignmentVersionRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Version == version, cancellationToken)
            ?? throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Area assignment version {version} does not exist, so demand {demandId} cannot freeze it."));
        ConfigurationConsumerBindingRow binding = new()
        {
            ConsumerKind = DispatchZoneAreaAssignmentGovernance.DemandConsumerKind,
            ConsumerId = demandId,
            ObjectKind = GovernedObjectKind.DispatchZoneAreaAssignment,
            ObjectId = DispatchZoneAreaAssignmentGovernance.ObjectId,
            FrozenVersion = header.Version,
            FrozenAt = frozenAt,
            SnapshotId = header.SnapshotId
        };
        _context.Set<ConfigurationConsumerBindingRow>().Add(binding);
        await _context.SaveChangesAsync(cancellationToken);
        return Project(binding);
    }

    public async Task<DemandAreaAssignmentFreeze?> ReadAsync(string demandId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);

        ConfigurationConsumerBindingRow? row = await DemandFreezes()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ConsumerId == demandId, cancellationToken);
        return row is null ? null : Project(row);
    }

    public async Task<IReadOnlyList<DemandAreaAssignmentFreeze>> ListInFlightAsync(CancellationToken cancellationToken)
    {
        ConfigurationConsumerBindingRow[] freezes = await DemandFreezes()
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
        string[] demandIds = [.. freezes.Select(row => row.ConsumerId)];
        HashSet<string> completed = new(
            await _context.Set<JourneyRuntimeRow>()
                .AsNoTracking()
                .Where(row => demandIds.Contains(row.DemandId) && row.Stage == JourneyRuntimeStage.Completed)
                .Select(row => row.DemandId)
                .ToArrayAsync(cancellationToken),
            StringComparer.Ordinal);
        return
        [
            .. freezes.Where(row => !completed.Contains(row.ConsumerId))
                .OrderBy(row => row.ConsumerId, StringComparer.Ordinal)
                .Select(Project)
        ];
    }

    private IQueryable<ConfigurationConsumerBindingRow> DemandFreezes() =>
        _context.Set<ConfigurationConsumerBindingRow>().Where(row =>
            row.ConsumerKind == DispatchZoneAreaAssignmentGovernance.DemandConsumerKind
            && row.ObjectKind == GovernedObjectKind.DispatchZoneAreaAssignment
            && row.ObjectId == DispatchZoneAreaAssignmentGovernance.ObjectId);

    private static DemandAreaAssignmentFreeze Project(ConfigurationConsumerBindingRow row) =>
        new(row.ConsumerId, row.FrozenVersion, row.SnapshotId, row.FrozenAt);
}

/// <summary>
/// 结构性派车阻断的存取（REQ-0210）。只存取，不判断什么算结构性无解——那是 control-server#74 的事。
/// </summary>
public sealed class StructuralDispatchBlockStore(ControlServerDbContext context) : IStructuralDispatchBlockStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<StructuralDispatchBlock> RaiseOrRefreshAsync(
        string demandId,
        string reasonCode,
        string transportDemandKey,
        string detailJson,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(transportDemandKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(detailJson);

        StructuralDispatchBlockRow? row = await _context.Set<StructuralDispatchBlockRow>()
            .SingleOrDefaultAsync(
                candidate => candidate.DemandId == demandId && candidate.ReasonCode == reasonCode,
                cancellationToken);
        if (row is null)
        {
            row = new StructuralDispatchBlockRow
            {
                DemandId = demandId,
                ReasonCode = reasonCode,
                TransportDemandKey = transportDemandKey,
                FirstRaisedAt = observedAt,
                LastSeenAt = observedAt,
                DetailJson = detailJson
            };
            _context.Set<StructuralDispatchBlockRow>().Add(row);
        }
        else if (row.ClearedAt is null)
        {
            // Still holding: the existing alert is updated, not re-raised (REQ-0210).
            row.LastSeenAt = observedAt;
        }
        else
        {
            // Held before, cleared, and holds again: a new episode in the same row.
            row.TransportDemandKey = transportDemandKey;
            row.FirstRaisedAt = observedAt;
            row.LastSeenAt = observedAt;
            row.ClearedAt = null;
            row.DetailJson = detailJson;
        }
        await _context.SaveChangesAsync(cancellationToken);
        return Project(row);
    }

    public async Task<bool> ClearAsync(
        string demandId,
        string reasonCode,
        DateTimeOffset clearedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        StructuralDispatchBlockRow? row = await _context.Set<StructuralDispatchBlockRow>()
            .SingleOrDefaultAsync(
                candidate => candidate.DemandId == demandId
                    && candidate.ReasonCode == reasonCode
                    && candidate.ClearedAt == null,
                cancellationToken);
        if (row is null)
        {
            return false;
        }
        row.ClearedAt = clearedAt;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<StructuralDispatchBlock>> ListUnclearedAsync(CancellationToken cancellationToken)
    {
        StructuralDispatchBlockRow[] rows = await _context.Set<StructuralDispatchBlockRow>()
            .AsNoTracking()
            .Where(row => row.ClearedAt == null)
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        return
        [
            .. rows.OrderBy(row => row.FirstRaisedAt)
                .ThenBy(row => row.DemandId, StringComparer.Ordinal)
                .ThenBy(row => row.ReasonCode, StringComparer.Ordinal)
                .Select(Project)
        ];
    }

    private static StructuralDispatchBlock Project(StructuralDispatchBlockRow row) =>
        new(row.DemandId, row.ReasonCode, row.TransportDemandKey, row.FirstRaisedAt, row.LastSeenAt, row.ClearedAt,
            row.DetailJson);
}

/// <summary>
/// 车辆各仓位的 SlotPosition 分组，取服务端对「这台车绑的是哪个整车模型」的正式记录，不取车报（program#70 定案 4）。
/// </summary>
/// <remarks>
/// <para>
/// 解析顺序固定：生效配置 <c>ActiveSlotConfigurations</c> 引用的模型；没有时退到该车最新一版已发布 IO 绑定引用的模型；
/// 都没有就是未解析，由调用方 fail-closed。不回退到已批准八仓事实的默认模型，也不按仓号区间推断。
/// </para>
/// <para>
/// 「最新一版 IO 绑定」按绑定时间取，时间相同再比版本号。版本号是按（车，模型）各自从 1 编号的
/// （<see cref="SlotConfigurationAuthorityStore.PublishIoBindingsAsync"/>），一台车先后绑过两个模型时，只比版本号会让
/// 改过两版的旧模型压过刚绑上的新模型。
/// </para>
/// </remarks>
public sealed class VehicleSlotPositionReader(ControlServerDbContext context) : IVehicleSlotPositionReader
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<VehicleSlotPositions?> ReadAsync(string agvId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);

        (string SlotModelVersionId, VehicleSlotPositionSource Source)? model =
            await ResolveModelAsync(agvId, cancellationToken);
        if (model is null)
        {
            return null;
        }

        string slotModelVersionId = model.Value.SlotModelVersionId;
        SlotModelSlotRow[] slots = await _context.Set<SlotModelSlotRow>()
            .AsNoTracking()
            .Where(row => row.SlotModelVersionId == slotModelVersionId)
            .ToArrayAsync(cancellationToken);
        // A record that points at a model with no slots names nothing usable. That is unresolved, not a
        // vehicle with zero slots in every group.
        return slots.Length == 0
            ? null
            : new VehicleSlotPositions(
                agvId,
                slotModelVersionId,
                model.Value.Source,
                slots.ToDictionary(row => row.PhysicalSlotNumber, row => row.SlotPosition));
    }

    public async Task<SlotPositionGroupCapacity> ReadLargestGroupCapacityAsync(
        IReadOnlyCollection<string> agvIds,
        string slotPosition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agvIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotPosition);

        int largest = 0;
        List<string> unresolved = [];
        foreach (string agvId in agvIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            VehicleSlotPositions? positions = await ReadAsync(agvId, cancellationToken);
            if (positions is null)
            {
                unresolved.Add(agvId);
                continue;
            }
            largest = Math.Max(largest, positions.PhysicalSlotCountByGroup.GetValueOrDefault(slotPosition));
        }
        return new SlotPositionGroupCapacity(slotPosition, largest, unresolved);
    }

    private async Task<(string SlotModelVersionId, VehicleSlotPositionSource Source)?> ResolveModelAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        string? active = await _context.Set<ActiveSlotConfigurationRow>()
            .AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .Select(row => row.SlotModelVersionId)
            .SingleOrDefaultAsync(cancellationToken);
        if (active is not null)
        {
            return (active, VehicleSlotPositionSource.ActiveSlotConfiguration);
        }

        var bindings = await _context.Set<SlotIoBindingRow>()
            .AsNoTracking()
            .Where(row => row.AgvId == agvId && row.Status == PublishedVersionImmutabilityGuard.PublishedStatus)
            .Select(row => new { row.SlotModelVersionId, row.Version, row.CreatedAt })
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        var latest = bindings
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Version)
            .FirstOrDefault();
        return latest is null ? null : (latest.SlotModelVersionId, VehicleSlotPositionSource.LatestPublishedIoBinding);
    }
}
