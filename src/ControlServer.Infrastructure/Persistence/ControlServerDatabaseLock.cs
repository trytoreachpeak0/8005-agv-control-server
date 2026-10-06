using System.Diagnostics;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 与一个库文件绑定的独占锁（control-server#473）：运行中的服务端整个进程期间持有它，FieldOps 直接写库前拿同一把，拿到才写。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么探测不够。</b>FieldOps 写库前探一次服务端（control-server#459），只能说明探的那一刻服务端停着；探完、写库前服务端恰好被
/// 启动起来，探测拦不住。锁把「服务端在用这个库」从一次观测变成一个同刻只有一方能成立的事实。
/// </para>
/// <para>
/// <b>锁的是库文件，不是机器。</b>锁文件就放在库旁边（<c>&lt;库文件&gt;.instance-lock</c>）。并行期 v2 实例与 MVP 在同一台机器上、用不同的
/// 库目录，一方的锁挡不住另一方。
/// </para>
/// <para>
/// <b>锁是 OS 的，不是「文件在不在」。</b>持有就是以 <see cref="FileShare.None"/> 打开锁文件并一直不关；进程怎么结束（正常退出、崩溃、
/// 被杀）都由操作系统关掉句柄、锁随之释放。锁文件本身从不删除，留在那里不说明任何事——删它也解不了锁，句柄还在别人手里。
/// </para>
/// </remarks>
public sealed class ControlServerDatabaseLock : IDisposable
{
    /// <summary>锁文件名在库文件名之后加的后缀。</summary>
    public const string LockFileSuffix = ".instance-lock";

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION: someone else has the file open in a way that excludes us.
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    private readonly FileStream _handle;

    private ControlServerDatabaseLock(string databasePath, FileStream handle)
    {
        DatabasePath = databasePath;
        _handle = handle;
    }

    /// <summary>锁住的库文件，绝对路径。</summary>
    public string DatabasePath { get; }

    /// <summary>库文件 <paramref name="databasePath"/> 的锁文件，绝对路径。</summary>
    public static string LockFileFor(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return Path.GetFullPath(databasePath) + LockFileSuffix;
    }

    /// <summary>试一次：拿到了返回锁，别的进程（或本进程另一处）正持有时返回 <see langword="null"/>。库所在的目录必须已经存在。</summary>
    public static ControlServerDatabaseLock? TryAcquire(string databasePath)
    {
        string database = Path.GetFullPath(databasePath);
        FileStream handle;
        try
        {
            handle = new FileStream(database + LockFileSuffix, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException held) when (held.HResult is SharingViolation or LockViolation)
        {
            return null;
        }
        // Who holds it, for a person looking at the directory; nobody reads it back, and it cannot be read while held.
        try
        {
            using Process self = Process.GetCurrentProcess();
            byte[] holder = System.Text.Encoding.UTF8.GetBytes(
                $"pid={Environment.ProcessId} process={self.ProcessName} since={DateTimeOffset.Now:O}{Environment.NewLine}");
            handle.SetLength(0);
            handle.Write(holder);
            handle.Flush();
        }
        catch (IOException)
        {
            // The lock is the open handle, not its content.
        }
        return new ControlServerDatabaseLock(database, handle);
    }

    /// <summary>
    /// 服务端启动用：拿不到就每隔 <paramref name="retryInterval"/> 再试，最多等 <paramref name="wait"/>，仍拿不到返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 等一小会儿是为了重启：服务管理器报「已停止」时上一个进程可能还在收尾、句柄还没关；FieldOps 一次写库也只要几秒。真有另一个实例
    /// 在用这个库，等多久都拿不到，那时拒绝启动。
    /// </remarks>
    public static async Task<ControlServerDatabaseLock?> AcquireAsync(
        string databasePath,
        TimeSpan wait,
        TimeSpan retryInterval,
        Action? waiting,
        CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        bool announced = false;
        while (true)
        {
            ControlServerDatabaseLock? acquired = TryAcquire(databasePath);
            if (acquired is not null || clock.Elapsed >= wait)
            {
                return acquired;
            }
            if (!announced)
            {
                waiting?.Invoke();
                announced = true;
            }
            await Task.Delay(retryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>服务端拒绝启动时给维护人员看的那段话：哪个库目录、哪个库、怎么办。</summary>
    public static string ServerRefusal(string databasePath)
    {
        string database = Path.GetFullPath(databasePath);
        return $"服务端拒绝启动：库目录 {Path.GetDirectoryName(database)} 里的库 {Path.GetFileName(database)} 正被另一个进程占用"
            + "（另一个服务端实例，或正在直接写库的 FieldOps）。两个进程同时用一个库会互相覆盖状态。"
            + "请确认用这个库目录的服务端服务或进程都已经停下（服务管理器、任务管理器里看 ControlServer.Host）；FieldOps 写库一般几秒内结束。"
            + $"确认后再启动。不要删除锁文件 {Path.GetFileName(database + LockFileSuffix)}：锁随占用它的进程退出自动释放，删文件解不了锁。";
    }

    /// <summary>FieldOps 拒绝直接写库时给维护人员看的那段话。</summary>
    public static string FieldOpsRefusal(string databasePath)
    {
        string database = Path.GetFullPath(databasePath);
        return $"没有写库：库目录 {Path.GetDirectoryName(database)} 里的库 {Path.GetFileName(database)} 正被另一个进程占用，"
            + "多半是服务端正在运行（也可能是另一个 FieldOps 正在写）。服务端在运行时请改用 --server，经服务端执行并核对 RIoT；"
            + "要直接写库，先停止用这个库目录的服务端，再重试。";
    }

    public void Dispose() => _handle.Dispose();
}
