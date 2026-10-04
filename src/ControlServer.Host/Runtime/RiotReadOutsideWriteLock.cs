using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 读 RIoT 之前的守卫（control-server#452）：这个库的写事务是 <c>BEGIN IMMEDIATE</c>，开事务那一刻就拿到整库写锁，直到提交。在事务里读 RIoT，
/// RIoT 慢多久，引擎那一轮与别的车的入站就被挡多久，等满 busy timeout 就以 <c>SQLITE_BUSY</c> 失败——一辆车的一次确认拖住整个车队。
/// </summary>
/// <remarks>
/// 只在调用点守，不在 RIoT 适配器里守：适配器是单例，不知道此刻这条流程用的是哪个 <see cref="DbContext"/>。所以每一处在判定前读 RIoT 的地方，
/// 读之前调一次 <see cref="Ensure"/>：等待点到点人工收尾（control-server#447）、人工清桩确认与现场确认充不上的观察（本票）。
/// </remarks>
public static class RiotReadOutsideWriteLock
{
    /// <summary>这个上下文此刻开着事务（自己的，或调用方的、入站处理器的收件箱事务）就抛：这里不许读 RIoT。</summary>
    public static void Ensure(DbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("RIoT is read outside the write lock (control-server#452).");
        }
    }
}
