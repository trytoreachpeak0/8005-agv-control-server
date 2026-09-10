using System.Globalization;
using System.Text.Json;
using ControlServer.Domain;

namespace ControlServer.Host.Transport;

/// <summary>
/// 协议 v2 消息 9 <c>OnboardAlarmSnapshot</c> 的线上形状与服务端告警模型之间的映射。
/// </summary>
/// <remarks>
/// <para>
/// 单独一个文件，不塞进 <see cref="OnboardMessageProcessor"/>：那里放的是「收到一条消息之后做什么」，
/// 这里放的是「线上那份 JSON 是什么意思」。#16 定的业务语义一个字不改，本文件只负责把 <c>AlarmEntry</c>
/// 翻译成 <see cref="OnboardAlarmEntry"/>。
/// </para>
/// <para>
/// <b><c>subjectType</c> 是开放字符串，所以未知取值归 <see cref="OnboardAlarmScope.Fleet"/>。</b>
/// 这是**朝可见方向**倒的：Fleet 是进看板的那一类，认不出主体类型的告警会被看见，而不是悄悄消失。
/// 反过来把未知归给三个车载类之一，等于让一条服务端读不懂的告警只出现在车上——两边加起来就不再是
/// 全集了。
/// </para>
/// <para>
/// <b>告警码全程是字符串。</b>它一次都不经过 <c>ErrorCode</c>：告警是开放集合，协议错误码是封闭
/// enum，有架构测试断言两者无交集。
/// </para>
/// </remarks>
internal static class OnboardAlarmSnapshotWire
{
    /// <summary>看板与车载两侧分账用的主体类型。协议把它留成开放字符串，这里是服务端认得的那几个。</summary>
    internal const string VehicleSubject = "VEHICLE";

    internal const string SlotSubject = "SLOT";

    internal const string StationSubject = "STATION";

    internal const string DemandSubject = "DEMAND";

    internal const string SlotOperationSubject = "SLOT_OPERATION";

    /// <summary>快照的修订号——ack 里回给车的 <c>appliedRevision</c> 就是它。</summary>
    internal static long Revision(JsonElement payload) =>
        payload.GetProperty("alarmSnapshotRevision").GetInt64();

    /// <summary>把线上那一份翻成服务端的快照视图。</summary>
    internal static OnboardAlarmSnapshotView Read(string agvId, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);

        return new OnboardAlarmSnapshotView(
            agvId,
            Revision(payload),
            payload.GetProperty("observedAt").GetDateTimeOffset(),
            [.. payload.GetProperty("alarms").EnumerateArray().Select(ReadAlarm)]);
    }

    private static OnboardAlarmEntry ReadAlarm(JsonElement alarm)
    {
        string subjectType = Required(alarm, "subjectType");
        string? subjectId = Optional(alarm, "subjectId");
        return new OnboardAlarmEntry(
            Required(alarm, "code"),
            Required(alarm, "severity"),
            alarm.GetProperty("raisedAt").GetDateTimeOffset(),
            Scope(subjectType),
            // displayMessage 可以为空，那时候码本身就是能说的全部——不编一句话出来。
            Optional(alarm, "displayMessage") ?? Required(alarm, "code"),
            DemandId: subjectType == DemandSubject ? subjectId : null,
            StationId: subjectType == StationSubject ? subjectId : null,
            SlotOperationAttemptId: subjectType == SlotOperationSubject ? subjectId : null,
            PhysicalSlotNumber: subjectType == SlotSubject ? SlotNumber(subjectId) : null,
            AlarmId: Required(alarm, "alarmId"));
    }

    private static OnboardAlarmScope Scope(string subjectType) => subjectType switch
    {
        VehicleSubject or SlotSubject => OnboardAlarmScope.CurrentVehicle,
        StationSubject => OnboardAlarmScope.CurrentStop,
        DemandSubject or SlotOperationSubject => OnboardAlarmScope.CurrentOperation,
        _ => OnboardAlarmScope.Fleet
    };

    /// <summary>
    /// 仓位号认不出来时归 <see langword="null"/>，不抛。
    /// </summary>
    /// <remarks>
    /// 一条告警的主体 id 写歪了，代价应当是「这条告警少一个仓位号」，不是「整份快照被拒、这台车在看板
    /// 上变成失联」——后者拿一个显示细节换掉了 REQ-0269 的失联直述。
    /// </remarks>
    private static int? SlotNumber(string? subjectId) =>
        int.TryParse(subjectId, NumberStyles.None, CultureInfo.InvariantCulture, out int slot)
            ? slot
            : null;

    private static string Required(JsonElement element, string name) =>
        element.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"OnboardAlarmSnapshot alarm '{name}' must be a string.");

    private static string? Optional(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null
            ? null
            : element.GetProperty(name).GetString();
}
