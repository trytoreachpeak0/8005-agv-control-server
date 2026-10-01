using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>现场人员角色名单在哪（批次9-08，control-server#406）。</summary>
/// <remarks>
/// <para>
/// <b>为什么在服务端</b>（用户 2026-09-29 定，经调度转达；走票面第一步第 2 条的甲路）：协议的 <c>OperatorContext</c> 只带
/// <c>operatorId</c>、<c>verificationMethod</c>、<c>verifiedAt</c>，没有角色，角色不上线路；服务端按 <c>operatorId</c> 查这份名单。
/// 批次9-12（<c>REQ-0176</c> 的 R-11）照同一份名单查。
/// </para>
/// <para>
/// <b>只读、空即一律拒</b>：服务端从不写它。没配路径、文件不在、读不出、名单为空，都是「没有任何人有这个权限」——清桩确认一律
/// <c>REJECTED</c>，不放行。每次判定现读一次，改了名单不用重启。
/// </para>
/// </remarks>
public sealed class FieldOperatorRoleOptions
{
    public const string SectionName = "FieldOperatorRoles";

    /// <summary>名单文件路径；相对路径按程序目录解析。空即没有名单。</summary>
    public string? Path { get; set; }

    /// <summary>
    /// 部署方声明：这里的车载端开着人工清桩入口（onboard-hmi#229：车载端 <c>wireToGate.recoveryResumeEnabled</c> 为真，并配了维护人员凭据）。
    /// 默认假。
    /// </summary>
    /// <remarks>
    /// 协议的 <c>CapabilitySnapshot</c> 没有任何字段说车载端有没有这个入口，服务端从线路上看不到，所以只能由部署方在这里声明；声明错了，
    /// 「已确认充不上」之后车载端点不出确认，出口只剩 Host 入口（<see cref="StationClearanceExit"/>）。
    /// </remarks>
    public bool OnboardClearanceEntryDeclared { get; set; }
}

/// <summary>
/// 现场人员角色名单：一个 JSON 文件，<c>{"operators":[{"operatorId":"…","roles":["R-11"]}]}</c>。角色码照
/// <c>8005-agv-program</c> 的 <c>requirement-documents/01-stakeholders/stakeholders-and-user-classes.md</c>：R-11 设备/电气维护人员，
/// R-13 AGV 运维/调度管理员。
/// </summary>
public sealed class FieldOperatorRoleRoster(IOptions<FieldOperatorRoleOptions> options)
{
    /// <summary>设备/电气维护人员。</summary>
    public const string EquipmentMaintenance = "R-11";

    /// <summary>AGV 运维/调度管理员。</summary>
    public const string AgvOperationsAdministrator = "R-13";

    /// <summary>「人工清桩」权限的初始授予（<c>REQ-0179</c>）。</summary>
    public static IReadOnlyList<string> StationClearanceRoles { get; } = [EquipmentMaintenance, AgvOperationsAdministrator];

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 这个人在 <paramref name="allowed"/> 里的第一个角色（按 <paramref name="allowed"/> 的先后）；不在名单里、名单读不到、或没有这些角色答空。
    /// </summary>
    public string? GrantedRole(string? operatorId, IReadOnlyList<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            return null;
        }
        string trimmed = operatorId.Trim();
        string[] roles =
        [
            .. Entries()
                .Where(entry => string.Equals(entry.OperatorId?.Trim(), trimmed, StringComparison.Ordinal))
                .SelectMany(entry => entry.Roles ?? []),
        ];
        return allowed.FirstOrDefault(role => roles.Contains(role, StringComparer.Ordinal));
    }

    /// <summary>名单此刻可读，且至少有一个具名的人持有 <paramref name="allowed"/> 之一。没配、读不到、为空都答假。</summary>
    public bool AnyoneHolds(IReadOnlyList<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        return Entries().Any(entry =>
            !string.IsNullOrWhiteSpace(entry.OperatorId) &&
            (entry.Roles ?? []).Any(role => allowed.Contains(role, StringComparer.Ordinal)));
    }

    private IReadOnlyList<RosterEntry> Entries()
    {
        string? path = options.Value.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }
        string full = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(AppContext.BaseDirectory, path);
        try
        {
            return JsonSerializer.Deserialize<RosterDocument>(File.ReadAllText(full), ReadOptions)?.Operators ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable is "nobody holds the permission", never "everybody does".
            return [];
        }
    }

    private sealed record RosterDocument(IReadOnlyList<RosterEntry>? Operators);

    private sealed record RosterEntry(string? OperatorId, IReadOnlyList<string>? Roles);
}
