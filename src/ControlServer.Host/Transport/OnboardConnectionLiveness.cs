using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Transport;

/// <summary>
/// 一条 Onboard 连接的静默计时（ADR-cross-0027）：距今多久没有从这条连接上收到过合法消息。
/// </summary>
/// <remarks>
/// <para>
/// <b>用本机单调时钟，不用挂钟。</b>ADR-cross-0027 明写存活计时走单调时钟，理由是挂钟会被 NTP 校正、被人
/// 手动改：往前跳一步，一条刚刚还活着的会话立刻"静默了一小时"；往后跳，一条死掉的连接永远不到期。
/// <see cref="TimeProvider.GetTimestamp"/> 不受这两件事影响。
/// </para>
/// <para>
/// <b>刷新的时机是「这条消息被处理完了」，不是「读到了一行字节」。</b>ADR 说的是"合法协议消息"，而一行读回来
/// 时还不知道它合不合法——信封校验、会话代次核对都在处理里。所以调用方在
/// <c>OnboardMessageProcessor.ProcessAsync</c> 正常返回之后才调 <see cref="Refresh"/>；抛异常的那一条不刷新，
/// 而它本来也会让连接就此结束。
/// </para>
/// <para>
/// <b>阈值与看板、旅程阻断同源</b>（<see cref="SessionLiveness.Timeout"/>），但判定时钟不同源：那两处按数据库
/// 里的收件时间与当前 UTC 相减。这是有意的，不是疏忽——两者只在系统时钟被调整时分歧，而那时单调的这个是对的。
/// 细节见 <see cref="SessionLiveness"/> 的类注释。
/// </para>
/// </remarks>
internal sealed class OnboardConnectionLiveness
{
    private readonly TimeProvider _clock;
    private long _lastInboundTimestamp;

    internal OnboardConnectionLiveness(TimeProvider clock, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), timeout, "The Onboard liveness timeout must be positive.");
        }
        _clock = clock;
        Timeout = timeout;
        Refresh();
    }

    /// <summary>多久没有合法入站就算失联。</summary>
    internal TimeSpan Timeout { get; }

    /// <summary>距今多久没有收到过合法入站。</summary>
    internal TimeSpan Silence => _clock.GetElapsedTime(_lastInboundTimestamp);

    /// <summary>这条连接现在算不算失联。边界取「满」：正好到阈值已经算。</summary>
    internal bool Expired => Silence >= Timeout;

    /// <summary>还能静默多久才到期；已经到期就是 <see cref="TimeSpan.Zero"/>。</summary>
    internal TimeSpan Remaining
    {
        get
        {
            TimeSpan left = Timeout - Silence;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>收到了一条合法入站：计时从现在重新开始。</summary>
    internal void Refresh() => _lastInboundTimestamp = _clock.GetTimestamp();
}
