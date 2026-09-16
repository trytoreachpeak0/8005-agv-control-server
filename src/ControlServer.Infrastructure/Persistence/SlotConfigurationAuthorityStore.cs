using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 仓位配置的权威维护入口（REQ-0258）。
/// </summary>
/// <remarks>
/// 两层各自版本化、各自不可改写：<see cref="SlotTemplateRow"/> 是可被多个仓位共同引用的受控业务
/// 规格，<see cref="SlotModelVersionRow"/> 是引用模板并绑定物理编号与 SlotPosition 的另一层。改一层
/// 不静默影响另一层 —— 模板改了不会动已生效配置，要让车换配置只能走整车维护再激活。
///
/// 每次发布都经 <see cref="GovernedConfigurationPublisher"/>，所以每个版本都自带 #9 的完整快照与
/// 审计。本类不新增任何表。
/// </remarks>
public sealed class SlotConfigurationAuthorityStore(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher)
{
    private const string DraftStatus = "DRAFT";
    private const string PublishedStatus = "PUBLISHED";

    private readonly ControlServerDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));

    /// <summary>发布一版单仓模板。已发布的版本改不动，改内容只能发新版本。</summary>
    public async Task<SlotTemplateRow> PublishTemplateVersionAsync(
        string templateKey,
        SlotTemplateSpecification specification,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        ArgumentNullException.ThrowIfNull(specification);

        long version = await NextVersionAsync(
            _context.Set<SlotTemplateRow>().Where(row => row.TemplateKey == templateKey)
                .Select(row => row.Version),
            cancellationToken);
        SlotTemplateRow row = new()
        {
            SlotTemplateId = NewId(),
            TemplateKey = templateKey,
            Version = version,
            LengthMm = specification.LengthMm,
            WidthMm = specification.WidthMm,
            HeightMm = specification.HeightMm,
            CompatibleBasketTypesJson = JsonSerializer.Serialize(specification.CompatibleBasketTypes),
            Status = DraftStatus,
            CreatedAt = occurredAt
        };
        _context.Set<SlotTemplateRow>().Add(row);
        await _context.SaveChangesAsync(cancellationToken);

        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.SlotTemplate,
            templateKey,
            version,
            JsonSerializer.Serialize(specification),
            "SLOT_TEMPLATE_VERSION_PUBLISHED",
            occurredAt,
            cancellationToken);

        row.Status = PublishedStatus;
        row.PublishedAt = occurredAt;
        row.SnapshotId = snapshot.SnapshotId;
        await _context.SaveChangesAsync(cancellationToken);
        return row;
    }

    /// <summary>
    /// 发布一版整车模型。仓位集合、物理编号、SlotPosition 与模板引用一起冻结。
    /// </summary>
    public async Task<SlotModelVersionRow> PublishModelVersionAsync(
        string modelKey,
        IReadOnlyList<SlotModelSlotSpecification> slots,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count == 0)
        {
            throw new ArgumentException("A vehicle model version needs at least one slot.", nameof(slots));
        }

        long version = await NextVersionAsync(
            _context.Set<SlotModelVersionRow>().Where(row => row.ModelKey == modelKey)
                .Select(row => row.Version),
            cancellationToken);
        SlotModelVersionRow model = new()
        {
            SlotModelVersionId = NewId(),
            ModelKey = modelKey,
            Version = version,
            Status = DraftStatus,
            SlotCount = slots.Count,
            CreatedAt = occurredAt
        };
        _context.Set<SlotModelVersionRow>().Add(model);
        foreach (SlotModelSlotSpecification slot in slots)
        {
            SlotTemplateRow template = await RequirePublishedTemplateAsync(
                slot.SlotTemplateKey, slot.SlotTemplateVersion, cancellationToken);
            _context.Set<SlotModelSlotRow>().Add(new SlotModelSlotRow
            {
                SlotModelVersionId = model.SlotModelVersionId,
                PhysicalSlotNumber = slot.PhysicalSlotNumber,
                SlotPosition = slot.SlotPosition,
                SlotTemplateId = template.SlotTemplateId
            });
        }
        await _context.SaveChangesAsync(cancellationToken);

        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.SlotModelVersion,
            modelKey,
            version,
            JsonSerializer.Serialize(slots),
            "SLOT_MODEL_VERSION_PUBLISHED",
            occurredAt,
            cancellationToken);

        model.Status = PublishedStatus;
        model.PublishedAt = occurredAt;
        model.SnapshotId = snapshot.SnapshotId;
        await _context.SaveChangesAsync(cancellationToken);
        return model;
    }

    /// <summary>
    /// 录入一台车某一版模型下的 IO 绑定。这是硬件相关变更，只能从这条路进来。
    /// </summary>
    public async Task<IReadOnlyList<SlotIoBindingRow>> PublishIoBindingsAsync(
        string agvId,
        string slotModelVersionId,
        IReadOnlyList<SlotIoBindingSpecification> bindings,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotModelVersionId);
        ArgumentNullException.ThrowIfNull(bindings);

        // 绑定发布、激活与回滚冻结的是同一个治理对象（这台车在这一版车型下的仓位配置），共用一条版本线，
        // 所以版本号要越过这条线上已有的每一版快照，不能只看绑定行：激活或回滚占掉的版本号，冻结时会原样
        // 拿回那一版既有快照，新绑定行与它的发布审计就指到了别人的内容上。绑定行也算进来，是为了越过发布
        // 到一半、行已落地而快照没冻上的那一版。
        string objectId = FormattableString.Invariant($"{agvId}:{slotModelVersionId}");
        long version = await NextVersionAsync(
            _context.Set<SlotIoBindingRow>()
                .Where(row => row.AgvId == agvId && row.SlotModelVersionId == slotModelVersionId)
                .Select(row => row.Version)
                .Concat(_context.Set<GovernedConfigurationSnapshotRow>()
                    .Where(row => row.ObjectKind == GovernedObjectKind.ActiveSlotConfiguration
                        && row.ObjectId == objectId)
                    .Select(row => row.Version)),
            cancellationToken);
        List<SlotIoBindingRow> rows = [];
        foreach (SlotIoBindingSpecification binding in bindings)
        {
            SlotIoBindingRow row = new()
            {
                SlotIoBindingId = NewId(),
                AgvId = agvId,
                SlotModelVersionId = slotModelVersionId,
                PhysicalSlotNumber = binding.PhysicalSlotNumber,
                UnlockOutputPoint = binding.UnlockOutputPoint,
                LockFeedbackInputPoint = binding.LockFeedbackInputPoint,
                LightCurtainInputPoint = binding.LightCurtainInputPoint,
                SignalPolarity = binding.SignalPolarity,
                PulseResetMilliseconds = binding.PulseResetMilliseconds,
                Version = version,
                Status = DraftStatus,
                CreatedAt = occurredAt
            };
            rows.Add(row);
            _context.Set<SlotIoBindingRow>().Add(row);
        }
        await _context.SaveChangesAsync(cancellationToken);

        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.ActiveSlotConfiguration,
            objectId,
            version,
            JsonSerializer.Serialize(bindings),
            "SLOT_IO_BINDING_VERSION_PUBLISHED",
            occurredAt,
            cancellationToken);

        foreach (SlotIoBindingRow row in rows)
        {
            row.Status = PublishedStatus;
            row.SnapshotId = snapshot.SnapshotId;
        }
        await _context.SaveChangesAsync(cancellationToken);
        return rows;
    }

    /// <summary>
    /// 把 8005 三台现有车的八仓事实以已批准版本入库（REQ-0267）。幂等：已经入过就返回既有那一版。
    /// </summary>
    public async Task<SlotModelVersionRow> EnsureApprovedHardwareFactsAsync(
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        SlotModelVersionRow? existing = await _context.Set<SlotModelVersionRow>()
            .FirstOrDefaultAsync(
                row => row.ModelKey == ApprovedSlotHardwareFacts.ModelKey && row.Version == 1,
                cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        SlotTemplateRow template = await PublishTemplateVersionAsync(
            ApprovedSlotHardwareFacts.TemplateKey,
            new SlotTemplateSpecification(600, 400, 300, ["PDFN5", "TOLL"]),
            occurredAt,
            cancellationToken);
        SlotModelSlotSpecification[] slots =
        [
            .. Enumerable.Range(1, ApprovedSlotHardwareFacts.SlotCount).Select(number =>
                new SlotModelSlotSpecification(
                    number,
                    // 已批准硬件事实（REQ-0267）的数据录入，不是派车规则：分组从这里进整车模板，派车只读模板
                    // （REQ-0349 不许在派车或装卸规则里按仓号区间写死）。批次 4 之前写 LEFT／RIGHT，指同一侧仓门。
                    number <= 4 ? "FRONT" : "REAR",
                    template.TemplateKey,
                    template.Version))
        ];
        return await PublishModelVersionAsync(
            ApprovedSlotHardwareFacts.ModelKey, slots, occurredAt, cancellationToken);
    }

    /// <summary>
    /// 核验车载端报上来的配置声明。**只核验，不采信**：这个方法不写任何权威表。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 比对的对象是这台车**此刻跑着的那一份**配置，由 <see cref="VehicleSlotModelResolver"/> 解析——与派车
    /// 读仓位分组认车型的是同一份实现，不是另一份写法相同的副本。
    /// </para>
    /// <para>
    /// 有生效配置时比的是它冻结的那一版快照内容，不是那个车型下版本号最大的绑定行：回滚（取旧版内容重新
    /// 激活）与「激活之后又发布一版绑定但还没再激活」这两种情形下，两者指的不是同一份东西，按绑定行比会把
    /// 一台照生效内容接好线的车判成不一致。还没激活过的车才退到最新一版已发布绑定。
    /// </para>
    /// <para>
    /// 解析不出来（两份记录都没有、最新一版绑定跨车型并列、生效配置的快照读不出内容）时没有权威值可比，
    /// 声明一律不通过，每个报上来的仓位都记一条 <c>slot</c> 不符——fail-closed，不猜一个车型凑出「通过」。
    /// </para>
    /// </remarks>
    public async Task<VehicleDeclarationVerdict> VerifyVehicleDeclarationAsync(
        string agvId,
        IReadOnlyList<SlotIoBindingSpecification> declared,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentNullException.ThrowIfNull(declared);

        SlotIoBindingSpecification[] authoritative =
            await VehicleSlotModelResolver.ReadActiveContentAsync(_context, agvId, cancellationToken) ?? [];
        Dictionary<int, SlotIoBindingSpecification> bySlot =
            authoritative.ToDictionary(binding => binding.PhysicalSlotNumber);

        List<string> mismatched = [];
        foreach (SlotIoBindingSpecification claim in declared)
        {
            if (!bySlot.TryGetValue(claim.PhysicalSlotNumber, out SlotIoBindingSpecification? authority))
            {
                mismatched.Add(Field(claim.PhysicalSlotNumber, "slot"));
                continue;
            }
            AddIfDifferent(mismatched, claim.PhysicalSlotNumber, "unlockOutputPoint",
                claim.UnlockOutputPoint, authority.UnlockOutputPoint);
            AddIfDifferent(mismatched, claim.PhysicalSlotNumber, "lockFeedbackInputPoint",
                claim.LockFeedbackInputPoint, authority.LockFeedbackInputPoint);
            AddIfDifferent(mismatched, claim.PhysicalSlotNumber, "lightCurtainInputPoint",
                claim.LightCurtainInputPoint, authority.LightCurtainInputPoint);
            AddIfDifferent(mismatched, claim.PhysicalSlotNumber, "signalPolarity",
                claim.SignalPolarity, authority.SignalPolarity);
            if (claim.PulseResetMilliseconds != authority.PulseResetMilliseconds)
            {
                mismatched.Add(Field(claim.PhysicalSlotNumber, "pulseResetMilliseconds"));
            }
        }
        foreach (int missing in bySlot.Keys.Except(declared.Select(claim => claim.PhysicalSlotNumber)))
        {
            mismatched.Add(Field(missing, "slot"));
        }
        return new VehicleDeclarationVerdict(agvId, mismatched.Count == 0, [.. mismatched.Order(StringComparer.Ordinal)]);
    }

    private static void AddIfDifferent(List<string> into, int slot, string field, string claimed, string authoritative)
    {
        if (!string.Equals(claimed, authoritative, StringComparison.Ordinal))
        {
            into.Add(Field(slot, field));
        }
    }

    private static string Field(int slot, string field) =>
        FormattableString.Invariant($"slot{slot}.{field}");

    private async Task<SlotTemplateRow> RequirePublishedTemplateAsync(
        string templateKey,
        long version,
        CancellationToken cancellationToken)
    {
        SlotTemplateRow? template = await _context.Set<SlotTemplateRow>()
            .FirstOrDefaultAsync(
                row => row.TemplateKey == templateKey
                    && row.Version == version
                    && row.Status == PublishedStatus,
                cancellationToken);
        return template ?? throw new InvalidOperationException(
            FormattableString.Invariant(
                $"Slot template {templateKey} v{version} is not published; a model version cannot reference it."));
    }

    private static async Task<long> NextVersionAsync(IQueryable<long> versions, CancellationToken cancellationToken)
    {
        long[] existing = await versions.ToArrayAsync(cancellationToken);
        return existing.Length == 0 ? 1 : existing.Max() + 1;
    }

    private static string NewId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}
