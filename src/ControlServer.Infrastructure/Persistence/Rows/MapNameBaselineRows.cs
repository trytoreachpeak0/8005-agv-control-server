namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 一张图的地图名基线（control-server#186，REQ-0341）：每个 <c>mapId</c> 一行，记首次读到的名称。之后读到不同的名称记在
/// <see cref="PendingName"/>，该图生效绑定的任务类型因此暂停；现场用 FieldOps 接受之后，它才成为新的 <see cref="Name"/>。
/// 与 <c>JourneyRuntime:mapIdentity</c> 无关，两者互不比较。
/// </summary>
public sealed class MapNameBaselineRow
{
    public int MapId { get; set; }

    /// <summary>基线名称：首次读到的，或最近一次被接受的。</summary>
    public required string Name { get; set; }

    /// <summary>这一行建立的时刻，即首次读到这张图的时刻；接受新名称不改它。</summary>
    public DateTimeOffset EstablishedAt { get; set; }

    /// <summary>读到的、与基线不同且尚未接受的名称；没有待接受的改名时为空。</summary>
    public string? PendingName { get; set; }

    /// <summary>第一次读到 <see cref="PendingName"/> 这个名称的时刻。</summary>
    public DateTimeOffset? PendingSince { get; set; }

    /// <summary>最近一次接受新名称的时刻与操作者（<c>fieldops:accept-map-name:</c> 加审计记录号）。</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    public string? AcceptedBy { get; set; }
}
