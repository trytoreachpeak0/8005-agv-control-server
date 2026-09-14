using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ControlServer.EmergencyDrill;

/// <summary>
/// SUMMARY.md, in Chinese, from this run's evidence only. Written once: the field-record section is
/// filled in by hand afterwards, and a second summarize would silently overwrite that.
/// </summary>
internal static class DrillSummary
{
    private const string ExceptionApproval =
        "2026-09-14，产品负责人（用户）在对话中批准：批次 2 票 19 不走 FAILED 产品路径，改做缩减版演练，并对 " +
        "`vendor/8005-agv-program/docs/riot-call-allowlist.md` 第 1.5 节做**一次性破例，仅限本次演练、仅限 agv02**" +
        "（`" + DrillGuards.ApprovedDeviceKey + "`）：\n\n" +
        "1. 8005 工具经白名单内的 `POST /api/order/v1/add/byDefaultMissions` 为 agv02 建一张移动单，让空载车在 map 25 的两个站点之间行驶；\n" +
        "2. 车在两站之间行驶时，工具向 agv02 发送**且只发送一次** `triggerEmergency`；\n" +
        "3. 工具观察 RIoT 确实让车停下，并读取急停闩锁 `emergencyState`（`CAN_RECOVER`／`CAN_NOT_RECOVER`）；\n" +
        "4. 车在急停锁着、停稳的状态下，以白名单内的订单命令 `CMD_ORDER_CANCEL` 取消演练单——只作清理，不代替停车；" +
        "`CAN_NOT_RECOVER` 时同样取消；\n" +
        "5. 演练单进入终态，且现场人员确认车辆已停稳、车上无货、仓门已关（代替白名单要求的车载端确认）后，**仅当**闩锁为 " +
        "`CAN_RECOVER` 时调用一次 `cancelEmergency`，并回查 `emergencyState=OK`；`CAN_NOT_RECOVER` 时禁止调用解除，转 RIoT 人工处理。\n\n" +
        "**收尾顺序是先取消演练单、再解除急停。**最初批准的写法是先解除、后取消单；同日用户裁定改为现在的顺序，理由：" +
        "解除后 RIoT 可能让车接着开往终点，而现场人员此时正在车旁。工具以守卫强制这个顺序。\n\n" +
        "会让车移动、发急停、取消订单或解除急停的每一条命令，运行前都在对话中逐次单独授权。白名单文档本身不改。";

    private static readonly string[] L2EvidenceDirectories =
    [
        "evidence/l2/20260914-ci-34815736635-emergency-stop-single-trigger-01",
        "evidence/l2/20260914-ci-34815736635-emergency-stop-single-trigger-02",
        "evidence/l2/20260914-ci-34815736635-emergency-stop-single-trigger-03"
    ];

    internal static CommandOutcome Write(DrillContext context)
    {
        CommandOutcome outcome = new("summarize");
        DrillEvidence evidence = context.Evidence;
        DrillState state = context.State;
        if (File.Exists(Path.Combine(evidence.Directory, DrillEvidence.SummaryFileName)))
        {
            return outcome.Refuse("SUMMARY.md already exists in this run; evidence is append-only -- fill in its field-record section by hand");
        }

        JsonElement[] commands = [.. evidence.ReadLines(DrillEvidence.CommandsFileName)];
        JsonElement[] wire = [.. evidence.ReadLines(DrillEvidence.WireFileName)];
        CallCount trigger = Count(commands, wire, "triggerEmergency", path => path.EndsWith("/triggerEmergency", StringComparison.Ordinal));
        CallCount cancelEmergency = Count(commands, wire, "cancelEmergency", path => path.EndsWith("/cancelEmergency", StringComparison.Ordinal));
        CallCount create = Count(commands, wire, "byDefaultMissions", path => path.StartsWith("/api/order/v1/add/byDefaultMissions", StringComparison.Ordinal));
        CallCount cancelOrder = Count(commands, wire, "CMD_ORDER_CANCEL", path => path.StartsWith("/api/task/v1/order/command/", StringComparison.Ordinal));

        TriggerRecord? triggerRecord = state.Trigger;
        bool stopProven = triggerRecord is { Latched: true, Stopped: true };
        bool sentOnce = trigger.Intents == 1 && trigger.Posts == 1;
        List<string> abnormal = Abnormalities(state, commands, wire, trigger, cancelEmergency, create, cancelOrder);

        StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"# W1 空载急停演练（缩减版）证据摘要 `{state.RunId}`");
        text.AppendLine();
        if (state.FakeRiot)
        {
            text.AppendLine("> **自测运行**（`--fake-riot`，对本机回环上的 `ControlServer.FakeRiot`）。**这不是现场证据**，不得作为票 19 的出口证据引用。");
            text.AppendLine();
        }
        text.AppendLine("| 项 | 值 |");
        text.AppendLine("| --- | --- |");
        Row(text, "票据", "批次 2 票 19（W1 空载急停演练），缩减版");
        Row(text, "runId", Code(state.RunId));
        Row(text, "车辆", $"{state.VehicleAlias} {Code(state.DeviceKey)}");
        Row(text, "地图", $"map {state.MapId.ToString(CultureInfo.InvariantCulture)}（{Code(state.MapIdentity)}）");
        Row(text, "RIoT 地址", Code(state.RiotBaseUrl));
        Row(text, "初始化时刻／主机", $"{Time(state.CreatedAt)}／{Code(state.CreatedOnHost)}");
        Row(text, "演练单", state.Order is null
            ? "未建"
            : $"{Code(state.Order.UpperId)} → orderId {Code(state.Order.OrderId ?? "-")}，站 {state.Order.StartStationId.ToString(CultureInfo.InvariantCulture)} → {state.Order.DestinationStationId.ToString(CultureInfo.InvariantCulture)}");
        Row(text, "收尾顺序", "发令 → 取消演练单 → 解除急停（解除前演练单必须已是终态）");
        Row(text, "摘要生成时刻", Time(DateTimeOffset.Now));
        text.AppendLine();

        text.AppendLine("## 一、判定");
        text.AppendLine();
        text.AppendLine("| 判据 | 结论 | 依据 |");
        text.AppendLine("| --- | --- | --- |");
        Row(text, "RIoT 侧确实停车",
            stopProven ? "**PASS**（RIoT 侧证据；现场目视结论见第三节）" : "**未证实**",
            StopBasis(triggerRecord));
        Row(text, "8005 只发一次",
            sentOnce ? "**PASS**" : "**FAIL**",
            $"本 run 在发令前写下的 `triggerEmergency` 记录（`commands.jsonl` 中 `phase=SENDING`）{trigger.Intents.ToString(CultureInfo.InvariantCulture)} 条；" +
            $"`wire.jsonl` 中实际发出的 `POST …/triggerEmergency` {trigger.Posts.ToString(CultureInfo.InvariantCulture)} 次。" +
            "产品路径（`EmergencyStopSupervisor`）的「只发一次」不由本演练证明，由 L2 场景 `emergency-stop-single-trigger` 在服务端 CI 三连跑证明：" +
            string.Join("、", L2EvidenceDirectories.Select(Code)));
        Row(text, "演练单已清理（先于解除）", CancelVerdict(state), CancelBasis(state));
        Row(text, "急停解除生效", ReleaseVerdict(state), ReleaseBasis(state));
        text.AppendLine();
        text.AppendLine("票 19 要求停车事实由**现场观察与 RIoT 侧证据双向确认**：上表只是 RIoT 侧证据，现场目视结论填在第三节。");
        text.AppendLine("停稳判据：连续至少 3 个采样、跨度至少 1 秒，每个采样 `speed=0`、`movementState` 已上报且不是 `MT_RUNNING`、`currentMap` 与 " +
            "`currentStationId` 不变；读数失败会打断连续。车辆卡片不带坐标，「位置不变」只能按站点号判定。同时记下产品 `ReadMotion` 是否也会把这段读成 " +
            "`NotMoving`（它只认 `MT_FINISHED`／`MT_PAUSED`）。");
        text.AppendLine();

        text.AppendLine("## 二、白名单破例（2026-09-14 用户批准）");
        text.AppendLine();
        text.AppendLine(ExceptionApproval);
        text.AppendLine();

        text.AppendLine("## 三、现场记录（现场填写）");
        text.AppendLine();
        text.AppendLine("| 项 | 填写 |");
        text.AppendLine("| --- | --- |");
        Row(text, "日期", string.Empty);
        Row(text, "现场安全员（姓名）", string.Empty);
        Row(text, "工具操作员（姓名）", string.Empty);
        Row(text, "RIoT 配合人（姓名）", string.Empty);
        Row(text, "其他参与人", string.Empty);
        Row(text, "运行主机", Code(state.Preflight?.Host ?? state.CreatedOnHost));
        text.AppendLine();
        text.AppendLine("| 时刻 | 事件 | 记录人 |");
        text.AppendLine("| --- | --- | --- |");
        foreach (string happening in new[]
                 {
                     "空载确认：车上无货、仓内无产品",
                     "发令前目视：车在两站之间行驶",
                     "目视确认停车（停车位置）",
                     "取消演练单后车辆状态（应仍停着）",
                     "确认车辆停稳、无货、仓门全部关闭（`release --field-confirmed` 的依据）",
                     "解除后车辆状态（应不再移动：演练单已是终态）"
                 })
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"|  | {happening} |  |");
        }
        text.AppendLine();
        text.AppendLine("现场目视结论（发令后车是否停下、停在哪里、与 RIoT 侧证据是否一致；取消单与解除后车是否保持不动）：");
        text.AppendLine();
        text.AppendLine("照片指针（照片本身不进 git，这里只留指针）：");
        text.AppendLine();
        text.AppendLine("- 空载车厢／仓位：");
        text.AppendLine("- 发令前车辆位置：");
        text.AppendLine("- 停车后车辆位置：");
        text.AppendLine("- RIoT 界面急停状态截图：");
        text.AppendLine("- 取消演练单后 RIoT 订单状态截图：");
        text.AppendLine("- 解除后 RIoT 界面截图：");
        text.AppendLine();

        text.AppendLine("## 四、命令记录");
        text.AppendLine();
        text.AppendLine("| 时刻 | 命令 | 结果 | 说明 |");
        text.AppendLine("| --- | --- | --- | --- |");
        foreach (JsonElement entry in commands)
        {
            string at = Text(entry, "at") ?? "-";
            string command = Text(entry, "command") ?? "-";
            if (Text(entry, "phase") == "SENDING")
            {
                Row(text, at, Code(command), "**发出**", $"即将发出 {Code(Text(entry, "operation") ?? "-")} → {Code(Text(entry, "target") ?? "-")}");
            }
            else
            {
                Row(text, at, Code(command), Text(entry, "outcome") ?? "-", Text(entry, "message") ?? string.Empty);
            }
        }
        text.AppendLine();

        text.AppendLine("## 五、RIoT 写调用计数（本 run）");
        text.AppendLine();
        text.AppendLine("| 调用 | 发令前记录（SENDING） | 线上 POST（wire.jsonl） |");
        text.AppendLine("| --- | --- | --- |");
        CountRow(text, "`byDefaultMissions`（建单）", create);
        CountRow(text, "`triggerEmergency`", trigger);
        CountRow(text, "`CMD_ORDER_CANCEL`（订单命令端点）", cancelOrder);
        CountRow(text, "`cancelEmergency`", cancelEmergency);
        text.AppendLine();

        text.AppendLine("## 六、网络预检（最近一次）");
        text.AppendLine();
        PreflightRecord? preflight = state.Preflight;
        if (preflight is null)
        {
            text.AppendLine("本 run 没有做过预检。");
        }
        else
        {
            text.AppendLine("| 项 | 值 |");
            text.AppendLine("| --- | --- |");
            Row(text, "时刻／主机", $"{Time(preflight.At)}／{Code(preflight.Host)}");
            Row(text, "结论", preflight.Passed ? "通过" : "**未通过**");
            Row(text, "路由", Code(preflight.RouteVerdict) + (preflight.AcceptedRouteReason is null ? string.Empty : $"（以 `--accept-tun` 放行：{preflight.AcceptedRouteReason}）"));
            Row(text, "车辆卡片读 5 次", $"中位 {Motion.Number(preflight.LatencyMedianMs)} ms，最大 {Motion.Number(preflight.LatencyMaxMs)} ms");
            Row(text, "未通过项", preflight.Failures.Count == 0 ? "无" : string.Join("；", preflight.Failures));
            text.AppendLine();
            text.AppendLine("路由细节（下一跳、出口网卡、TUN 标记、系统代理）见 `timeline.jsonl` 的 `preflightRoute` 行。本工具的 HTTP 传输 `UseProxy=false`。");
        }
        text.AppendLine();

        text.AppendLine("## 七、异常与偏离");
        text.AppendLine();
        if (abnormal.Count == 0)
        {
            text.AppendLine("无。");
        }
        foreach (string item in abnormal)
        {
            text.AppendLine("- " + item);
        }
        text.AppendLine();

        text.AppendLine("## 八、本目录文件");
        text.AppendLine();
        text.AppendLine("- `drill-state.json`：本 run 做过什么；每次改写前的版本都追加在 `state-history.jsonl`。建单、发令、取消单、解除之前先写这里再调用。");
        text.AppendLine("- `commands.jsonl`：每条命令一行（参数、守卫逐项结果、结论），每次 RIoT 写调用发出前另有一行 `phase=SENDING`。");
        text.AppendLine("- `timeline.jsonl`：全部观测（车辆卡片、运动采样、闩锁、订单、站点、预检）。");
        text.AppendLine("- `wire.jsonl`：每个 HTTP 请求的方法、路径、状态码与耗时；不记请求头，API key 不进证据。");
        text.AppendLine("- `SUMMARY.md`：本文件；第三节由现场人员补填。");

        evidence.WriteNewFile(DrillEvidence.SummaryFileName, text.ToString());
        state.SummarizedAt = DateTimeOffset.Now;
        evidence.SaveState(state);

        outcome.Data["stopProven"] = stopProven;
        outcome.Data["sentOnce"] = sentOnce;
        outcome.Data["triggerIntents"] = trigger.Intents;
        outcome.Data["triggerPosts"] = trigger.Posts;
        outcome.Data["cancelOrderIntents"] = cancelOrder.Intents;
        outcome.Data["cancelOrderPosts"] = cancelOrder.Posts;
        outcome.Data["cancelEmergencyIntents"] = cancelEmergency.Intents;
        outcome.Data["cancelEmergencyPosts"] = cancelEmergency.Posts;
        outcome.Data["abnormal"] = abnormal;
        outcome.Lines.Add($"RIoT 侧确实停车: {(stopProven ? "PASS" : "未证实")}");
        outcome.Lines.Add($"8005 只发一次: {(sentOnce ? "PASS" : "FAIL")} (SENDING {trigger.Intents}, POST {trigger.Posts})");
        outcome.Lines.Add($"abnormal items: {abnormal.Count}");
        return outcome.Set("OK", 0, "wrote " + Path.Combine(evidence.Directory, DrillEvidence.SummaryFileName));
    }

    private sealed record CallCount(int Intents, int Posts);

    private static CallCount Count(JsonElement[] commands, JsonElement[] wire, string operation, Func<string, bool> pathMatches) => new(
        commands.Count(entry => Text(entry, "phase") == "SENDING" && Text(entry, "operation") == operation),
        wire.Count(entry => Text(entry, "method") == "POST" && Text(entry, "path") is string path && pathMatches(path)));

    private static List<string> Abnormalities(
        DrillState state,
        JsonElement[] commands,
        JsonElement[] wire,
        CallCount trigger,
        CallCount cancelEmergency,
        CallCount create,
        CallCount cancelOrder)
    {
        List<string> items = [];
        if (state.FakeRiot)
        {
            items.Add("本目录是 FakeRiot 自测，不是现场演练证据。");
        }
        if (state.Preflight?.AcceptedRouteReason is string reason)
        {
            items.Add($"网络预检路由不是直连，以 `--accept-tun` 放行，理由：{reason}");
        }
        if (state.Trigger is { } triggerRecord)
        {
            if (triggerRecord.AllowStationary)
            {
                items.Add("`trigger` 以 `--allow-stationary` 发出：发令时车没有在两站之间行驶。");
            }
            if (triggerRecord.Disposition != "Accepted")
            {
                items.Add($"`triggerEmergency` 调用结果为 `{triggerRecord.Disposition}`（{triggerRecord.Receipt?.FailureCategory ?? "-"}），不是 Accepted。");
            }
            if (!triggerRecord.Latched)
            {
                items.Add($"发令后 {triggerRecord.ObserveSeconds.ToString(CultureInfo.InvariantCulture)} 秒观察窗内没有读到闩锁（`CAN_RECOVER`／`CAN_NOT_RECOVER`）。");
            }
            if (!triggerRecord.Stopped)
            {
                items.Add("发令后观察窗内没有得到停稳证据（连续 3 个静止采样、跨度 1 秒）。");
            }
            if (triggerRecord.Stopped && triggerRecord.StopStreakProductReadingNotMoving == false)
            {
                items.Add("停稳那段采样的 `movementState` 不在产品 `ReadMotion` 认可的「未移动」集合（`MT_FINISHED`／`MT_PAUSED`）里：产品路径不会把它读成已停住。");
            }
            if (triggerRecord.ReadFailures > 0)
            {
                items.Add($"发令后观察期间有 {triggerRecord.ReadFailures.ToString(CultureInfo.InvariantCulture)} 次读数失败。");
            }
            if (state.CancelOrder is null && state.Order is not null)
            {
                items.Add("发令后本 run 没有取消演练单。");
            }
        }
        else if (state.Order is not null)
        {
            items.Add("建了演练单但本 run 没有发令。");
        }
        if (state.CanNotRecoverObserved)
        {
            items.Add("观察到 `CAN_NOT_RECOVER`：按白名单 1.5 节不调用 `cancelEmergency`，转 RIoT 人工处理。");
        }
        if (state.CancelOrder is { } cancel)
        {
            if (cancel.Disposition is not ("Accepted" or "NOT_SENT_ALREADY_TERMINAL"))
            {
                items.Add($"`CMD_ORDER_CANCEL` 调用结果为 `{cancel.Disposition}`（{cancel.Receipt?.FailureCategory ?? "-"}），不是 Accepted。");
            }
            if (!cancel.TerminalObserved)
            {
                items.Add("`CMD_ORDER_CANCEL` 之后没有读到订单进入终态：按裁定停下回报用户，不改用先解除的顺序。");
            }
        }
        if (state.Trigger is not null && state.Release is null && !state.CanNotRecoverObserved)
        {
            items.Add("发令后本 run 没有解除急停。");
        }
        if (state.Release is { } release)
        {
            if (state.CancelOrder is null || release.AttemptedAt < state.CancelOrder.AttemptedAt)
            {
                items.Add("解除急停早于取消演练单，与裁定的收尾顺序不符。");
            }
            if (!release.OkObserved)
            {
                items.Add($"`cancelEmergency` 结果 `{release.Disposition}`，观察窗内没有读到 `emergencyState=OK`（最后读到 `{release.LastLatch ?? "未读到"}`）。");
            }
        }
        if (trigger.Intents > 1 || trigger.Posts > 1 || trigger.Intents != trigger.Posts)
        {
            items.Add($"`triggerEmergency` 计数异常：SENDING {trigger.Intents.ToString(CultureInfo.InvariantCulture)} 条，POST {trigger.Posts.ToString(CultureInfo.InvariantCulture)} 次。");
        }
        if (cancelEmergency.Intents > 1 || cancelEmergency.Posts > 1 || cancelEmergency.Intents != cancelEmergency.Posts)
        {
            items.Add($"`cancelEmergency` 计数异常：SENDING {cancelEmergency.Intents.ToString(CultureInfo.InvariantCulture)} 条，POST {cancelEmergency.Posts.ToString(CultureInfo.InvariantCulture)} 次。");
        }
        if (create.Intents > 1 || create.Posts > 1 || cancelOrder.Intents > 1 || cancelOrder.Posts > 1)
        {
            items.Add("建单或订单命令的发出次数多于一次。");
        }
        foreach (JsonElement entry in commands)
        {
            string? outcome = Text(entry, "outcome");
            if (outcome is "REFUSED" or "NOT_CONFIRMED" or "WINDOW_MISSED" or "PREFLIGHT_FAILED")
            {
                items.Add($"{Text(entry, "at")} `{Text(entry, "command")}` → {outcome}：{Text(entry, "message")}");
            }
        }
        int failedRequests = wire.Count(entry =>
            Text(entry, "error") is not null ||
            (entry.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() >= 400));
        if (failedRequests > 0)
        {
            items.Add($"`wire.jsonl` 中有 {failedRequests.ToString(CultureInfo.InvariantCulture)} 个请求出错或返回 HTTP 4xx/5xx。");
        }
        return items;
    }

    private static string StopBasis(TriggerRecord? trigger)
    {
        if (trigger is null)
        {
            return "本 run 没有发令。";
        }
        string latch = trigger.Latched
            ? $"发令后 {Motion.Number(trigger.MsToLatch)} ms 读到 `emergencyState={trigger.LatchState}`"
            : "观察窗内没有读到闩锁";
        string stop = trigger.Stopped
            ? $"发令后 {Motion.Number(trigger.MsToStop)} ms 起连续静止采样（结束时连续 {trigger.StopStreak.ToString(CultureInfo.InvariantCulture)} 个；产品读数 NotMoving：{(trigger.StopStreakProductReadingNotMoving == true ? "是" : "否")}）"
            : "观察窗内没有停稳证据";
        string before = trigger.PreSample is null
            ? "发令前采样缺失"
            : $"发令前 `speed={Motion.Number(trigger.PreSample.Speed)}`、`movementState={trigger.PreSample.MovementState ?? "-"}`、`currentStationId={Motion.Number(trigger.PreSample.CurrentStationId)}`";
        return $"{before}；调用结果 `{trigger.Disposition}`；{latch}；{stop}；共 {trigger.Samples.ToString(CultureInfo.InvariantCulture)} 个采样。";
    }

    private static string CancelVerdict(DrillState state) => state.CancelOrder switch
    {
        null => state.Order is null ? "不适用（未建单）" : "未做",
        { TerminalObserved: true } => "**PASS**",
        _ => "**未证实**"
    };

    private static string CancelBasis(DrillState state) => state.CancelOrder is not { } cancel
        ? "本 run 没有取消演练单。"
        : cancel.AlreadyTerminal
            ? $"取消时闩锁 `{cancel.LatchAtCancel ?? "-"}`；订单 {Code(cancel.OrderId)} 已是终态（`orderState={Motion.Number(cancel.OrderStateAfter)}`），没有发出命令。"
            : $"取消时闩锁 `{cancel.LatchAtCancel ?? "-"}`、车辆停稳；`CMD_ORDER_CANCEL` {Code(cancel.OrderId)} 调用结果 `{cancel.Disposition}`；回读 `orderState={Motion.Number(cancel.OrderStateAfter)}`，终态：{(cancel.TerminalObserved ? "是" : "否")}。";

    private static string ReleaseVerdict(DrillState state) => state.Release switch
    {
        null when state.CanNotRecoverObserved => "不适用（`CAN_NOT_RECOVER`，转 RIoT 人工）",
        null => "未做",
        { OkObserved: true } => "**PASS**",
        _ => "**未证实**"
    };

    private static string ReleaseBasis(DrillState state) => state.Release is not { } release
        ? "本 run 没有调用 `cancelEmergency`。"
        : $"解除前演练单已是终态（`orderState={Motion.Number(release.OrderStateAtRelease)}`）；现场确认人 {release.FieldConfirmedBy}（`{release.FieldConfirmation}`）；解除前闩锁 `{release.PreLatch ?? "-"}`；调用结果 `{release.Disposition}`；" +
          (release.OkObserved ? $"发出后 {Motion.Number(release.MsToOk)} ms 读到 `emergencyState=OK`。" : $"没有读到 OK，最后 `{release.LastLatch ?? "未读到"}`。");

    private static void Row(StringBuilder text, params string[] cells) =>
        text.AppendLine("| " + string.Join(" | ", cells.Select(Cell)) + " |");

    private static void CountRow(StringBuilder text, string name, CallCount count) =>
        Row(text, name, count.Intents.ToString(CultureInfo.InvariantCulture), count.Posts.ToString(CultureInfo.InvariantCulture));

    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Code(string value) => "`" + value + "`";

    private static string Time(DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
