using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.FieldOps;

/// <summary>
/// W1 现场窗口的运维入口：逐仓核对录入、逐台放行、门禁启用留痕。
/// </summary>
/// <remarks>
/// <para>
/// **这是受控运维流程，不是界面。**放行一台车的仓位配置就绪会让它有资格取得业务就绪，方向是
/// fail-unsafe 的——无人员认证的前提下这类动作不上看板，只走这条要人在机器前刻意敲一次的路。
/// 与恢复入口同理。
/// </para>
/// <para>
/// 每条命令向 stdout 打一个 JSON 对象，退出码 0 表示做成、1 表示被拒或未就绪、2 表示用法错误。
/// 编排脚本据此写 <c>timeline.jsonl</c> 与 <c>assertions.json</c>。
/// </para>
/// </remarks>
internal static partial class Program
{
    private static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions Input = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>取值即「出现」的开关，后面不跟值。</summary>
    private static readonly HashSet<string> ValuelessOptions = new(StringComparer.Ordinal) { "dry-run" };

    internal static async Task<int> Main(string[] args)
    {
        // The vehicles are named in Chinese in RIoT, so every identifier this tool prints goes
        // through here. Without this the console encodes UTF-8 JSON as the ANSI code page and the
        // evidence ends up holding a mangled agvId -- which reads as a real mismatch later.
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);

        if (args.Length == 0)
        {
            return Usage("no command given");
        }

        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int index = 1; index < args.Length;)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                return Usage($"malformed option near '{args[index]}'");
            }
            string name = args[index][2..];
            // A switch takes no value. Requiring one would make the operator type `--dry-run true`,
            // and `--dry-run false` would then read as a dry run that is not one.
            if (ValuelessOptions.Contains(name))
            {
                options[name] = "true";
                index += 1;
                continue;
            }
            if (index + 1 >= args.Length)
            {
                return Usage($"malformed option near '{args[index]}'");
            }
            options[name] = args[index + 1];
            index += 2;
        }

        if (!options.TryGetValue("database", out string? databasePath))
        {
            return Usage("--database is required");
        }
        if (!File.Exists(databasePath))
        {
            return Usage($"database file not found: {databasePath}");
        }

        // 一行不写的命令连库都用 SQLite 自己的只读模式开——「只读」由驱动保证，不是靠这里自觉。
        // 一个判断、一份名单：新增只读动词往这里加，不要在别处另起一套。
        bool readOnly = OpensReadOnly(args[0]);
        // 服务端主机正在写同一个文件。连接串走共用的那一处，等写锁的上限两边因此是同一个值——自己拼一串
        // 出来的话，这个进程会在对方一次正常的写事务上直接报 database is locked。
        DbContextOptions<ControlServerDbContext> contextOptions =
            new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(ControlServerSqlite.ForDatabaseFile(databasePath, readOnly))
                .Options;
        await using ControlServerDbContext context = new(contextOptions);
        GovernanceStore governance = new(
            context, GovernanceDeploymentIdentity.ForCurrentHost(), AuditRetentionPolicy.Default);
        SlotConfigurationReadinessGate gate = new(context, governance);
        DateTimeOffset now = DateTimeOffset.Now;

        return args[0] switch
        {
            "status" => await StatusAsync(context, gate),
            "verify" => await VerifyAsync(gate, options, now),
            "release" => await ReleaseAsync(gate, options, now),
            "enable-gate" => await EnableGateAsync(governance, options, now),
            "audit" => await AuditAsync(context, options),
            "seed-approved-facts" => await SeedApprovedFactsAsync(context, governance, now),
            "bind-io" => await BindIoAsync(context, governance, options, now),
            "export-audit" => await ExportAuditAsync(context, options, now),
            CheckBindingSnapshotsCommand => await CheckBindingSnapshotsAsync(context),
            ImportAreaAssignmentsCommand => await ImportAreaAssignmentsAsync(context, governance, options, now),
            ReadAreaAssignmentsCommand => await ReadAreaAssignmentsAsync(context, governance, options),
            ActivateTaskTypeStationsCommand => await ActivateTaskTypeStationsAsync(context, governance, options, now),
            RollbackTaskTypeStationsCommand => await RollbackTaskTypeStationsAsync(context, governance, options, now),
            ReconcileTaskTypeStationsCommand => await ReconcileTaskTypeStationsAsync(context, governance, options, now),
            ReleaseTaskTypeStationHoldCommand => await ReleaseTaskTypeStationHoldAsync(context, governance, options, now),
            ReadTaskTypeStationsCommand => await ReadTaskTypeStationsAsync(context, governance, options),
            CloseTaskTypeStationActivationCommand => await CloseTaskTypeStationActivationAsync(context, governance, options, now),
            ImportDispatchZoneParametersCommand => await ImportDispatchZoneParametersAsync(context, governance, options, now),
            ReadDispatchZoneParametersCommand => await ReadDispatchZoneParametersAsync(context, governance, options),
            _ => Usage($"unknown command '{args[0]}'")
        };
    }

    /// <summary>
    /// 以 SQLite 只读模式开库的命令。一个判断、一份名单：新增只读动词往这里加，不要在别处另起一套。
    /// </summary>
    internal static bool OpensReadOnly(string command) =>
        command is CheckBindingSnapshotsCommand or ReadAreaAssignmentsCommand or ReadTaskTypeStationsCommand
            or ReadDispatchZoneParametersCommand;

    private const string CheckBindingSnapshotsCommand = "check-binding-snapshots";

    private const string ImportAreaAssignmentsCommand = "import-area-assignments";

    private const string ReadAreaAssignmentsCommand = "area-assignments";

    /// <summary>
    /// 体检：哪些 IO 绑定行指着的快照装的不是它们自己。**只读，一行不写。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 找的是取号撞车留下的痕迹：绑定发布与激活、回滚曾经会取到同一个版本号，后冻结的一方于是拿回先
    /// 冻结那一方的快照，绑定行与它的发布审计就指向了一份别人的内容。取号修好之后新写入不再产生这种
    /// 行，已经写下的不会自己变好。
    /// </para>
    /// <para>
    /// 退出码沿用本工具的约定：0 是干净，1 是查出了东西（不是工具出错），2 是用法错误。它不修任何东西
    /// ——一版快照不可改写，重新发布一版并作废旧的那一版是有人负责的运维决定。
    /// </para>
    /// </remarks>
    private static async Task<int> CheckBindingSnapshotsAsync(ControlServerDbContext context)
    {
        IReadOnlyList<SlotBindingSnapshotFinding> findings =
            await SlotConfigurationBindingSnapshotAudit.ScanAsync(context, CancellationToken.None);
        return Emit(
            new
            {
                command = CheckBindingSnapshotsCommand,
                outcome = findings.Count == 0 ? "OK" : "FINDINGS",
                count = findings.Count,
                findings = findings.Select(finding => new
                {
                    agvId = finding.AgvId,
                    slotModelVersionId = finding.SlotModelVersionId,
                    objectId = finding.ObjectId,
                    version = finding.Version,
                    snapshotId = finding.SnapshotId,
                    problem = finding.Problem,
                    slotsOnlyInSnapshot = finding.SlotsOnlyInSnapshot,
                    slotsOnlyInRows = finding.SlotsOnlyInRows,
                    differingFields = finding.DifferingFields
                })
            },
            findings.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// 把 REQ-0267 的已批准八仓硬件事实入库。幂等：已经入过就返回既有那一版。
    /// </summary>
    /// <remarks>
    /// DO1–DO8 开锁、DI1–DI8 锁反馈、DI9–DI16 仓内光幕、500 ms 脉冲复位、ACTIVE_HIGH。它们是**已批准
    /// 的版本化不可改写内容**，走与别的版本同一条发布路径，因此同样产快照与审计，发布之后同样改不动。
    /// </remarks>
    private static async Task<int> SeedApprovedFactsAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        DateTimeOffset now)
    {
        SlotConfigurationAuthorityStore authority = new(
            context, new ControlServer.Application.GovernedConfigurationPublisher(governance, governance));
        SlotModelVersionRow model = await authority.EnsureApprovedHardwareFactsAsync(now, CancellationToken.None);
        return Emit(
            new
            {
                command = "seed-approved-facts",
                outcome = "OK",
                modelKey = model.ModelKey,
                slotModelVersionId = model.SlotModelVersionId,
                version = model.Version,
                slotCount = model.SlotCount
            },
            0);
    }

    /// <summary>
    /// 给一台车录入 IO 绑定。这是硬件相关变更，只能从这条权威路径进来。
    /// </summary>
    /// <remarks>
    /// 录入用的是已批准的八仓事实，不是现场手抄的一份。车上报的声明只被核验、永远不被采信为权威
    /// （REQ-0258），所以这里没有「从车上读回来填进去」这条路。
    /// </remarks>
    private static async Task<int> BindIoAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!options.TryGetValue("agv", out string? agvId))
        {
            return Usage("bind-io needs --agv <agvId>");
        }
        SlotModelVersionRow? model = await context.Set<SlotModelVersionRow>()
            .FirstOrDefaultAsync(row => row.ModelKey == ApprovedSlotHardwareFacts.ModelKey && row.Version == 1);
        if (model is null)
        {
            return Usage("run seed-approved-facts first: the approved eight-slot model is not in this database");
        }

        SlotConfigurationAuthorityStore authority = new(
            context, new ControlServer.Application.GovernedConfigurationPublisher(governance, governance));
        IReadOnlyList<SlotIoBindingRow> bindings = await authority.PublishIoBindingsAsync(
            agvId, model.SlotModelVersionId, ApprovedSlotHardwareFacts.IoBindings, now, CancellationToken.None);
        return Emit(
            new
            {
                command = "bind-io",
                outcome = "OK",
                agvId,
                slotModelVersionId = model.SlotModelVersionId,
                boundSlots = bindings.Count,
                version = bindings.Count > 0 ? bindings[0].Version : 0
            },
            0);
    }

    /// <summary>每台车此刻的仓位配置就绪判定与缺口。只读，不改任何东西。</summary>
    private static async Task<int> StatusAsync(ControlServerDbContext context, SlotConfigurationReadinessGate gate)
    {
        var pairs = await context.Set<SlotIoBindingRow>().AsNoTracking()
            .Select(row => new { row.AgvId, row.SlotModelVersionId })
            .Distinct()
            .ToArrayAsync();
        List<object> vehicles = [];
        foreach (var pair in pairs.OrderBy(pair => pair.AgvId, StringComparer.Ordinal))
        {
            SlotConfigurationReadinessVerdict verdict = await gate.EvaluateAsync(
                pair.AgvId, pair.SlotModelVersionId, CancellationToken.None);
            vehicles.Add(new
            {
                agvId = verdict.AgvId,
                slotModelVersionId = verdict.SlotModelVersionId,
                ready = verdict.Ready,
                reasonCode = verdict.ReasonCode,
                slotsMissingBinding = verdict.SlotsMissingBinding,
                slotsMissingVerification = verdict.SlotsMissingVerification,
                slotsVerifiedNegative = verdict.SlotsVerifiedNegative
            });
        }
        return Emit(new { command = "status", outcome = "OK", vehicles }, 0);
    }

    /// <summary>
    /// 录入一台车的逐仓核对。抽样会被门禁整批拒绝——那正是要的：得到一次拒绝，不是一份待办清单。
    /// </summary>
    private static async Task<int> VerifyAsync(
        SlotConfigurationReadinessGate gate,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!options.TryGetValue("record", out string? recordPath))
        {
            return Usage("verify needs --record <field-record.json>");
        }
        VehicleFieldRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<VehicleFieldRecord>(
                await File.ReadAllTextAsync(recordPath), Input);
        }
        catch (JsonException malformed)
        {
            // A hand-filled record with a mistyped timestamp is the normal case in a plant, not an
            // exceptional one. It gets one line saying which field, not a stack trace.
            return Usage($"field record {recordPath} is malformed: {malformed.Message}");
        }
        if (record is null || record.Slots.Count == 0)
        {
            return Usage($"field record is empty or unreadable: {recordPath}");
        }

        SlotVerificationConfirmation[] confirmations =
        [
            .. record.Slots.Select(slot => new SlotVerificationConfirmation(
                slot.PhysicalSlotNumber,
                slot.OpenSignalConfirmed,
                slot.CloseSignalConfirmed,
                slot.InPlaceSignalConfirmed,
                slot.FieldRecordReference))
        ];
        try
        {
            await gate.RecordVerificationAsync(
                record.AgvId,
                record.SlotModelVersionId,
                confirmations,
                record.VerifiedAt ?? now,
                CancellationToken.None);
        }
        catch (SampledVerificationRejectedException rejected)
        {
            return Emit(
                new
                {
                    command = "verify",
                    outcome = "REJECTED",
                    agvId = record.AgvId,
                    reason = rejected.Message
                },
                1);
        }

        SlotConfigurationReadinessVerdict verdict = await gate.EvaluateAsync(
            record.AgvId, record.SlotModelVersionId, CancellationToken.None);
        return Emit(
            new
            {
                command = "verify",
                outcome = "OK",
                agvId = record.AgvId,
                slotModelVersionId = record.SlotModelVersionId,
                verifiedBy = record.VerifiedBy,
                slotsConfirmed = confirmations.Length,
                photoPointers = record.PhotoPointers,
                ready = verdict.Ready,
                reasonCode = verdict.ReasonCode
            },
            0);
    }

    /// <summary>
    /// 放行一台车：把现算的判定写进读模型。**不就绪就放行不了**——这个命令不能把一台不合格的车
    /// 变成合格的，它只能把已经成立的事实记下来。
    /// </summary>
    private static async Task<int> ReleaseAsync(
        SlotConfigurationReadinessGate gate,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!options.TryGetValue("agv", out string? agvId) ||
            !options.TryGetValue("model", out string? slotModelVersionId))
        {
            return Usage("release needs --agv <agvId> --model <slotModelVersionId>");
        }

        SlotConfigurationReadinessVerdict verdict = await gate.RefreshReadinessAsync(
            agvId, slotModelVersionId, now, CancellationToken.None);
        return Emit(
            new
            {
                command = "release",
                outcome = verdict.Ready ? "OK" : "NOT_READY",
                agvId,
                slotModelVersionId,
                ready = verdict.Ready,
                reasonCode = verdict.ReasonCode,
                slotsMissingBinding = verdict.SlotsMissingBinding,
                slotsMissingVerification = verdict.SlotsMissingVerification,
                slotsVerifiedNegative = verdict.SlotsVerifiedNegative,
                releasedAt = now
            },
            verdict.Ready ? 0 : 1);
    }

    /// <summary>
    /// 记录门禁启用这一刻。
    /// </summary>
    /// <remarks>
    /// **它记的是时刻，不是它自己在拦车。**当前 <c>src/</c> 里没有任何生产调用点读
    /// <see cref="SlotConfigurationReadinessGate.IsSlotConfigurationConfirmedAsync"/>——把 readiness
    /// 接进投运判定属于投运流程，不属于这张现场票。所以这条命令做的是：写一条不可改写审计，让
    /// 「门禁上线晚于第一台车核对通过」这件事在证据里可复核，并提醒运维去改
    /// <c>Governance:slotConfigurationReadinessGate</c> 并重启服务。
    /// </remarks>
    private static async Task<int> EnableGateAsync(
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        options.TryGetValue("note", out string? note);
        string auditRecordId = await governance.WriteBusinessAsync(
            new ControlServer.Application.GovernanceAuditEntry(
                "SLOT_CONFIGURATION_READINESS_GATE_ENABLED",
                GovernedObjectKind.ActiveSlotConfiguration,
                "fleet",
                null,
                GovernanceActionOutcome.Succeeded,
                JsonSerializer.Serialize(
                    new
                    {
                        mode = SlotConfigurationGateMode.Enforcing,
                        configurationKey = "Governance:slotConfigurationReadinessGate",
                        note = note ?? string.Empty
                    },
                    Output)),
            now,
            CancellationToken.None);
        return Emit(
            new
            {
                command = "enable-gate",
                outcome = "OK",
                auditRecordId,
                enabledAt = now,
                configurationKey = "Governance:slotConfigurationReadinessGate",
                requiredValue = nameof(SlotConfigurationGateMode.Enforcing)
            },
            0);
    }

    /// <summary>
    /// 导出本窗口相关的不可改写业务审计，供证据目录留档。
    /// </summary>
    /// <remarks>
    /// 按 UTC ticks 过滤而不是按 <c>DateTimeOffset</c>：EF 对 SQLite 翻译不了后者的比较，而把 180 天
    /// 审计全读进内存再筛，只为导出一个窗口的几十条，是拿一次全表扫换一次方便。
    /// </remarks>
    private static async Task<int> AuditAsync(ControlServerDbContext context, Dictionary<string, string> options)
    {
        long sinceTicks = options.TryGetValue("since", out string? since)
            && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            ? parsed.UtcTicks
            : 0L;

        var records = await context.Set<BusinessAuditRecordRow>().AsNoTracking()
            .Where(row => row.RecordedAtUtcTicks >= sinceTicks
                && (row.Action.StartsWith("SLOT_CONFIGURATION") || row.Action.StartsWith("WHOLE_VEHICLE")))
            .Select(row => new
            {
                row.AuditRecordId,
                row.RecordedAtUtcTicks,
                row.ActorIdentity,
                row.ActorAttribution,
                row.Action,
                row.ObjectId,
                row.Outcome,
                row.SnapshotId,
                row.DetailJson
            })
            .ToArrayAsync();
        return Emit(
            new
            {
                command = "audit",
                outcome = "OK",
                count = records.Length,
                records = records.OrderBy(record => record.RecordedAtUtcTicks)
            },
            0);
    }

    /// <summary>
    /// REQ-0271：把一条审计流在期限内的记录导出成文件。CSV 给人和 Excel，JSON 给下一次审计工具机械读回。
    /// </summary>
    /// <remarks>
    /// 与上面的 <c>audit</c> 不是一回事：那一条只挑本窗口相关的业务审计打到 stdout 供证据留档，这一条导出
    /// 一整条流（业务或管理员），不按动作名过滤。输出文件已存在就拒绝——导出常常就是证据，覆盖一份旧的等于
    /// 悄悄改掉它。
    /// </remarks>
    private static async Task<int> ExportAuditAsync(
        ControlServerDbContext context,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!options.TryGetValue("stream", out string? streamText) ||
            !options.TryGetValue("format", out string? formatText) ||
            !options.TryGetValue("output", out string? outputPath))
        {
            return Usage("export-audit needs --stream <business|administrator> --format <csv|json> --output <file>");
        }
        AuditTrail? stream = streamText switch
        {
            "business" => AuditTrail.Business,
            "administrator" => AuditTrail.Administrator,
            _ => null
        };
        AuditExportFormat? format = formatText switch
        {
            "csv" => AuditExportFormat.Csv,
            "json" => AuditExportFormat.Json,
            _ => null
        };
        if (stream is null)
        {
            return Usage($"--stream must be business or administrator, not '{streamText}'");
        }
        if (format is null)
        {
            return Usage($"--format must be csv or json, not '{formatText}'");
        }
        if (File.Exists(outputPath))
        {
            return Usage($"output file already exists: {outputPath}");
        }
        if (!TryReadInstant(options, "since", out DateTimeOffset? from) ||
            !TryReadInstant(options, "until", out DateTimeOffset? until))
        {
            return Usage("--since and --until must be ISO-8601 instants");
        }

        IReadOnlyList<AuditExportRecord> records;
        try
        {
            records = await AuditExport.ReadAsync(context, stream.Value, from, until, CancellationToken.None);
        }
        catch (ArgumentException inverted)
        {
            return Usage(inverted.Message);
        }
        byte[] content = AuditExport.Render(format.Value, stream.Value, records, from, until, now);
        await File.WriteAllBytesAsync(outputPath, content);
        return Emit(
            new
            {
                command = "export-audit",
                outcome = "OK",
                stream = AuditExport.StreamName(stream.Value),
                format = formatText,
                output = Path.GetFullPath(outputPath),
                from,
                until,
                count = records.Count,
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant()
            },
            0);
    }

    /// <summary>
    /// REQ-0350：整表原子导入分区归属表（含开门侧列）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>表自身有错就整份拒绝，一行都不写</b>，并且一次把全部错误行报出来。现场改一轮表要跑一趟，逐条挤
    /// 牙膏等于逐条跑一趟。<c>--dry-run</c> 只校验并出预览，正式导入则写一个新的不可变版本，附快照与不可改写
    /// 审计；内容与上一版完全相同也形成新版本——回滚就是把旧内容再导入一次。生效不要求重启服务或车辆空闲。
    /// </para>
    /// <para>
    /// 预览列出开门侧会随这份新内容变化的在途需求。它只提示：已冻结的需求装货与卸货仍按冻结版本执行。
    /// </para>
    /// </remarks>
    private static async Task<int> ImportAreaAssignmentsAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!options.TryGetValue("input", out string? inputPath))
        {
            return Usage("import-area-assignments needs --input <assignments.csv>");
        }
        if (!File.Exists(inputPath))
        {
            return Usage($"input file not found: {inputPath}");
        }
        bool dryRun = options.ContainsKey("dry-run");

        ControlServer.Application.AreaAssignmentImportService importer = new(
            new AreaAssignmentStore(
                context, new ControlServer.Application.GovernedConfigurationPublisher(governance, governance)),
            new DemandAreaAssignmentFreezeStore(context),
            new AreaAssignmentImportFacts(context));
        ControlServer.Application.AreaAssignmentImportResult result = await importer.ImportAsync(
            await File.ReadAllTextAsync(inputPath), dryRun, now, CancellationToken.None);

        bool accepted = result.Outcome == ControlServer.Application.AreaAssignmentImportOutcome.Accepted;
        return Emit(
            new
            {
                command = "import-area-assignments",
                outcome = accepted ? "OK" : "REJECTED",
                dryRun,
                input = Path.GetFullPath(inputPath),
                entryCount = result.EntryCount,
                version = result.Version?.Version,
                contentSha256 = result.Version?.ContentSha256,
                snapshotId = result.Version?.SnapshotId,
                importedAt = result.Version?.ImportedAt,
                errorCount = result.Errors.Count,
                errors = result.Errors.Select(error => new
                {
                    line = error.Line,
                    reasonCode = error.ReasonCode,
                    area = error.Area,
                    detail = error.Detail
                }),
                preview = result.Preview.Select(entry => new
                {
                    demandId = entry.DemandId,
                    area = entry.Area,
                    frozenVersion = entry.FrozenVersion,
                    frozenSlotPosition = entry.FrozenSlotPosition,
                    newSlotPosition = entry.NewSlotPosition
                })
            },
            accepted ? 0 : 1);
    }

    /// <summary>
    /// 当前（或指定）那一版分区归属表的全部行与版本元数据。
    /// </summary>
    /// <remarks>
    /// <b>只读，而且库是以 SQLite 的只读模式开的</b>（见 <see cref="Main"/> 里那个判断），与体检命令同一条
    /// 纪律：不写这件事由驱动保证，不靠这里自觉。
    /// </remarks>
    private static async Task<int> ReadAreaAssignmentsAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options)
    {
        long? version = null;
        if (options.TryGetValue("version", out string? versionText))
        {
            if (!long.TryParse(versionText, CultureInfo.InvariantCulture, out long parsed))
            {
                return Usage($"--version must be a whole number, not '{versionText}'");
            }
            version = parsed;
        }

        // 读路径不发布，所以这里给的 publisher 只是为了构造 store，它不会被走到。
        AreaAssignmentStore store = new(
            context, new ControlServer.Application.GovernedConfigurationPublisher(governance, governance));
        ControlServer.Application.AreaAssignmentTableVersion? table = version is null
            ? await store.ReadCurrentAsync(CancellationToken.None)
            : await store.ReadVersionAsync(version.Value, CancellationToken.None);
        if (table is null)
        {
            return Emit(
                new
                {
                    command = "area-assignments",
                    outcome = "NOT_FOUND",
                    requestedVersion = version,
                    detail = version is null
                        ? "No area assignment table has been imported into this database."
                        : "That version does not exist."
                },
                1);
        }

        return Emit(
            new
            {
                command = "area-assignments",
                outcome = "OK",
                version = table.Version,
                contentSha256 = table.ContentSha256,
                snapshotId = table.SnapshotId,
                importedAt = table.ImportedAt,
                entryCount = table.ByArea.Count,
                entries = table.ByArea.Values
                    .OrderBy(assignment => assignment.Area, StringComparer.Ordinal)
                    .Select(assignment => new
                    {
                        area = assignment.Area,
                        dispatchZone = assignment.DispatchZone,
                        slotPosition = assignment.SlotPosition
                    })
            },
            0);
    }

    private static bool TryReadInstant(Dictionary<string, string> options, string name, out DateTimeOffset? value)
    {
        value = null;
        if (!options.TryGetValue(name, out string? text))
        {
            return true;
        }
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static int Emit(object payload, int exitCode)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, Output));
        return exitCode;
    }

    private static int Usage(string problem)
    {
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ControlServer.FieldOps: {problem}."));
        Console.Error.WriteLine(
            "usage: ControlServer.FieldOps <status|verify|release|enable-gate|audit|seed-approved-facts|bind-io"
            + "|export-audit|check-binding-snapshots|import-area-assignments|area-assignments"
            + "|activate-task-type-stations|rollback-task-type-stations|reconcile-task-type-stations"
            + "|release-task-type-station-hold|close-task-type-station-activation|task-type-stations"
            + "|import-dispatch-zone-parameters|dispatch-zone-parameters>"
            + " --database <path> [options]");
        Console.Error.WriteLine("  verify      --record <field-record.json>");
        Console.Error.WriteLine("  release     --agv <agvId> --model <slotModelVersionId>");
        Console.Error.WriteLine("  enable-gate [--note <text>]");
        Console.Error.WriteLine("  audit       [--since <iso-8601>]");
        Console.Error.WriteLine("  seed-approved-facts");
        Console.Error.WriteLine("  bind-io     --agv <agvId>");
        Console.Error.WriteLine(
            "  export-audit --stream <business|administrator> --format <csv|json> --output <file>"
            + " [--since <iso-8601>] [--until <iso-8601>]");
        Console.Error.WriteLine(
            "  check-binding-snapshots   read-only; exit 1 means findings, not a tool failure");
        Console.Error.WriteLine("  import-area-assignments --input <assignments.csv> [--dry-run]");
        Console.Error.WriteLine("  area-assignments        [--version <n>]   read-only");
        Console.Error.WriteLine(
            "  activate-task-type-stations --input <candidate.json> --catalog <stations.json> --reason <text>"
            + " [--role <text>] [--dry-run]");
        Console.Error.WriteLine(
            "  rollback-task-type-stations --map <id> --version <n> --catalog <stations.json> --reason <text>"
            + " [--role <text>] [--dry-run]");
        Console.Error.WriteLine("  reconcile-task-type-stations --map <id> --reason <text> [--role <text>]");
        Console.Error.WriteLine(
            "  release-task-type-station-hold --map <id> --task-type <TASK_TYPE> --site-verification <ref>"
            + " --catalog <stations.json> --reason <text> [--role <text>]");
        Console.Error.WriteLine("  close-task-type-station-activation --map <id> --reason <text> [--role <text>]");
        Console.Error.WriteLine("  task-type-stations      --map <id>   read-only");
        Console.Error.WriteLine("  import-dispatch-zone-parameters --input <zone-parameters.csv> [--dry-run]");
        Console.Error.WriteLine("  dispatch-zone-parameters        [--version <n>]   read-only");
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "import-area-assignments takes a UTF-8 CSV whose header is exactly"
            + " 'area,dispatch_zone,slot_position' and whose fields carry no outer whitespace. The whole table"
            + " is imported atomically: if any row is bad the whole file is rejected, every bad row is"
            + " reported, and nothing is written.");
        Console.Error.WriteLine(
            "  A blank line at the end of the file is ignored; a blank line between rows is a malformed row"
            + " and rejects the file.");
        Console.Error.WriteLine(
            "  Slot position groups come from the published whole-vehicle slot models in this database, never"
            + " from a value hard-coded here; run seed-approved-facts first on a fresh database.");
        Console.Error.WriteLine(
            "  Dispatch zones come from the dispatch policy stored in this database, which the server writes at"
            + " startup from its configuration. Two consequences:");
        Console.Error.WriteLine(
            "    - the server must have been started once with the intended configuration before importing;");
        Console.Error.WriteLine(
            "    - a zone that is configured but has no vehicle serving it counts as not existing.");
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "The task type station verbs change which whole binding set version a map uses, and release holds."
            + " candidate.json is {mapId, ruleVersion, requiredTaskTypes[], bindings[{taskType, stationRiotId,"
            + " stationName, siteVerificationRef}]}; stations.json is {mapId, stations[{stationId, stationName}]},"
            + " the map's complete RIoT station list. It must be exactly the list the server last confirmed, and that"
            + " confirmation must still be fresh; otherwise nothing is activated or released.");
        Console.Error.WriteLine(
            "  Activation commits in two steps. If the second one cannot be confirmed the map stays held and the"
            + " outcome is RESULT_UNKNOWN; run reconcile-task-type-stations, which reads what is really in force."
            + " Only when it reads back a contradiction does close-task-type-station-activation give up the attempt:"
            + " the map is left with no active version until the next activation or rollback.");
        Console.Error.WriteLine(
            "import-dispatch-zone-parameters takes a UTF-8 CSV whose header is exactly"
            + " 'dispatch_zone,en_route_addition_max_path_cost_increase_mm,starvation_threshold_seconds'. The increase is in"
            + " planned path cost (millimetres, 0 to 1000000), not time; the threshold is in seconds (1 to 86400). An empty"
            + " value, or a zone not in the table, is unconfigured; an increase of 0 forbids en-route addition in that zone."
            + " The same whole-table rules as import-area-assignments apply, except that a table whose values equal the"
            + " current version writes no new version (UNCHANGED).");
        Console.Error.WriteLine(
            "  --role is recorded as given and is not verified: there is no personnel authentication, and every audit"
            + " record names this deployment, not a person.");
        return 2;
    }
}

/// <summary>现场逐仓核对记录的一行。</summary>
internal sealed record SlotFieldRecord(
    int PhysicalSlotNumber,
    bool OpenSignalConfirmed,
    bool CloseSignalConfirmed,
    bool InPlaceSignalConfirmed,
    string? FieldRecordReference,
    string? Note);

/// <summary>一台车的现场核对记录。照片指针留在这里，照片本身不进 git。</summary>
internal sealed record VehicleFieldRecord(
    string AgvId,
    string SiteAlias,
    string SlotModelVersionId,
    string VerifiedBy,
    DateTimeOffset? VerifiedAt,
    IReadOnlyList<string> PhotoPointers,
    IReadOnlyList<SlotFieldRecord> Slots);
