using System.Collections.Concurrent;

namespace ControlServer.Host.Runtime.IdleReturn;

/// <summary>
/// 每一趟空闲返回的承诺连续物化失败了几轮（control-server#390 审查 L3）。每轮一次失败只记一条 Warning，失败多少轮都一个样子；
/// 连续到 <see cref="EscalateAfter"/> 轮时引擎升级为一条 Error，只一次。
/// </summary>
/// <remarks>
/// 宿主里是单例：引擎按轮次新建（每轮一个作用域），计数要跨轮次活着；也不放静态字段，并行的测试会互相清掉。
/// </remarks>
public sealed class IdleReturnMaterializationFailures
{
    /// <summary>连续失败到这一轮升级。</summary>
    public const int EscalateAfter = 5;

    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>记一次失败，答连续失败的轮数（含这一次）。</summary>
    public int Failed(string journeyId) => _counts.AddOrUpdate(journeyId, 1, (_, count) => count + 1);

    /// <summary>这一趟物化了（或作废了）：清零。</summary>
    public void Succeeded(string journeyId) => _counts.TryRemove(journeyId, out _);

    /// <summary>只留还在待物化的承诺的计数：承诺已被别的路径释放的，不再记着。</summary>
    public void RetainOnly(IReadOnlySet<string> journeyIds)
    {
        ArgumentNullException.ThrowIfNull(journeyIds);
        foreach (string journeyId in _counts.Keys.Where(key => !journeyIds.Contains(key)))
        {
            _counts.TryRemove(journeyId, out _);
        }
    }
}
