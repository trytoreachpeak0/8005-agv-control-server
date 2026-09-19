using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>
/// FieldOps 手里的站点目录算不算「当前新鲜」（control-server#161）。
/// </summary>
/// <remarks>
/// <para>
/// FieldOps 是进程外直连数据库的工具，够不到 RIoT；站点清单本身也不落库，库里只有服务端最近一次完整确认留下的一行
/// <c>MapStationCatalogStates</c>（确认时刻、批准的新鲜度参数、由内容哈希导出的目录修订）。所以运维带一份目录进来，这里做两件事：
/// 按服务端同一个规则算它的指纹与修订，要求与库里那行的修订相等；再按那一行自己记下的批准参数判它新鲜不新鲜。
/// 两条都过，才说明「这份清单就是服务端刚刚完整确认过的那一份」。任何一条不过，都不激活、不解除。
/// </para>
/// <para>
/// 指纹规则与 <c>HttpRiotMovementGateway.ReadMapStationsAsync</c> 一致，修订与 <c>CatalogAvailabilityAccess.RevisionOf</c> 一致；
/// 两处都有测试钉住，改一边另一边的测试会红。
/// </para>
/// </remarks>
public static class TaskTypeStationCatalogEvidence
{
    /// <summary>按站点 id 排序后 <c>mapId\tstationId\tstationName</c> 逐行拼接，UTF-8 的 SHA-256 小写十六进制。</summary>
    public static string Fingerprint(int mapId, IEnumerable<RiotMapStation> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        string canonical = string.Join('\n', stations
            .OrderBy(station => station.StationId)
            .Select(station => string.Create(
                CultureInfo.InvariantCulture, $"{mapId}\t{station.StationId}\t{station.StationName}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>指纹前 8 字节按大端读成非负 long。</summary>
    public static long RevisionOf(string contentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        Span<byte> first8 = stackalloc byte[8];
        for (int index = 0; index < 8; index++)
        {
            first8[index] = byte.Parse(
                contentSha256.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return BinaryPrimitives.ReadInt64BigEndian(first8) & long.MaxValue;
    }

    /// <summary>运维带来的一份目录，指纹按上面的规则现算。</summary>
    public static RiotMapStationCatalogSnapshot Supplied(
        int mapId,
        IReadOnlyList<RiotMapStation> stations,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(stations);
        return new RiotMapStationCatalogSnapshot(
            mapId, observedAt, Fingerprint(mapId, stations), [.. stations.OrderBy(station => station.StationId)]);
    }

    /// <summary>
    /// 这份目录是不是 <paramref name="mapId"/> 当前新鲜、且就是服务端最近一次完整确认过的那一份。
    /// </summary>
    /// <returns>每个不满足的理由一条，原因码都是 <see cref="TaskTypeStationReasonCodes.BindingCatalogNotFresh"/>；为空表示新鲜。</returns>
    public static IReadOnlyList<TaskTypeStationViolation> JudgeFreshness(
        int mapId,
        RiotMapStationCatalogSnapshot? supplied,
        MapStationCatalogAvailability? state,
        DateTimeOffset now)
    {
        List<TaskTypeStationViolation> violations = [];
        if (supplied is null)
        {
            violations.Add(NotFresh($"No station catalog was supplied for Map {mapId}."));
        }
        if (state is null)
        {
            violations.Add(NotFresh($"The server has never recorded a complete station catalog confirmation for Map {mapId}."));
            return violations;
        }
        if (state.State == MapStationCatalogState.BuildIncompatible)
        {
            violations.Add(NotFresh($"Map {mapId}'s catalog state is {state.State}; the catalog belongs to a different build."));
        }
        if (state.ApprovedMaxUnconfirmedSeconds is not int maxUnconfirmed || state.LastCompleteConfirmationAt is not DateTimeOffset confirmedAt)
        {
            violations.Add(NotFresh($"Map {mapId} has no approved freshness window or no complete confirmation on record."));
        }
        else if (now - confirmedAt > TimeSpan.FromSeconds(maxUnconfirmed))
        {
            violations.Add(NotFresh(
                $"Map {mapId}'s last complete catalog confirmation was at {confirmedAt:O}, more than the approved {maxUnconfirmed} s before {now:O}."));
        }
        if (supplied is not null)
        {
            long suppliedRevision = RevisionOf(supplied.ContentSha256);
            if (supplied.MapId != mapId || suppliedRevision != state.CatalogRevision)
            {
                violations.Add(NotFresh(
                    $"The supplied catalog (Map {supplied.MapId}, revision {suppliedRevision}) is not the one the server last confirmed for Map {mapId} (revision {state.CatalogRevision})."));
            }
        }
        return violations;
    }

    private static TaskTypeStationViolation NotFresh(FormattableString detail) =>
        new(TaskTypeStationReasonCodes.BindingCatalogNotFresh, null, null, detail.ToString(CultureInfo.InvariantCulture));
}
