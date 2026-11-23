using System.Linq.Expressions;
using ControlServer.Application;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 按 <see cref="TransportDemandIdentity"/>（业务键 + 程）判身份的三张表的行：受理（唯一索引）、抑制与完成事实（主键）。control-server#911。
/// </summary>
public interface ITransportDemandIdentityRow
{
    string TransportDemandKey { get; }

    string TransportLeg { get; }
}

/// <summary>
/// 三张身份表上「是不是同一件活」的唯一写法。产品源码里不再直接拿 <c>TransportDemandKey</c> 判这三张表的身份——
/// <c>TransportDemandIdentityArchitectureTests</c> 守着这一条，因为漏改一处的后果是运行时的身份冲突，而不是编译错。
/// </summary>
public static class TransportDemandIdentityQueries
{
    public static TransportDemandIdentity Identity(this ITransportDemandIdentityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(row.TransportDemandKey, row.TransportLeg);
    }

    /// <summary>与 <paramref name="identity"/> 同一身份的行。</summary>
    public static IQueryable<TRow> WhereIdentity<TRow>(this IQueryable<TRow> rows, TransportDemandIdentity identity)
        where TRow : class, ITransportDemandIdentityRow
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(Matching<TRow>(identity));
    }

    /// <summary>与 <paramref name="identity"/> 同一身份的行，内存里（变更跟踪器的 <c>Local</c>）。</summary>
    public static IEnumerable<TRow> WhereIdentity<TRow>(this IEnumerable<TRow> rows, TransportDemandIdentity identity)
        where TRow : class, ITransportDemandIdentityRow
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(row => row.Identity() == identity);
    }

    /// <summary>
    /// 受理行里 <c>DemandId</c> 是这一条、或身份是这一个的行：受理与追加查重用。按身份撞上的是另一条需求时由调用方判冲突。
    /// </summary>
    public static IQueryable<AcceptedDemandRow> WhereDemandIdOrIdentity(
        this IQueryable<AcceptedDemandRow> rows, string demandId, TransportDemandIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        string key = identity.TransportDemandKey;
        string leg = identity.TransportLeg;
        return rows.Where(row => row.DemandId == demandId || (row.TransportDemandKey == key && row.TransportLeg == leg));
    }

    /// <summary>
    /// 业务键落在 <paramref name="keys"/> 里的行：一次读出一批候选的同键行，再在内存里按 <see cref="Identity"/> 比。
    /// 只是缩小读的范围，判身份仍要按 <see cref="Identity"/>。
    /// </summary>
    public static IQueryable<TRow> WhereKeyAmong<TRow>(this IQueryable<TRow> rows, IReadOnlyCollection<string> keys)
        where TRow : class, ITransportDemandIdentityRow
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(keys);
        KeyList values = new([.. keys]);
        ParameterExpression row = Expression.Parameter(typeof(TRow), "row");
        Expression contains = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)],
            Expression.Field(Expression.Constant(values), nameof(KeyList.Keys)),
            Expression.Property(row, nameof(ITransportDemandIdentityRow.TransportDemandKey)));
        return rows.Where(Expression.Lambda<Func<TRow, bool>>(contains, row));
    }

    private sealed class KeyList(string[] keys)
    {
        public readonly string[] Keys = keys;
    }

    // 按具体类型的属性建表达式，翻译成两列相等；比较值经闭包对象的成员取，EF 把它当参数，不把每个值内联成一条新 SQL。
    private static Expression<Func<TRow, bool>> Matching<TRow>(TransportDemandIdentity identity)
        where TRow : class, ITransportDemandIdentityRow
    {
        IdentityValues values = new(identity.TransportDemandKey, identity.TransportLeg);
        ParameterExpression row = Expression.Parameter(typeof(TRow), "row");
        ConstantExpression holder = Expression.Constant(values);
        Expression key = Expression.Equal(
            Expression.Property(row, nameof(ITransportDemandIdentityRow.TransportDemandKey)),
            Expression.Field(holder, nameof(IdentityValues.Key)));
        Expression leg = Expression.Equal(
            Expression.Property(row, nameof(ITransportDemandIdentityRow.TransportLeg)),
            Expression.Field(holder, nameof(IdentityValues.Leg)));
        return Expression.Lambda<Func<TRow, bool>>(Expression.AndAlso(key, leg), row);
    }

    private sealed class IdentityValues(string key, string leg)
    {
        public readonly string Key = key;
        public readonly string Leg = leg;
    }
}
