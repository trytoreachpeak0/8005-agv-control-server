namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 人工清桩确认与现场确认充不上经入站处理器进来时（control-server#452）：写锁里重读的库事实与锁外观察 RIoT 时的不一样了，这一次什么也没写。
/// 入站处理器接住它，让收件箱事务整个回滚，回到锁外重新观察、整次重判；次数用尽时以 <see cref="Exception.Message"/>（与判定自己重试用尽时逐字相同）
/// 抛 <see cref="InvalidOperationException"/>。
/// </summary>
public sealed class FieldConfirmationObservationStaleException(string exhaustedMessage) : Exception(exhaustedMessage);
